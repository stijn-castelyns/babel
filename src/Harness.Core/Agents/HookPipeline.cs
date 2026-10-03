using Harness.Sdk;

namespace Harness.Core.Agents;

/// <summary>Runs hooks in order. A hook that blocks, cancels, drops or decides stops the rest for that event.</summary>
public sealed class HookPipeline(IReadOnlyList<IHarnessHook> hooks)
{
    public static HookPipeline Empty { get; } = new([]);

    public IReadOnlyList<IHarnessHook> Hooks => hooks;

    public async ValueTask TriggerFiredAsync(TriggerFiredContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) { await h.OnTriggerFiredAsync(c, ct); if (c.Dropped) return; }
    }

    public async ValueTask RunStartingAsync(RunStartingContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) { await h.OnRunStartingAsync(c, ct); if (c.Cancelled) return; }
    }

    public async ValueTask ModelCallingAsync(ModelCallingContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) await h.OnModelCallingAsync(c, ct);
    }

    public async ValueTask ModelCalledAsync(ModelCalledContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) await h.OnModelCalledAsync(c, ct);
    }

    public async ValueTask ToolCallingAsync(ToolCallingContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) { await h.OnToolCallingAsync(c, ct); if (c.Blocked) return; }
    }

    public async ValueTask ToolCalledAsync(ToolCalledContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) await h.OnToolCalledAsync(c, ct);
    }

    public async ValueTask ApprovalRequestedAsync(ApprovalRequestedContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) { await h.OnApprovalRequestedAsync(c, ct); if (c.Decision != ApprovalDecision.Defer) return; }
    }

    public async ValueTask CompactingAsync(CompactingContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) await h.OnCompactingAsync(c, ct);
    }

    public async ValueTask RunCompletedAsync(RunCompletedContext c, CancellationToken ct)
    {
        foreach (IHarnessHook h in hooks) await h.OnRunCompletedAsync(c, ct);
    }
}
