using System.Text.Json.Nodes;

namespace Harness.Sdk;

/// <summary>Delivers a run's result somewhere (reply, file, webhook, or a plugin destination).</summary>
public interface IOutputSink
{
    string Type { get; }
    Task DeliverAsync(RunResult result, SinkOptions options, CancellationToken cancellationToken);
}

public sealed record SinkOptions(JsonObject Settings);

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
