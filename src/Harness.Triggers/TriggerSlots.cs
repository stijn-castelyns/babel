using Harness.Runs;

namespace Harness.Triggers;

/// <summary>
/// A trigger's <c>concurrency:</c> limits: at most <c>global</c> runs of the trigger at once, and at most <c>perSession</c>
/// runs per session key. Places in line are reserved when a run is created, so waiting runs get a slot in arrival order, as
/// soon as one that fits their key is free.
/// </summary>
public sealed class TriggerSlots(string triggerId)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _perKey = new(StringComparer.Ordinal);
    private readonly LinkedList<Ticket> _waiting = new();
    private int _active;

    public string TriggerId { get; } = triggerId;
    public int? Global { get; set; }
    public int? PerSession { get; set; }

    public int Active { get { lock (_gate) return _active; } }

    /// <summary>A place in line for one run; <see cref="Ready"/> completes when the slot is the run's.</summary>
    public sealed class Ticket(string? key)
    {
        public string? Key { get; } = key;
        internal TaskCompletionSource ReadySource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Ticket>? Node { get; set; }
        public Task Ready => ReadySource.Task;
    }

    // Invariant: after every change, no waiting ticket fits (Release hands out every slot it can), so a newcomer that fits
    // is not jumping the line.
    private bool Fits(string? key) =>
        (Global is not int g || _active < g) && (key is null || PerSession is not int p || _perKey.GetValueOrDefault(key) < p);

    private void Take(string? key)
    {
        _active++;
        if (key is not null) _perKey[key] = _perKey.GetValueOrDefault(key) + 1;
    }

    /// <summary>Takes a slot now if one fits, without queueing (for <c>onBusy: drop</c>).</summary>
    public bool TryTake(string? key)
    {
        lock (_gate)
        {
            if (!Fits(key)) return false;
            Take(key);
            return true;
        }
    }

    /// <summary>Gets in line: the ticket is ready at once when a slot fits, otherwise when one frees up.</summary>
    public Ticket Reserve(string? key)
    {
        Ticket ticket = new(key);
        lock (_gate)
        {
            if (Fits(key))
            {
                Take(key);
                ticket.ReadySource.TrySetResult();
            }
            else ticket.Node = _waiting.AddLast(ticket);
        }
        return ticket;
    }

    /// <summary>Gives a ticket back: leaves the line, or frees the slot if it was already granted.</summary>
    public void Cancel(Ticket ticket)
    {
        lock (_gate)
        {
            if (ticket.Node?.List is not null)
            {
                _waiting.Remove(ticket.Node);
                return;
            }
        }
        if (ticket.Ready.IsCompleted) Release(ticket.Key);
    }

    public void Release(string? key)
    {
        List<Ticket> ready = [];
        lock (_gate)
        {
            _active--;
            if (key is not null && _perKey.TryGetValue(key, out int n)) { if (n <= 1) _perKey.Remove(key); else _perKey[key] = n - 1; }
            for (LinkedListNode<Ticket>? node = _waiting.First; node is not null;)
            {
                LinkedListNode<Ticket>? next = node.Next;
                if (Fits(node.Value.Key))
                {
                    Take(node.Value.Key);
                    _waiting.Remove(node);
                    ready.Add(node.Value);
                }
                node = next;
            }
        }
        foreach (Ticket t in ready) t.ReadySource.TrySetResult();
    }

    /// <summary>The gate one run holds: a slot for its session key, taken now (<c>drop</c>) or reserved in line (<c>queue</c>).</summary>
    public sealed class Gate : IRunGate
    {
        private readonly TriggerSlots _slots;
        private readonly string? _key;
        private readonly Ticket? _ticket;
        private readonly Action _onEntered, _onAbandoned;
        private int _state;   // 0 = waiting, 1 = holding, 2 = released

        /// <param name="entered">True when the slot was already taken with <see cref="TryTake"/>; otherwise a place in line is reserved now.</param>
        public Gate(TriggerSlots slots, string? key, bool entered, Action onEntered, Action onAbandoned)
        {
            _slots = slots;
            _key = key;
            _onEntered = onEntered;
            _onAbandoned = onAbandoned;
            if (entered) _state = 1;
            else _ticket = slots.Reserve(key);
        }

        public bool IsFree => Volatile.Read(ref _state) == 1 || _ticket?.Ready.IsCompleted == true;

        public string Describe() => $"a concurrency slot of trigger '{_slots.TriggerId}'" + (_key is null ? "" : $" (session {_key})");

        public async ValueTask EnterAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _state) != 0) return;
            await _ticket!.Ready.WaitAsync(cancellationToken);
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;   // exited meanwhile; Exit gave the ticket back
            _onEntered();
        }

        public void Exit()
        {
            switch (Interlocked.Exchange(ref _state, 2))
            {
                case 1:
                    _slots.Release(_key);
                    break;
                case 0:
                    // The run ended (cancelled) before it held the slot: leave the line, or hand back a slot granted meanwhile.
                    _slots.Cancel(_ticket!);
                    _onAbandoned();
                    break;
            }
        }
    }
}
