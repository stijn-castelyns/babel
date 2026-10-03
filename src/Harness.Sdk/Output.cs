using System.Text.Json.Nodes;

namespace Harness.Sdk;

/// <summary>Delivers a run's result somewhere (reply, file, webhook, or a plugin destination).</summary>
public interface IOutputSink
{
    string Type { get; }
    Task DeliverAsync(RunResult result, SinkOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// A sink's settings from the trigger or template (<c>{ type: file, path: ... }</c>), with placeholders such as
/// <c>{output.summary}</c> rendered and <c>secret:</c>/<c>env:</c> references resolved, plus the variables used to render them.
/// </summary>
public sealed record SinkOptions(JsonObject Settings, IReadOnlyDictionary<string, string>? Variables = null);

/// <summary>
/// Sends a reply back to where a trigger event came from. Implement it on a trigger source (a chat channel) so the
/// <c>reply</c> sink can answer the original sender through the source instance that received the event.
/// </summary>
public interface IReplyChannel
{
    /// <summary>Matches <see cref="ReplyAddress.Channel"/>.</summary>
    string Channel { get; }
    Task SendAsync(ReplyAddress to, string text, IReadOnlyList<string> files, CancellationToken cancellationToken);
}

public static class RunStates
{
    public const string Queued = "queued";
    public const string Preparing = "preparing";
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Validating = "validating";
    public const string Delivering = "delivering";

    public const string Succeeded = "succeeded";
    public const string InvalidOutput = "invalid_output";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";

    public static bool IsFinal(string state) =>
        state is Succeeded or InvalidOutput or Failed or TimedOut or Cancelled or Rejected;
}

public sealed record RunResult
{
    public required string RunId { get; init; }
    public required string SessionId { get; init; }
    public required string State { get; init; }
    public string? TriggerId { get; init; }
    public string? Text { get; init; }
    public JsonNode? Output { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public string? Error { get; init; }
    public ReplyAddress? ReplyTo { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
}
