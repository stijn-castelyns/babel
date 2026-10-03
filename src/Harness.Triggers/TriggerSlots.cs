using Harness.Runs;

namespace Harness.Triggers;

/// <summary>
/// A trigger's <c>concurrency:</c> limits: at most <c>global</c> runs of the trigger at once, and at most <c>perSession</c>
/// runs per session key. Waiting runs get a slot in arrival order, as soon as one that fits their key is free.
/// </summary>
public sealed class TriggerSlots(string triggerId)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _perKey = new(StringComparer.Ordinal);
    private readonly LinkedList<(string? Key, TaskCompletionSource Ready)> _waiting = new();
    private int _active;

    public string TriggerId { get; } = triggerId;
    public int? Global { get; set; }
    public int? PerSession { get; set; }

    public int Active { get { lock (_gate) return _active; } }

    private bool Fits(string? key) =>
        (Global is not int g || _active < g) && (key is null || PerSession is not int p || _perKey.GetValueOrDefault(key) < p);

    private void Take(string? key)
    {
        _active++;
        if (key is not null) _perKey[key] = _perKey.GetValueOrDefault(key) + 1;
    }

    public bool IsFree(string? key) { lock (_gate) return Fits(key); }

    public bool TryTake(string? key)
    {
        lock (_gate)
        {
            if (!Fits(key)) return false;
            Take(key);
            return true;
        }
    }

    public async ValueTask TakeAsync(string? key, CancellationToken ct)
    {
        LinkedListNode<(string?, TaskCompletionSource)> node;
        lock (_gate)
        {
            if (Fits(key) && !_waiting.Any(w => w.Key == key))
            {
                Take(key);
                return;
            }
            node = _waiting.AddLast((key, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));
        }
        try { await node.Value.Item2.Task.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (node.List is not null) { _waiting.Remove(node); throw; }
            }
            Release(key);   // the slot was handed over just as the wait was cancelled
            throw;
        }
    }

    public void Release(string? key)
    {
        List<TaskCompletionSource> ready = [];
        lock (_gate)
        {
            _active--;
            if (key is not null && _perKey.TryGetValue(key, out int n)) { if (n <= 1) _perKey.Remove(key); else _perKey[key] = n - 1; }
            for (LinkedListNode<(string? Key, TaskCompletionSource Ready)>? node = _waiting.First; node is not null;)
            {
                LinkedListNode<(string? Key, TaskCompletionSource Ready)>? next = node.Next;
                if (Fits(node.Value.Key))
                {
                    Take(node.Value.Key);
                    _waiting.Remove(node);
                    ready.Add(node.Value.Ready);
                }
                node = next;
            }
        }
        foreach (TaskCompletionSource r in ready) r.TrySetResult();
    }

    /// <summary>The gate one run holds: a slot for its session key, taken now or when the run starts.</summary>
    public sealed class Gate(TriggerSlots slots, string? key, bool entered, Action onEntered, Action onAbandoned) : IRunGate
    {
        private int _state = entered ? 1 : 0;   // 0 = waiting, 1 = holding, 2 = released

        public bool IsFree => Volatile.Read(ref _state) == 1 || slots.IsFree(key);

        public string Describe() => $"a concurrency slot of trigger '{slots.TriggerId}'" + (key is null ? "" : $" (session {key})");

        public async ValueTask EnterAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _state) != 0) return;
            await slots.TakeAsync(key, cancellationToken);
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) { slots.Release(key); return; }
            onEntered();
        }

        public void Exit()
        {
            switch (Interlocked.Exchange(ref _state, 2))
            {
                case 1: slots.Release(key); break;
                case 0: onAbandoned(); break;   // the run ended (cancelled) before it got a slot
            }
        }
    }
}
