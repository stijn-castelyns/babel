using System.Text.Json.Nodes;
using Harness.Runs;
using Harness.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Triggers.Sources;

/// <summary>
/// Fires when another triggered run finishes, which chains runs without a workflow engine:
/// <c>{ type: run-completed, triggers: [nightly-deps], states: [succeeded, failed] }</c>. <c>trigger:</c> takes one id;
/// without either, every other trigger's runs count (never this trigger's own). <c>states</c> defaults to <c>succeeded</c>;
/// <c>any</c> matches every final state. The event's text is the finished run's text (its error, when it did not succeed), and
/// <c>{event.data.run.output.x}</c>, <c>{event.data.run.state}</c>… expose the rest. Interactive runs never fire it, and chains
/// stop after <see cref="TriggerEngine.MaxChainDepth"/> runs.
/// </summary>
public sealed class RunCompletedSource : ITriggerSource
{
    private RunOrchestrator? _runs;
    private Action<RunResult>? _handler;

    public string Type => "run-completed";

    public Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken)
    {
        HashSet<string> triggers = Strings(context.Settings["triggers"] ?? context.Settings["trigger"]);
        HashSet<string> states = Strings(context.Settings["states"] ?? context.Settings["state"]);
        if (states.Count == 0) states.Add(RunStates.Succeeded);
        foreach (string state in states)
            if (state != "any" && !RunStates.IsFinal(state))
                throw new InvalidOperationException($"Trigger '{context.TriggerId}': '{state}' is not a final run state.");

        TriggerEngine engine = context.Services.GetRequiredService<TriggerEngine>();
        _runs = context.Services.GetRequiredService<RunOrchestrator>();
        _handler = result =>
        {
            if (result.TriggerId is not { } upstream) return;
            if (triggers.Count > 0 ? !triggers.Contains(upstream) : upstream == context.TriggerId) return;
            if (!states.Contains("any") && !states.Contains(result.State)) return;
            _ = Task.Run(() => engine.EmitRunCompletedAsync(context, result));
        };
        _runs.RunCompleted += _handler;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runs is not null && _handler is not null) _runs.RunCompleted -= _handler;
        _handler = null;
        return Task.CompletedTask;
    }

    private static HashSet<string> Strings(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.Select(n => n?.ToString()).OfType<string>()],
        JsonValue v => [v.ToString()],
        _ => [],
    };
}
