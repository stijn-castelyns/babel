using System.Diagnostics;
using System.Text.Json.Nodes;
using Harness.Core;
using Harness.Core.Config;
using Harness.Extensions.Plugins;
using Harness.Sandbox;
using Harness.Sdk;

namespace Harness.Runs.Templates;

/// <summary>
/// Builds a triggered run's workspace under <c>~/.harness/runs/&lt;run-id&gt;/workspace</c> by running the template's steps in order,
/// then copies the template's <c>AGENTS.md</c> to the workspace root so it becomes the folder prompt layer.
/// </summary>
public sealed class WorkspaceBuilder
{
    public const string TemplateMountPath = "/harness/template";

    private readonly HarnessPaths _paths;
    private readonly SandboxFactory _sandboxes;
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, IWorkspaceStep> _steps = new(StringComparer.Ordinal);

    public WorkspaceBuilder(HarnessPaths paths, SandboxFactory sandboxes, PluginRegistry plugins, IEnumerable<IWorkspaceStep> steps, IServiceProvider services)
    {
        _paths = paths;
        _sandboxes = sandboxes;
        _services = services;
        foreach (IWorkspaceStep step in steps) _steps[step.Type] = step;
        foreach ((string type, IWorkspaceStep step) in plugins.WorkspaceSteps()) _steps.TryAdd(type, step);
    }

    public IEnumerable<string> StepTypes => _steps.Keys;

    public string RunDirectory(string runId) => Path.Combine(_paths.RunsDir, runId);

    public string WorkspaceFor(string runId) => Path.Combine(RunDirectory(runId), "workspace");

    /// <summary>The steps to run: the template's, or by default <c>copy files/</c> and <c>run ./setup.sh</c> when those exist.</summary>
    public static JsonArray StepsOf(RunTemplate template)
    {
        if (template.Workspace.Steps is { } steps) return steps;
        JsonArray defaults = [];
        if (Directory.Exists(Path.Combine(template.Directory, "files"))) defaults.Add(new JsonObject { ["copy"] = new JsonObject { ["from"] = "files/", ["into"] = "." } });
        if (File.Exists(Path.Combine(template.Directory, "setup.sh"))) defaults.Add(new JsonObject { ["run"] = "./setup.sh" });
        return defaults;
    }

    /// <summary>
    /// Runs every step. <paramref name="spec"/> is the run's sandbox profile; the template folder is added to it read-only for
    /// <c>run</c> steps. Throws <see cref="WorkspaceStepException"/> when a step fails.
    /// </summary>
    public async Task BuildAsync(RunTemplate template, string runId, string workspace, IReadOnlyDictionary<string, string> variables,
        SandboxSpec spec, Action<string, JsonObject> emit, CancellationToken ct)
    {
        Directory.CreateDirectory(workspace);
        JsonArray steps = StepsOf(template);
        Dictionary<string, string> vars = new(variables) { ["run.id"] = runId, ["workspace"] = workspace };

        ISandbox? sandbox = null;
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                (string type, JsonNode? raw) = ((JsonObject)steps[i]!).Single();
                if (!_steps.TryGetValue(type, out IWorkspaceStep? step))
                    throw new WorkspaceStepException($"Step {i + 1}: no workspace step of type '{type}'. Available: {string.Join(", ", _steps.Keys.Order())}.");
                sandbox ??= await _sandboxes.CreateAsync(spec with
                {
                    Mounts = [.. spec.Mounts, new MountSpec(template.Directory, TemplateMountPath, MountMode.ReadOnly)],
                }, ct);

                List<string> log = [];
                emit(EventTypes.WorkspaceStep, new JsonObject { ["index"] = i + 1, ["type"] = type, ["status"] = "started" });
                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    await step.ExecuteAsync(new WorkspaceStepContext
                    {
                        RunId = runId,
                        TemplateName = template.Name,
                        TemplateDirectory = template.Directory,
                        WorkspaceRoot = workspace,
                        Settings = TemplateVariables.Render(raw, vars),
                        Variables = vars,
                        Sandbox = sandbox,
                        Services = _services,
                        Log = log.Add,
                        Render = text => TemplateVariables.Render(text, vars),
                    }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    emit(EventTypes.WorkspaceStep, new JsonObject
                    {
                        ["index"] = i + 1, ["type"] = type, ["status"] = "failed", ["error"] = ex.Message, ["log"] = Join(log),
                        ["elapsedMs"] = clock.ElapsedMilliseconds,
                    });
                    throw ex as WorkspaceStepException ?? new WorkspaceStepException($"Step {i + 1} ({type}) failed: {ex.Message}", ex);
                }
                emit(EventTypes.WorkspaceStep, new JsonObject
                {
                    ["index"] = i + 1, ["type"] = type, ["status"] = "finished", ["log"] = Join(log), ["elapsedMs"] = clock.ElapsedMilliseconds,
                });
            }
        }
        finally
        {
            if (sandbox is not null) await sandbox.DisposeAsync();
        }

        string agentsMd = Path.Combine(template.Directory, "AGENTS.md");
        string target = Path.Combine(workspace, "AGENTS.md");
        if (File.Exists(agentsMd) && !File.Exists(target)) File.Copy(agentsMd, target);
    }

    /// <summary>Applies the template's <c>keep</c> policy once the run reached its final state.</summary>
    public void Cleanup(string runId, string keep, string finalState)
    {
        bool remove = keep switch
        {
            "never" => true,
            "always" => false,
            _ => finalState == RunStates.Succeeded,
        };
        string workspace = WorkspaceFor(runId);
        if (!remove || !Directory.Exists(workspace)) return;
        foreach (string file in Directory.EnumerateFileSystemEntries(workspace, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            // Git marks pack files read-only, which makes Directory.Delete fail on some platforms.
            try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Directory.Delete(workspace, recursive: true);
    }

    private static string? Join(List<string> log)
    {
        if (log.Count == 0) return null;
        string text = string.Join("\n", log);
        return text.Length > 4000 ? text[..1000] + "\n…\n" + text[^2900..] : text;
    }
}
