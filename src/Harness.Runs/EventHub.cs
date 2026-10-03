using System.Threading.Channels;
using Harness.Sdk;

namespace Harness.Runs;

/// <summary>
/// In-memory fan-out of run events to live subscribers (SSE clients, the TUI, <c>runs watch</c>).
/// Persisted events are also in each session's <c>events.jsonl</c>, which per-run streams replay from.
/// The hub keeps a ring of recent events with its own sequence numbers for the firehose's <c>Last-Event-ID</c>.
/// </summary>
public sealed class EventHub
{
    public sealed record Envelope(long HubSeq, HarnessEvent Event);

    private const int RingSize = 2_000;
    private readonly Lock _gate = new();
    private readonly LinkedList<Envelope> _ring = new();
    private readonly List<Subscription> _subscribers = [];
    private long _hubSeq;

    public void Publish(HarnessEvent evt)
    {
        Subscription[] targets;
        Envelope envelope;
        lock (_gate)
        {
            envelope = new Envelope(++_hubSeq, evt);
            if (EventTypes.IsPersisted(evt.Type))
            {
                _ring.AddLast(envelope);
                if (_ring.Count > RingSize) _ring.RemoveFirst();
            }
            targets = [.. _subscribers];
        }
        foreach (Subscription s in targets)
            if (s.Filter(evt)) s.Channel.Writer.TryWrite(envelope);
    }

    /// <summary>
    /// Subscribes to live events matching <paramref name="filter"/>. With <paramref name="afterHubSeq"/>, buffered events
    /// after that sequence number are delivered first, atomically with the switch to live.
    /// </summary>
    public Subscription Subscribe(Func<HarnessEvent, bool> filter, long? afterHubSeq = null)
    {
        Subscription sub = new(this, filter);
        lock (_gate)
        {
            if (afterHubSeq is long after)
                foreach (Envelope e in _ring)
                    if (e.HubSeq > after && filter(e.Event)) sub.Channel.Writer.TryWrite(e);
            _subscribers.Add(sub);
        }
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (_gate) _subscribers.Remove(sub);
        sub.Channel.Writer.TryComplete();
    }

    public sealed class Subscription(EventHub hub, Func<HarnessEvent, bool> filter) : IDisposable
    {
        internal Func<HarnessEvent, bool> Filter { get; } = filter;
        internal Channel<Envelope> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<Envelope>(
            new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public ChannelReader<Envelope> Reader => Channel.Reader;
        public void Dispose() => hub.Remove(this);
    }
}
