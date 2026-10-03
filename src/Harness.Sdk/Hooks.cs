using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Harness.Sdk;

/// <summary>
/// One interface for every lifecycle point. All methods default to no-ops, so a hook implements only what it needs.
/// Hooks run in registration order; a hook that blocks or cancels stops the remaining hooks for that event.
/// </summary>
public interface IHarnessHook
{
    ValueTask OnTriggerFiredAsync(TriggerFiredContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnRunStartingAsync(RunStartingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnModelCallingAsync(ModelCallingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnModelCalledAsync(ModelCalledContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnToolCallingAsync(ToolCallingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnToolCalledAsync(ToolCalledContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnApprovalRequestedAsync(ApprovalRequestedContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnCompactingAsync(CompactingContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask OnRunCompletedAsync(RunCompletedContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Identity of the run a hook context belongs to.</summary>
public abstract class HookContext
{
    public required string RunId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentName { get; init; }
    public required string WorkspaceRoot { get; init; }
}

public sealed class TriggerFiredContext
{
    public required TriggerEvent Event { get; set; }
    public string? TemplateName { get; set; }
    public bool Dropped { get; private set; }
    public string? DropReason { get; private set; }
    public void Drop(string reason) { Dropped = true; DropReason = reason; }
}

public sealed class RunStartingContext : HookContext
{
    public required IList<ChatMessage> Messages { get; init; }
    public bool Cancelled { get; private set; }
    public string? CancelReason { get; private set; }
    public void Cancel(string reason) { Cancelled = true; CancelReason = reason; }
}

public sealed class ModelCallingContext : HookContext
{
    public required IList<ChatMessage> Messages { get; init; }
    public required ChatOptions Options { get; init; }
}

public sealed class ModelCalledContext : HookContext
{
    public UsageDetails? Usage { get; init; }
    public string? ModelId { get; init; }
}

public sealed class ToolCallingContext : HookContext
{
    public required string ToolName { get; init; }
    public required string CallId { get; init; }
    /// <summary>The call's arguments. Changes are passed on to the tool.</summary>
    public required IDictionary<string, object?> Arguments { get; init; }
    public bool Blocked { get; private set; }
    public string? BlockReason { get; private set; }

    /// <summary>Stops the call; the model sees the reason as the tool's result. To pre-approve calls, use <see cref="IHarnessHook.OnApprovalRequestedAsync"/>.</summary>
    public void Block(string reason) { Blocked = true; BlockReason = reason; }

    /// <summary>Reads an argument as <typeparamref name="T"/>, whether it arrived as a CLR value or a <see cref="JsonElement"/>.</summary>
    public T? Argument<T>(string name)
    {
        if (!Arguments.TryGetValue(name, out object? value) || value is null) return default;
        if (value is T typed) return typed;
        if (value is JsonElement element) return element.Deserialize<T>();
        return (T?)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

public sealed class ToolCalledContext : HookContext
{
    public required string ToolName { get; init; }
    public required string CallId { get; init; }
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
    /// <summary>The tool's result. Replace it to rewrite what the model sees.</summary>
    public object? Result { get; set; }
    public Exception? Exception { get; init; }
}

public enum ApprovalDecision { Defer, Approve, Deny }

public sealed class ApprovalRequestedContext : HookContext
{
    public required string RequestId { get; init; }
    public required string ToolName { get; init; }
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
    public ApprovalDecision Decision { get; private set; } = ApprovalDecision.Defer;
    public string? Reason { get; private set; }
    public void Approve(string? reason = null) { Decision = ApprovalDecision.Approve; Reason = reason; }
    public void Deny(string reason) { Decision = ApprovalDecision.Deny; Reason = reason; }
}

public sealed class CompactingContext : HookContext
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    /// <summary>Indexes into <see cref="Messages"/> that must survive compaction.</summary>
    public ISet<int> Pinned { get; } = new HashSet<int>();
}

public sealed class RunCompletedContext : HookContext
{
    public required RunResult Result { get; set; }
}
