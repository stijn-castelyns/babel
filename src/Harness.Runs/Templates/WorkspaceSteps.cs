using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Sandbox;
using Harness.Sdk;

namespace Harness.Runs.Templates;

/// <summary><c>- git: { url, ref, depth, into, timeoutSeconds }</c>: clones a repository into the workspace, on the host.</summary>
public sealed partial class GitStep : IWorkspaceStep
{
    public string Type => "git";

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex CommitId();

    public async Task ExecuteAsync(WorkspaceStepContext context, CancellationToken cancellationToken)
    {
        JsonObject settings = context.Settings as JsonObject
            ?? (context.Settings is JsonValue v ? new JsonObject { ["url"] = v.GetValue<string>() } : throw new WorkspaceStepException("git needs a url."));
        string url = Text(settings, "url") ?? throw new WorkspaceStepException("git needs a url.");
        string? gitRef = Text(settings, "ref");
        int? depth = settings["depth"] is JsonValue d ? int.Parse(d.ToString(), System.Globalization.CultureInfo.InvariantCulture) : null;
        string into = WorkspacePaths.Inside(context.WorkspaceRoot, Text(settings, "into") ?? ".", "git.into");
        TimeSpan timeout = TimeSpan.FromSeconds(settings["timeoutSeconds"] is JsonValue t ? double.Parse(t.ToString(), System.Globalization.CultureInfo.InvariantCulture) : 600);

        bool commit = gitRef is not null && CommitId().IsMatch(gitRef);
        List<string> args = ["clone"];
        if (!commit && depth is int n) args.AddRange(["--depth", n.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (!commit && gitRef is not null) args.AddRange(["--branch", gitRef]);
        if (settings["submodules"] is JsonValue sm && sm.ToString() == "true") args.Add("--recurse-submodules");
        args.AddRange(["--", url, into]);
        await GitAsync(context, args, timeout, cancellationToken);
        if (commit) await GitAsync(context, ["-C", into, "checkout", "--detach", gitRef!], timeout, cancellationToken);
    }

    private static async Task GitAsync(WorkspaceStepContext context, List<string> args, TimeSpan timeout, CancellationToken ct)
    {
        ProcessStartInfo psi = new("git") { WorkingDirectory = context.WorkspaceRoot };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        ExecResult result = await ProcessRunner.RunAsync(psi, new ExecRequest { Command = "git", Timeout = timeout }, null, ct);
        if (result.Output.Trim().Length > 0) context.Log(result.Output.TrimEnd());
        if (result.TimedOut) throw new WorkspaceStepException($"git {args[0]} timed out after {timeout}.");
        if (result.ExitCode != 0) throw new WorkspaceStepException($"git {args[0]} exited with {result.ExitCode}: {Tail(result.Output)}");
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;

    internal static string Tail(string output) => output.Length > 2000 ? "…" + output[^2000..] : output.TrimEnd();
}

/// <summary>
/// <c>- copy: { from: files/, into: . }</c>: copies from the template folder into the workspace. <c>*.tmpl</c> files are rendered
/// with the run's variables and written without the <c>.tmpl</c> suffix.
/// </summary>
public sealed class CopyStep : IWorkspaceStep
{
    public string Type => "copy";

    public Task ExecuteAsync(WorkspaceStepContext context, CancellationToken cancellationToken)
    {
        JsonNode? s = context.Settings;
        string fromRel = s is JsonValue v ? v.ToString() : ((s as JsonObject)?["from"] as JsonValue)?.ToString() ?? "files/";
        string intoRel = ((s as JsonObject)?["into"] as JsonValue)?.ToString() ?? ".";
        string from = WorkspacePaths.Inside(context.TemplateDirectory, fromRel, "copy.from");
        string into = WorkspacePaths.Inside(context.WorkspaceRoot, intoRel, "copy.into");

        int count = 0;
        if (File.Exists(from))
        {
            bool toDirectory = intoRel is "." or "./" || intoRel.EndsWith('/') || Directory.Exists(into);
            CopyFile(from, toDirectory ? Path.Combine(into, Path.GetFileName(from)) : into, context);
            count = 1;
        }
        else if (Directory.Exists(from))
        {
            foreach (string file in Directory.EnumerateFiles(from, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyFile(file, Path.Combine(into, Path.GetRelativePath(from, file)), context);
                count++;
            }
        }
        else throw new WorkspaceStepException($"copy: '{fromRel}' does not exist in template '{context.TemplateName}'.");
        context.Log($"copied {count} file(s) from {fromRel} into {intoRel}");
        return Task.CompletedTask;
    }

    private static void CopyFile(string source, string target, WorkspaceStepContext context)
    {
        bool render = target.EndsWith(".tmpl", StringComparison.Ordinal);
        if (render) target = target[..^5];
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!render)
        {
            File.Copy(source, target, overwrite: true);
            return;
        }
        File.WriteAllText(target, context.Render(File.ReadAllText(source)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }
}

/// <summary>
/// <c>- run: ./setup.sh</c> or <c>- run: { command, timeoutSeconds }</c>: runs a command in the run's sandbox, in the workspace root.
/// A <c>./name</c> that exists in the template folder but not in the workspace runs from the template folder, so a template's
/// <c>setup.sh</c> works without copying it. A non-zero exit aborts the run before any model call.
/// </summary>
public sealed class RunStep : IWorkspaceStep
{
    public string Type => "run";

    public async Task ExecuteAsync(WorkspaceStepContext context, CancellationToken cancellationToken)
    {
        JsonNode? s = context.Settings;
        string command = s is JsonValue v ? v.ToString() : ((s as JsonObject)?["command"] as JsonValue)?.ToString() ?? throw new WorkspaceStepException("run needs a command.");
        TimeSpan timeout = TimeSpan.FromSeconds((s as JsonObject)?["timeoutSeconds"] is JsonValue t ? double.Parse(t.ToString(), System.Globalization.CultureInfo.InvariantCulture) : 600);
        command = ResolveTemplateScript(command, context);

        string templateInSandbox = context.Sandbox.Paths.ToSandbox(context.TemplateDirectory);
        ExecResult result = await context.Sandbox.ExecAsync(new ExecRequest
        {
            Command = command,
            WorkingDirectory = context.WorkspaceRoot,
            Timeout = timeout,
            Environment = new Dictionary<string, string> { ["HARNESS_TEMPLATE"] = templateInSandbox, ["HARNESS_RUN_ID"] = context.RunId },
        }, cancellationToken);
        if (result.Output.Trim().Length > 0) context.Log(result.Output.TrimEnd());
        if (result.TimedOut) throw new WorkspaceStepException($"'{command}' timed out after {timeout}.");
        if (result.ExitCode != 0) throw new WorkspaceStepException($"'{command}' exited with {result.ExitCode}: {GitStep.Tail(result.Output)}");
    }

    private static string ResolveTemplateScript(string command, WorkspaceStepContext context)
    {
        string trimmed = command.TrimStart();
        if (!trimmed.StartsWith("./", StringComparison.Ordinal)) return command;
        int end = trimmed.IndexOfAny([' ', '\t', '\n']);
        string token = end < 0 ? trimmed : trimmed[..end];
        string rest = end < 0 ? "" : trimmed[end..];
        if (File.Exists(Path.Combine(context.WorkspaceRoot, token))) return command;
        string inTemplate = Path.GetFullPath(Path.Combine(context.TemplateDirectory, token));
        if (!File.Exists(inTemplate) || !WorkspacePaths.IsInside(context.TemplateDirectory, inTemplate)) return command;

        string path = "'" + context.Sandbox.Paths.ToSandbox(inTemplate).Replace("'", "'\\''") + "'";
        bool executable = OperatingSystem.IsWindows() || (File.GetUnixFileMode(inTemplate) & UnixFileMode.UserExecute) != 0;
        return (executable ? path : "bash " + path) + rest;
    }
}

internal static class WorkspacePaths
{
    /// <summary>Resolves <paramref name="relative"/> under <paramref name="root"/> and rejects anything that escapes it.</summary>
    public static string Inside(string root, string relative, string what)
    {
        if (Path.IsPathRooted(relative)) throw new WorkspaceStepException($"{what} must be a relative path, not '{relative}'.");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(root, full)) throw new WorkspaceStepException($"{what} '{relative}' points outside its folder.");
        return full;
    }

    public static bool IsInside(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
