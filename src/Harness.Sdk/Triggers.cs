using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Harness.Sdk;

/// <summary>Anything that emits <see cref="TriggerEvent"/>s: schedules, webhooks, chat channels.</summary>
public interface ITriggerSource
{
    string Type { get; }
    Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Handed to a trigger source when it starts.</summary>
public abstract class TriggerSourceContext
{
    /// <summary>The trigger definition this source instance serves.</summary>
    public abstract string TriggerId { get; }
    /// <summary>The <c>source:</c> block of the trigger definition, secrets already resolved.</summary>
    public abstract JsonObject Settings { get; }
    public abstract IServiceProvider Services { get; }
    /// <summary>Writes the event to the durable queue. Duplicate <see cref="TriggerEvent.EventId"/>s are dropped.</summary>
    public abstract ValueTask EmitAsync(TriggerEvent triggerEvent, CancellationToken cancellationToken);
    /// <summary>Registers a route under <c>/hooks/</c> on the webhook listener.</summary>
    public abstract void MapWebhook(string path, Func<HttpContext, Task> handler);
}

public sealed record TriggerEvent(
    string EventId,
    string TriggerId,
    DateTimeOffset ReceivedAt,
    string? Sender,
    string? Text,
    IReadOnlyList<EventAttachment> Attachments,
    JsonObject Data,
    ReplyAddress? ReplyTo);

public sealed record EventAttachment(string Name, string ContentType, string? Path, Uri? Url);

/// <summary>Where the <c>reply</c> sink and in-chat approvals go.</summary>
public sealed record ReplyAddress(string Channel, string Address, JsonObject? Extra = null);
