using Harness.Core.Agents;
using Microsoft.Extensions.AI;

namespace Harness.Tools;

/// <summary>
/// Contributes the built-in tools an agent definition lists under <c>tools.builtin</c>. The web tools are opt-in: they run
/// in the daemon and reach the internet whatever the sandbox's network setting, so they are not in the default list.
/// </summary>
public sealed class BuiltinToolsExtension : IRunExtension
{
    public static readonly IReadOnlyList<string> Names = ["read", "list", "glob", "grep", "edit", "write", "shell", "web_search", "web_fetch"];

    public ValueTask<IEnumerable<AITool>> GetToolsAsync(RunContext run, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Create(run.Agent.Tools.Builtin, new Workspace(run.WorkspaceRoot), run));

    public static IEnumerable<AITool> Create(IEnumerable<string> names, IWorkspace workspace, RunContext run)
    {
        FileTools files = new(workspace, run.State);
        ShellTool shell = new(workspace, run.Sandbox, run.State);
        WebTools web = new(run.Web);
        foreach (string name in names.Distinct())
        {
            yield return name switch
            {
                "read" => AIFunctionFactory.Create(files.Read, name: "read"),
                "list" => AIFunctionFactory.Create(files.List, name: "list"),
                "glob" => AIFunctionFactory.Create(files.Glob, name: "glob"),
                "grep" => AIFunctionFactory.Create(files.Grep, name: "grep"),
                "edit" => AIFunctionFactory.Create(files.Edit, name: "edit"),
                "write" => AIFunctionFactory.Create(files.Write, name: "write"),
                "shell" => AIFunctionFactory.Create(shell.Shell, name: "shell"),
                "web_search" => AIFunctionFactory.Create(web.WebSearch, name: "web_search"),
                "web_fetch" => AIFunctionFactory.Create(web.WebFetch, name: "web_fetch"),
                _ => throw new Harness.Core.Config.ConfigException($"Unknown built-in tool '{name}'. Built-ins are: {string.Join(", ", Names)}."),
            };
        }
    }
}
