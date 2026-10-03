using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Tools;

/// <summary>
/// A small .gitignore implementation for the file tools: comments, negation, directory-only and anchored patterns,
/// <c>*</c>, <c>?</c> and <c>**</c>. Each directory's .gitignore applies to paths below it.
/// </summary>
public sealed class Gitignore
{
    private readonly string _root;
    private readonly Dictionary<string, List<Rule>> _rulesByDir = new(StringComparer.Ordinal);

    private sealed record Rule(Regex Pattern, bool Negate, bool DirectoryOnly);

    /// <summary>Always skipped, whatever .gitignore says.</summary>
    public static readonly HashSet<string> AlwaysIgnored = [".git", ".hg", ".svn"];

    public Gitignore(string root) => _root = root;

    public bool IsIgnored(string fullPath, bool isDirectory)
    {
        if (AlwaysIgnored.Contains(Path.GetFileName(fullPath))) return true;
        string rel = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        if (rel is "." or "") return false;
        if (rel == ".harness/spill") return true;

        bool ignored = false;
        // Walk the .gitignore files from the root down to the path's parent; later (deeper) rules win.
        string[] segments = rel.Split('/');
        string dir = _root;
        for (int depth = 0; depth < segments.Length; depth++)
        {
            string relToDir = string.Join('/', segments[depth..]);
            // A directory-only rule can only match a file through one of the file's parent directories.
            int lastSlash = relToDir.LastIndexOf('/');
            string? parentPart = lastSlash < 0 ? null : relToDir[..lastSlash];
            foreach (Rule rule in RulesFor(dir))
            {
                string? subject = rule.DirectoryOnly && !isDirectory ? parentPart : relToDir;
                if (subject is not null && rule.Pattern.IsMatch(subject)) ignored = !rule.Negate;
            }
            dir = Path.Combine(dir, segments[depth]);
        }
        return ignored;
    }

    private List<Rule> RulesFor(string dir)
    {
        if (_rulesByDir.TryGetValue(dir, out List<Rule>? cached)) return cached;
        List<Rule> rules = [];
        string file = Path.Combine(dir, ".gitignore");
        if (File.Exists(file))
            foreach (string raw in File.ReadLines(file))
                if (Parse(raw) is { } rule) rules.Add(rule);
        _rulesByDir[dir] = rules;
        return rules;
    }

    private static Rule? Parse(string line)
    {
        line = line.TrimEnd();
        if (line.Length == 0 || line.StartsWith('#')) return null;
        bool negate = line.StartsWith('!');
        if (negate) line = line[1..];
        bool dirOnly = line.EndsWith('/');
        line = line.TrimEnd('/');
        bool anchored = line.Contains('/');
        line = line.TrimStart('/');
        if (line.Length == 0) return null;
        string body = GlobToRegex(line);
        // Unanchored patterns match at any depth; a match on a directory also covers everything below it.
        string pattern = (anchored ? "^" : "^(?:.*/)?") + body + "(?:/.*)?$";
        return new Rule(new Regex(pattern, RegexOptions.CultureInvariant), negate, dirOnly);
    }

    /// <summary>Converts a glob to a regex body: <c>**</c> spans directories, <c>*</c> and <c>?</c> do not.</summary>
    public static string GlobToRegex(string glob)
    {
        StringBuilder sb = new();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                bool slashAfter = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else if (c == '[')
            {
                int close = glob.IndexOf(']', i + 1);
                if (close < 0) { sb.Append("\\["); continue; }
                string set = glob[(i + 1)..close].Replace("\\", "\\\\");
                if (set.StartsWith('!')) set = "^" + set[1..];
                sb.Append('[').Append(set).Append(']');
                i = close;
            }
            else if (c == '{' && glob.IndexOf('}', i) is int end and > 0)
            {
                sb.Append("(?:").Append(string.Join('|', glob[(i + 1)..end].Split(',').Select(GlobToRegex))).Append(')');
                i = end;
            }
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return sb.ToString();
    }

    /// <summary>Walks the tree below <paramref name="start"/>, skipping ignored entries and not following directory symlinks.</summary>
    public IEnumerable<FileSystemInfo> Walk(string start, int maxDepth = int.MaxValue)
    {
        Stack<(DirectoryInfo Dir, int Depth)> stack = new();
        stack.Push((new DirectoryInfo(start), 0));
        while (stack.Count > 0)
        {
            (DirectoryInfo dir, int depth) = stack.Pop();
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            foreach (FileSystemInfo entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                bool isDir = entry is DirectoryInfo;
                if (IsIgnored(entry.FullName, isDir)) continue;
                yield return entry;
                if (isDir && entry.LinkTarget is null && depth + 1 < maxDepth) stack.Push(((DirectoryInfo)entry, depth + 1));
            }
        }
    }
}
