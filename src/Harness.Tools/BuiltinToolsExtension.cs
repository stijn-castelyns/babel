using Harness.Core.Agents;
using Microsoft.Extensions.AI;

namespace Harness.Tools;

/// <summary>Contributes the seven built-in tools an agent definition lists under <c>tools.builtin</c>.</summary>
public sealed class BuiltinToolsExtension : IRunExtension
{
    public static readonly IReadOnlyList<string> Names = ["read", "list", "glob", "grep", "edit", "write", "shell"];

    public ValueTask<IEnumerable<AITool>> GetToolsAsync(RunContext run, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Create(run.Agent.Tools.Builtin, new Workspace(run.WorkspaceRoot), run));

    public static IEnumerable<AITool> Create(IEnumerable<string> names, IWorkspace workspace, RunContext run)
    {
        FileTools files = new(workspace, run.State);
        ShellTool shell = new(workspace, run.Sandbox, run.State);
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
                _ => throw new Harness.Core.Config.ConfigException($"Unknown built-in tool '{name}'. Built-ins are: {string.Join(", ", Names)}."),
            };
        }
    }
}
