using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Core.Agents;

namespace Harness.Tools;

/// <summary>
/// The file tools: read, list, glob, grep, edit and write. Results are plain text with a one-line header that says how to continue.
/// <c>edit</c> and <c>write</c> refuse to touch an existing file the session has not read, or that changed since it was read.
/// </summary>
public sealed class FileTools(IWorkspace workspace, SessionRuntimeState state)
{
    public const int DefaultReadLimit = 400;
    public const int MaxLineChars = 2_000;
    public const int MaxListEntries = 400;
    public const int MaxGlobResults = 100;
    public const int MaxGrepResults = 200;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Description("Read a text file as numbered lines. Page through long files with offset and limit.")]
    public async Task<string> Read(
        [Description("File path relative to the workspace root")] string path,
        [Description("First line to return (1-based)")] int offset = 1,
        [Description("Maximum number of lines to return")] int limit = DefaultReadLimit,
        CancellationToken cancellationToken = default)
    {
        string full = workspace.Resolve(path);
        if (Directory.Exists(full)) return $"Error: '{workspace.Display(full)}' is a directory. Use list instead.";
        if (!File.Exists(full)) return $"Error: '{workspace.Display(full)}' does not exist.";

        byte[] bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        if (IsBinary(bytes)) return $"{workspace.Display(full)} · binary file, {bytes.Length.ToString("N0", Inv)} bytes · not shown";

        offset = Math.Max(1, offset);
        limit = Math.Clamp(limit, 1, 5_000);
        string hash = Workspace.Hash(bytes);
        int turn = state.Turn;
        if (state.Reads.TryGetValue(full, out FileReadState? previous) && previous.Hash == hash && previous.Offset == offset && previous.Limit == limit)
            return $"{workspace.Display(full)} · lines {offset}–{offset + limit - 1} unchanged since turn {previous.Turn}; the earlier result still applies.";
        state.Reads[full] = new FileReadState(hash, turn, offset, limit);

        string[] lines = SplitLines(Encoding.UTF8.GetString(bytes));
        if (lines.Length == 0) return $"{workspace.Display(full)} · empty file";
        if (offset > lines.Length) return $"{workspace.Display(full)} · {lines.Length.ToString("N0", Inv)} lines · offset {offset} is past the end";

        int last = Math.Min(lines.Length, offset + limit - 1);
        StringBuilder sb = new();
        sb.Append($"{workspace.Display(full)} · lines {offset}–{last} of {lines.Length.ToString("N0", Inv)}");
        sb.Append(last < lines.Length ? $" · next: offset={last + 1}" : " · end of file");
        sb.Append('\n');
        int width = last.ToString(Inv).Length;
        for (int i = offset; i <= last; i++)
        {
            string line = lines[i - 1];
            if (line.Length > MaxLineChars) line = line[..MaxLineChars] + $"… [line clipped, {line.Length.ToString("N0", Inv)} chars]";
            sb.Append(i.ToString(Inv).PadLeft(width)).Append('\t').Append(line).Append('\n');
        }
        return sb.ToString();
    }

    [Description("List a directory as a tree. Honours .gitignore.")]
    public string List(
        [Description("Directory relative to the workspace root")] string path = ".",
        [Description("How many levels deep to go")] int depth = 2)
    {
        string full = workspace.Resolve(path);
        if (!Directory.Exists(full)) return File.Exists(full) ? $"Error: '{workspace.Display(full)}' is a file. Use read instead." : $"Error: '{workspace.Display(full)}' does not exist.";
        depth = Math.Clamp(depth, 1, 8);

        Gitignore ignore = new(workspace.Root);
        StringBuilder body = new();
        int shown = 0, hidden = 0;
        void Walk(string dir, int level, string indent)
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { return; }
            foreach (FileSystemInfo e in entries.OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.Ordinal))
            {
                bool isDir = e is DirectoryInfo;
                if (ignore.IsIgnored(e.FullName, isDir)) continue;
                if (shown >= MaxListEntries) { hidden++; continue; }
                shown++;
                body.Append(indent).Append(e.Name);
                if (isDir) body.Append('/');
                if (e.LinkTarget is not null) body.Append(" -> ").Append(e.LinkTarget);
                body.Append('\n');
                if (isDir && e.LinkTarget is null)
                {
                    if (level < depth) Walk(e.FullName, level + 1, indent + "  ");
                    else if (CountEntries(e.FullName, ignore) is int n and > 0) body.Append(indent).Append("  … ").Append(n.ToString(Inv)).Append(" entries\n");
                }
            }
        }
        Walk(full, 1, "");
        string header = $"{workspace.Display(full)}/ · depth {depth} · {shown} entries" + (hidden > 0 ? $" · {hidden} more not shown (narrow the path or lower depth)" : "");
        return header + "\n" + body;
    }

    [Description("Find files by glob pattern, for example **/*.cs or src/**/Program.cs. Returns paths only, newest first.")]
    public string Glob(
        [Description("Glob pattern relative to path")] string pattern,
        [Description("Directory to search, relative to the workspace root")] string path = ".")
    {
        string full = workspace.Resolve(path);
        if (!Directory.Exists(full)) return $"Error: '{workspace.Display(full)}' is not a directory.";
        string normalized = pattern.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        // A pattern without a slash matches at any depth, like most tools do for "*.cs".
        Regex regex = new("^" + (normalized.Contains('/') ? "" : "(?:.*/)?") + Gitignore.GlobToRegex(normalized) + "$", RegexOptions.CultureInvariant);

        List<FileInfo> matches = [];
        foreach (FileSystemInfo e in new Gitignore(workspace.Root).Walk(full))
            if (e is FileInfo f && regex.IsMatch(Path.GetRelativePath(full, f.FullName).Replace('\\', '/')))
                matches.Add(f);

        if (matches.Count == 0) return $"glob {pattern} in {workspace.Display(full)} · no matches";
        IEnumerable<FileInfo> ordered = matches.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxGlobResults);
        StringBuilder sb = new($"glob {pattern} in {workspace.Display(full)} · {matches.Count.ToString(Inv)} files");
        if (matches.Count > MaxGlobResults) sb.Append($" · showing newest {MaxGlobResults}");
        sb.Append('\n');
        foreach (FileInfo f in ordered) sb.Append(workspace.Display(f.FullName)).Append('\n');
        return sb.ToString();
    }

    [Description("Search file contents with a regular expression. Mode 'files' lists matching files, 'content' shows matching lines, 'count' counts matches per file.")]
    public async Task<string> Grep(
        [Description("Regular expression to search for")] string pattern,
        [Description("File or directory to search, relative to the workspace root")] string path = ".",
        [Description("files | content | count")] string mode = "files",
        [Description("Only search files matching this glob, for example *.cs")] string? include = null,
        [Description("Case-insensitive search")] bool ignoreCase = false,
        [Description("Lines of context around each match in content mode")] int context = 0,
        CancellationToken cancellationToken = default)
    {
        string full = workspace.Resolve(path);
        if (!Directory.Exists(full) && !File.Exists(full)) return $"Error: '{workspace.Display(full)}' does not exist.";
        if (mode is not ("files" or "content" or "count")) return "Error: mode must be files, content or count.";
        context = Math.Clamp(context, 0, 10);

        List<string> lines = RipgrepPath is { } rg
            ? await RipgrepAsync(rg, pattern, full, mode, include, ignoreCase, context, cancellationToken)
            : ManagedGrep(pattern, full, mode, include, ignoreCase, context);

        string scope = workspace.Display(full);
        if (lines.Count == 0) return $"grep /{pattern}/ in {scope} · no matches";
        int total = lines.Count;
        string unit = mode switch { "files" => "files", "count" => "files", _ => "lines" };
        StringBuilder sb = new($"grep /{pattern}/ in {scope} · {mode} · {total.ToString(Inv)} {unit}");
        if (total > MaxGrepResults) sb.Append($" · showing first {MaxGrepResults} (narrow the pattern or path)");
        sb.Append('\n');
        foreach (string line in lines.Take(MaxGrepResults))
            sb.Append(line.Length > MaxLineChars ? line[..MaxLineChars] + "…" : line).Append('\n');
        return sb.ToString();
    }

    [Description("Replace an exact string in a file. 'old' must occur exactly once unless replaceAll is true. Read the file first.")]
    public async Task<string> Edit(
        [Description("File path relative to the workspace root")] string path,
        [Description("Exact text to replace, including whitespace")] string old,
        [Description("Replacement text")] string @new,
        [Description("Replace every occurrence instead of requiring exactly one")] bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        string full = workspace.Resolve(path);
        if (!File.Exists(full)) return $"Error: '{workspace.Display(full)}' does not exist. Use write to create it.";
        if (string.IsNullOrEmpty(old)) return "Error: 'old' must not be empty.";
        if (old == @new) return "Error: 'old' and 'new' are identical.";

        byte[] bytes = await File.ReadAllBytesAsync(full, cancellationToken);
        if (CheckFresh(full, bytes) is { } stale) return stale;

        string text = Encoding.UTF8.GetString(bytes);
        bool crlf = text.Contains("\r\n", StringComparison.Ordinal);
        if (crlf && !old.Contains('\r'))
        {
            old = old.Replace("\n", "\r\n", StringComparison.Ordinal);
            @new = @new.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        int count = CountOccurrences(text, old);
        if (count == 0) return $"Error: 'old' was not found in {workspace.Display(full)}. Re-read the file and copy the text exactly.";
        if (count > 1 && !replaceAll) return $"Error: 'old' occurs {count} times in {workspace.Display(full)}. Add surrounding lines to make it unique, or set replaceAll.";

        int first = text.IndexOf(old, StringComparison.Ordinal);
        string updated = replaceAll ? text.Replace(old, @new, StringComparison.Ordinal) : string.Concat(text.AsSpan(0, first), @new, text.AsSpan(first + old.Length));
        byte[] output = Encoding.UTF8.GetBytes(updated);
        if (HasBom(bytes) && !HasBom(output)) output = [.. Encoding.UTF8.GetPreamble(), .. output];
        await File.WriteAllBytesAsync(full, output, cancellationToken);
        Remember(full, output);

        int startLine = text.AsSpan(0, first).Count('\n') + 1;
        return $"edited {workspace.Display(full)} · {(replaceAll ? count : 1)} replacement{(count > 1 && replaceAll ? "s" : "")} · at line {startLine}\n" + Diff(old, @new, startLine);
    }

    [Description("Create a file or overwrite it completely. Overwriting requires reading the file first.")]
    public async Task<string> Write(
        [Description("File path relative to the workspace root")] string path,
        [Description("Full file content")] string content,
        CancellationToken cancellationToken = default)
    {
        string full = workspace.Resolve(path);
        if (Directory.Exists(full)) return $"Error: '{workspace.Display(full)}' is a directory.";
        bool exists = File.Exists(full);
        if (exists && CheckFresh(full, await File.ReadAllBytesAsync(full, cancellationToken)) is { } stale) return stale;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(full, bytes, cancellationToken);
        Remember(full, bytes);
        int lines = content.Length == 0 ? 0 : content.AsSpan().Count('\n') + (content.EndsWith('\n') ? 0 : 1);
        return $"{(exists ? "overwrote" : "created")} {workspace.Display(full)} · {bytes.Length.ToString("N0", Inv)} bytes · {lines.ToString("N0", Inv)} lines";
    }

    // ---- helpers ----

    /// <summary>Read-before-write: the file must have been read in this session and be unchanged since.</summary>
    private string? CheckFresh(string full, byte[] current)
    {
        if (!state.Reads.TryGetValue(full, out FileReadState? read))
            return $"Error: read {workspace.Display(full)} before changing it.";
        if (read.Hash != Workspace.Hash(current))
            return $"Error: {workspace.Display(full)} changed since you last read it (turn {read.Turn}). Read it again before changing it.";
        return null;
    }

    private void Remember(string full, byte[] bytes) =>
        state.Reads[full] = new FileReadState(Workspace.Hash(bytes), state.Turn, 0, 0);

    private static string Diff(string old, string @new, int startLine)
    {
        StringBuilder sb = new();
        string[] oldLines = SplitLines(old), newLines = SplitLines(@new);
        const int MaxShown = 12;
        foreach (string l in oldLines.Take(MaxShown)) sb.Append("- ").Append(l).Append('\n');
        if (oldLines.Length > MaxShown) sb.Append($"- … {oldLines.Length - MaxShown} more lines\n");
        foreach (string l in newLines.Take(MaxShown)) sb.Append("+ ").Append(l).Append('\n');
        if (newLines.Length > MaxShown) sb.Append($"+ … {newLines.Length - MaxShown} more lines\n");
        return sb.ToString();
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0) { count++; index += value.Length; }
        return count;
    }

    private static bool IsBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0;

    private static bool HasBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static int CountEntries(string dir, Gitignore ignore)
    {
        try { return new DirectoryInfo(dir).EnumerateFileSystemInfos().Count(e => !ignore.IsIgnored(e.FullName, e is DirectoryInfo)); }
        catch (Exception) { return 0; }
    }

    internal static string? RipgrepPath { get; set; } = FindRipgrep();

    private static string? FindRipgrep() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "rg.exe" : "rg")).FirstOrDefault(File.Exists);

    private async Task<List<string>> RipgrepAsync(string rg, string pattern, string full, string mode, string? include, bool ignoreCase, int context, CancellationToken ct)
    {
        ProcessStartInfo psi = new(rg) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workspace.Root };
        foreach (string a in mode switch
        {
            "files" => new[] { "--files-with-matches" },
            "count" => ["--count"],
            _ => ["--line-number", "--no-heading", "--with-filename", .. context > 0 ? new[] { "-C", context.ToString(Inv) } : []],
        }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--color=never");
        psi.ArgumentList.Add("--max-columns=" + MaxLineChars);
        psi.ArgumentList.Add("--glob=!.harness/spill/**");
        if (ignoreCase) psi.ArgumentList.Add("-i");
        if (include is not null) psi.ArgumentList.Add("--glob=" + include);
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(pattern);
        psi.ArgumentList.Add(Path.GetRelativePath(workspace.Root, full));

        using Process process = Process.Start(psi)!;
        List<string> lines = [];
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            if (lines.Count <= MaxGrepResults * 5) lines.Add(line.Replace('\\', '/'));
        await process.WaitForExitAsync(ct);
        if (process.ExitCode == 2 && lines.Count == 0)
            return [$"Error: {(await stderr).Trim()}"];
        return lines;
    }

    private List<string> ManagedGrep(string pattern, string full, string mode, string? include, bool ignoreCase, int context)
    {
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return [$"Error: invalid regex: {ex.Message}"]; }
        Regex? includeRegex = include is null ? null : new Regex("^(?:.*/)?" + Gitignore.GlobToRegex(include) + "$");

        IEnumerable<string> files = File.Exists(full)
            ? [full]
            : new Gitignore(workspace.Root).Walk(full).OfType<FileInfo>().Select(f => f.FullName);
        List<string> output = [];
        foreach (string file in files)
        {
            string rel = workspace.Display(file);
            if (includeRegex is not null && !includeRegex.IsMatch(rel)) continue;
            string[] lines;
            try
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (IsBinary(bytes)) continue;
                lines = SplitLines(Encoding.UTF8.GetString(bytes));
            }
            catch (IOException) { continue; }
            int hits = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i])) continue;
                hits++;
                if (mode == "content")
                {
                    for (int c = Math.Max(0, i - context); c < i; c++) output.Add($"{rel}-{c + 1}-{lines[c]}");
                    output.Add($"{rel}:{i + 1}:{lines[i]}");
                    for (int c = i + 1; c <= Math.Min(lines.Length - 1, i + context); c++) output.Add($"{rel}-{c + 1}-{lines[c]}");
                }
                else if (mode == "files") break;
            }
            if (hits > 0 && mode == "files") output.Add(rel);
            if (hits > 0 && mode == "count") output.Add($"{rel}:{hits}");
            if (output.Count > MaxGrepResults * 5) break;
        }
        return output;
    }
}
