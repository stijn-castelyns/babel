using System.Text.Json;
using Harness.Core;
using Harness.Core.Agents;
using Harness.Extensions.Plugins;
using Harness.Sdk;
using Microsoft.Agents.AI;

namespace Harness.Extensions.Skills;

/// <summary>
/// Agent Framework's <see cref="AgentSkillsProvider"/> over the agent's skill directories and plugin skills.
/// Scripts run inside the run's sandbox, never as host subprocesses. <c>load_skill</c> and <c>read_skill_resource</c> are
/// auto-approved; <c>run_skill_script</c> follows the agent's approval policy.
/// </summary>
public sealed class SkillsExtension(HarnessPaths paths, PluginRegistry plugins) : IRunExtension
{
    /// <summary>Skill directories the run uses, as host paths. The orchestrator mounts them read-only into the sandbox.</summary>
    public IReadOnlyList<string> SkillDirectories(Harness.Core.Config.AgentDefinition agent) =>
        [.. agent.Skills.Select(paths.Resolve).Where(Directory.Exists).Distinct()];

    public ValueTask<IEnumerable<AIContextProvider>> GetContextProvidersAsync(RunContext run, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> dirs = SkillDirectories(run.Agent);
        IReadOnlyList<AgentSkill> pluginSkills = plugins.SkillsFor(run.Agent);
        if (dirs.Count == 0 && pluginSkills.Count == 0) return ValueTask.FromResult<IEnumerable<AIContextProvider>>([]);

        bool scriptsAllowed = ApprovalPolicy.For(run.Agent, "run_skill_script") == ApprovalPolicy.Allow;
        AgentSkillsProviderBuilder builder = new AgentSkillsProviderBuilder()
            .UseFileScriptRunner((skill, script, args, services, ct) => RunScriptAsync(run, script, args, ct))
            .UseOptions(o =>
            {
                o.DisableLoadSkillApproval = true;
                o.DisableReadSkillResourceApproval = true;
                o.DisableRunSkillScriptApproval = scriptsAllowed;
            });
        if (dirs.Count > 0) builder.UseFileSkills(dirs);
        if (pluginSkills.Count > 0) builder.UseSkills(pluginSkills);
        return ValueTask.FromResult<IEnumerable<AIContextProvider>>([builder.Build()]);
    }

    internal static async Task<object?> RunScriptAsync(RunContext run, AgentFileSkillScript script, JsonElement? arguments, CancellationToken ct)
    {
        string path = run.Sandbox.Paths.ToSandbox(script.FullPath);
        (string? interpreter, bool _) = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".py" => ("python3", true),
            ".sh" => ("bash", true),
            ".js" or ".mjs" => ("node", true),
            ".ps1" => ("pwsh", true),
            _ => ((string?)null, false),
        };
        List<string> argv = [.. interpreter is null ? [] : new[] { path }, .. CliArguments(arguments)];
        ExecResult result = await run.Sandbox.ExecAsync(new ExecRequest
        {
            Command = interpreter ?? path,
            Arguments = argv,
            WorkingDirectory = run.WorkspaceRoot,
            Environment = new Dictionary<string, string> { ["HARNESS_SKILL_ARGS"] = arguments?.GetRawText() ?? "{}" },
            Timeout = TimeSpan.FromMinutes(5),
        }, ct);
        return $"exit {result.ExitCode}{(result.TimedOut ? " (timed out)" : "")}\n{result.Output}";
    }

    /// <summary>An argument object becomes <c>--key value</c> pairs; an array becomes positional arguments.</summary>
    internal static IEnumerable<string> CliArguments(JsonElement? arguments)
    {
        if (arguments is not { } args) yield break;
        if (args.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in args.EnumerateArray()) yield return Scalar(item);
        }
        else if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in args.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.False || p.Value.ValueKind == JsonValueKind.Null) continue;
                yield return "--" + p.Name;
                if (p.Value.ValueKind != JsonValueKind.True) yield return Scalar(p.Value);
            }
        }
    }

    private static string Scalar(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();
}
