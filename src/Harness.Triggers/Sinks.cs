using System.Text.Json.Nodes;
using Harness.Runs.Delivery;
using Harness.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Triggers;

/// <summary>
/// <c>reply</c> (or <c>{ type: reply, from: ... }</c>): answers the original sender through the trigger source that received the
/// event (any source implementing <see cref="IReplyChannel"/>), or a registered <see cref="IReplyChannel"/> for the channel.
/// Runs without a reply address (a manual fire, a schedule) skip it.
/// </summary>
public sealed class ReplySink(IServiceProvider services) : IOutputSink
{
    public string Type => "reply";

    public Task DeliverAsync(RunResult result, SinkOptions options, CancellationToken cancellationToken)
    {
        if (result.ReplyTo is not { } to) return Task.CompletedTask;
        IReplyChannel channel = (result.TriggerId is { } trigger ? services.GetRequiredService<TriggerEngine>().ReplyChannel(trigger, to.Channel) : null)
            ?? services.GetServices<IReplyChannel>().FirstOrDefault(c => c.Channel == to.Channel)
            ?? throw new InvalidOperationException($"nothing can send replies on channel '{to.Channel}'.");
        string text = options.Settings["from"] is JsonValue from
            ? SinkContent.Select(result, from.ToString())
            : result.Text is { Length: > 0 } t ? t : result.Error ?? "";
        return channel.SendAsync(to, text, result.Files, cancellationToken);
    }
}

/// <summary>
/// <c>{ type: run, trigger: &lt;id&gt;, text: "...", inputs: { ... } }</c>: starts another trigger with this run's result, which chains
/// runs without a workflow engine. Text and inputs may use <c>{output.x}</c>, <c>{text}</c> and the other run variables; the
/// next run also sees the whole result as <c>{event.data}</c>.
/// </summary>
public sealed class RunTriggerSink(IServiceProvider services) : IOutputSink
{
    public string Type => "run";

    public async Task DeliverAsync(RunResult result, SinkOptions options, CancellationToken cancellationToken)
    {
        string trigger = options.Settings["trigger"]?.ToString() ?? throw new InvalidOperationException("the run sink needs a trigger.");
        int depth = int.TryParse(options.Variables?.GetValueOrDefault("chain.depth"), out int d) ? d : 0;
        string? text = options.Settings["text"]?.ToString() ?? result.Text;
        await services.GetRequiredService<TriggerEngine>()
            .FireFromRunAsync(trigger, text, options.Settings["inputs"] as JsonObject, result, depth, cancellationToken);
    }
}
