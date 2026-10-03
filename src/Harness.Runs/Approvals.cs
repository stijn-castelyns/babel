using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Harness.Runs;

/// <summary>A tool call waiting for a person's decision. Whichever client answers first wins.</summary>
public sealed class PendingApproval
{
    public required string RequestId { get; init; }
    public required string RunId { get; init; }
    public required string SessionId { get; init; }
    public required string ToolName { get; init; }
    public required string ToolCallId { get; init; }
    public required JsonObject Arguments { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    internal TaskCompletionSource<ApprovalAnswer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record ApprovalAnswer(bool Approved, string? Reason, string DecidedBy, string Via, bool AlwaysForSession = false);

/// <summary>Tracks pending approvals across runs, so the approvals inbox and any client can answer them.</summary>
public sealed class ApprovalBroker
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();

    public IReadOnlyList<PendingApproval> Pending(string? runId = null) =>
        [.. _pending.Values.Where(p => runId is null || p.RunId == runId).OrderBy(p => p.RequestedAt)];

    public void Add(PendingApproval approval) => _pending[approval.RequestId] = approval;

    public bool Resolve(string runId, string requestId, ApprovalAnswer answer)
    {
        if (!_pending.TryGetValue(requestId, out PendingApproval? p) || p.RunId != runId) return false;
        if (!p.Completion.TrySetResult(answer)) return false;
        _pending.TryRemove(requestId, out _);
        return true;
    }

    public void Remove(string requestId) => _pending.TryRemove(requestId, out _);

    /// <summary>Denies everything still pending for a run (used when the run is cancelled).</summary>
    public void CancelRun(string runId)
    {
        foreach (PendingApproval p in Pending(runId))
        {
            p.Completion.TrySetCanceled();
            _pending.TryRemove(p.RequestId, out _);
        }
    }
}
