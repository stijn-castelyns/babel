using System.Runtime.InteropServices;
using System.Text;
using Harness.Core.Agents;
using Harness.Sdk;

namespace Harness.Core.Prompts;

/// <summary>
/// Composes the system prompt from the layers that are recomputed every run (never stored in history):
/// 1 harness base, 2 agent instructions, 3 user global AGENTS.md, 4 folder chain, 6 run instructions, 7 plugin contributors.
/// Layer 5 (lazy subfolder instructions) is appended to tool results by <see cref="ToolCallMiddleware"/>.
/// </summary>
public sealed class PromptComposer(HarnessPaths paths)
{
    public async Task<string> ComposeAsync(RunContext run, IEnumerable<IPromptContributor> contributors, CancellationToken ct)
    {
        StringBuilder sb = new();
        sb.Append(Base(run));

        if (run.Agent.Instructions is { } instructionsPath && ReadIfExists(paths.Resolve(instructionsPath)) is { } agentText)
            Section(sb, "Agent instructions", agentText);

        if (ReadIfExists(paths.GlobalAgentsMd) is { } global)
            Section(sb, "User instructions (~/.harness/AGENTS.md)", global);

        foreach ((string file, string text) in FolderChain(run.WorkspaceRoot, run.WorkingDirectory, run.FolderInstructionFiles))
        {
            string rel = Path.GetRelativePath(run.WorkspaceRoot, file).Replace('\\', '/');
            Section(sb, $"Project instructions ({rel})", text);
            run.State.LoadedFolderInstructions.TryAdd(Path.GetDirectoryName(file)!, true);
        }

        if (run.RunInstructions is { Length: > 0 } runText)
            Section(sb, "Run instructions", runText);

        PromptContributionContext pc = new(run.RunId, run.SessionId, run.AgentName, run.WorkspaceRoot, run.WorkingDirectory);
        foreach (IPromptContributor contributor in contributors)
            if (await contributor.ContributeAsync(pc, ct) is { Length: > 0 } extra)
                sb.Append("\n\n").Append(extra.Trim());

        return sb.ToString();
    }

    /// <summary>Layer 1: tool rules and the environment block.</summary>
    public static string Base(RunContext run)
    {
        string sandboxNote = run.Sandbox.Type == "none"
            ? "Commands run directly on the host."
            : $"Commands run inside a '{run.Sandbox.Type}' sandbox where the workspace is mounted at {run.Sandbox.Paths.ToSandbox(run.WorkspaceRoot)}.";
        string workdir = Path.GetRelativePath(run.WorkspaceRoot, run.WorkingDirectory).Replace('\\', '/');
        return $"""
            You are {run.AgentName}, a coding agent working in a local workspace through tools.

            Tool rules:
            - File paths are relative to the workspace root. Paths outside the workspace are rejected.
            - Read a file before you edit or overwrite it; edits fail if the file changed since you last read it.
            - Prefer `edit` with an exact, unique `old` string over rewriting whole files with `write`.
            - Results start with a one-line header that says how to continue (for example `next: offset=401`).
            - Large outputs are saved to a file and shown as head and tail; use `read` on the given path for the rest.
            - Use `grep` and `glob` to find code instead of listing large directory trees.
            - Some tools need the user's approval. If a call is denied, do not retry it unchanged; adapt or ask.
            - When the task is done, reply with a short summary of what changed and anything left to do.

            Environment:
            - OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})
            - Date: {DateTimeOffset.Now:yyyy-MM-dd}
            - Workspace root: {run.WorkspaceRoot}
            - Working directory: {(workdir == "." ? "(workspace root)" : workdir)}
            - Sandbox: {run.Sandbox.Type}. {sandboxNote}
            """;
    }

    /// <summary>Instruction files from the workspace root down to the working directory, root first.</summary>
    public static IEnumerable<(string File, string Text)> FolderChain(string root, string workingDirectory, IReadOnlyList<string> fileNames)
    {
        List<string> dirs = [];
        string? dir = Path.GetFullPath(workingDirectory);
        string fullRoot = Path.GetFullPath(root);
        while (dir is not null && (dir == fullRoot || dir.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            dirs.Add(dir);
            if (dir == fullRoot) break;
            dir = Path.GetDirectoryName(dir);
        }
        dirs.Reverse();
        foreach (string d in dirs)
            if (FirstExisting(d, fileNames) is { } file && ReadIfExists(file) is { } text)
                yield return (file, text);
    }

    public static string? FirstExisting(string dir, IReadOnlyList<string> fileNames) =>
        fileNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);

    private static void Section(StringBuilder sb, string title, string body) =>
        sb.Append("\n\n## ").Append(title).Append("\n\n").Append(body.Trim());

    private static string? ReadIfExists(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (IOException) { return null; }
    }
}
