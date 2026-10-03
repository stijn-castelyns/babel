using System.Text.Json.Nodes;

namespace Harness.Sdk;

/// <summary>
/// One step that builds a triggered run's workspace (<c>git</c>, <c>copy</c>, <c>run</c>, or a plugin type such as restoring a
/// database snapshot). Steps run in the order the run template lists them, before any model call.
/// </summary>
public interface IWorkspaceStep
{
    string Type { get; }
    /// <summary>Throws <see cref="WorkspaceStepException"/> (or any exception) to abort the run.</summary>
    Task ExecuteAsync(WorkspaceStepContext context, CancellationToken cancellationToken);
}

/// <summary>What a workspace step gets to work with.</summary>
public sealed class WorkspaceStepContext
{
    public required string RunId { get; init; }
    public required string TemplateName { get; init; }
    /// <summary>Host path of the template folder (read-only by convention).</summary>
    public required string TemplateDirectory { get; init; }
    /// <summary>Host path of the run's workspace, <c>~/.harness/runs/&lt;run-id&gt;/workspace</c>.</summary>
    public required string WorkspaceRoot { get; init; }
    /// <summary>The step's settings with <c>{inputs.x}</c>, <c>{event.text}</c> and similar placeholders already rendered.</summary>
    public required JsonNode? Settings { get; init; }
    /// <summary>The variables available to <c>*.tmpl</c> files and settings.</summary>
    public required IReadOnlyDictionary<string, string> Variables { get; init; }
    /// <summary>The run's sandbox profile, with the template folder mounted read-only. Anything that executes code goes through it.</summary>
    public required ISandbox Sandbox { get; init; }
    public required IServiceProvider Services { get; init; }
    /// <summary>Adds a line to the step's log, which is shown in the run's events.</summary>
    public required Action<string> Log { get; init; }
    /// <summary>Renders placeholders in a string with <see cref="Variables"/>; unknown placeholders are left as they are.</summary>
    public required Func<string, string> Render { get; init; }
}

public sealed class WorkspaceStepException(string message, Exception? inner = null) : Exception(message, inner);
