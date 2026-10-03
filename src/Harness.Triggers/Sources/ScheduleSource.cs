using System.Globalization;
using System.Text.Json.Nodes;
using Cronos;
using Harness.Core;
using Harness.Sdk;

namespace Harness.Triggers.Sources;

/// <summary>
/// Cron (with time zone), interval or one-shot <c>at</c>. Missed runs during downtime: <c>skip</c> (default),
/// <c>runOnce</c> or <c>catchUp</c> (at most 10).
/// </summary>
public sealed class ScheduleSource(TriggerQueue queue) : ITriggerSource
{
    public string Type => "schedule";
    private readonly List<CancellationTokenSource> _loops = [];

    public Task StartAsync(TriggerSourceContext context, CancellationToken cancellationToken)
    {
        Schedule schedule = Schedule.Parse(context.Settings);
        CancellationTokenSource cts = new();
        lock (_loops) _loops.Add(cts);
        _ = Task.Run(() => LoopAsync(context, schedule, cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_loops)
        {
            foreach (CancellationTokenSource cts in _loops) cts.Cancel();
            _loops.Clear();
        }
        return Task.CompletedTask;
    }

    private async Task LoopAsync(TriggerSourceContext context, Schedule schedule, CancellationToken ct)
    {
        DateTimeOffset? last = queue.State(context.TriggerId).LastFiredAt;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (last is DateTimeOffset previous && schedule.Missed != "skip")
        {
            List<DateTimeOffset> missed = [.. schedule.Between(previous, now).Take(10)];
            if (schedule.Missed == "runOnce" && missed.Count > 0) missed = [missed[^1]];
            foreach (DateTimeOffset at in missed) await FireAsync(context, at, ct);
        }

        DateTimeOffset cursor = now;
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset? next = schedule.Next(cursor);
            if (next is null) return;
            TimeSpan wait = next.Value - DateTimeOffset.UtcNow;
            // Task.Delay cannot wait longer than ~49 days; re-check periodically for long gaps.
            while (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait, ct);
                wait = next.Value - DateTimeOffset.UtcNow;
            }
            await FireAsync(context, next.Value, ct);
            cursor = next.Value;
        }
    }

    private static ValueTask FireAsync(TriggerSourceContext context, DateTimeOffset at, CancellationToken ct) =>
        context.EmitAsync(new TriggerEvent(
            EventId: "schedule:" + at.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture),
            TriggerId: context.TriggerId,
            ReceivedAt: DateTimeOffset.UtcNow,
            Sender: null,
            Text: null,
            Attachments: [],
            Data: new JsonObject { ["scheduledFor"] = at.ToString("O") },
            ReplyTo: null), ct);

    /// <summary>The next fire time after <paramref name="from"/>, for <c>triggers next</c>.</summary>
    public static DateTimeOffset? NextFire(JsonObject settings, DateTimeOffset from) => Schedule.Parse(settings).Next(from);

    internal sealed record Schedule(CronExpression? Cron, TimeZoneInfo Zone, TimeSpan? Interval, DateTimeOffset? At, string Missed)
    {
        public static Schedule Parse(JsonObject s)
        {
            string? cron = s["cron"]?.GetValue<string>();
            string? interval = s["interval"]?.GetValue<string>();
            string? at = s["at"]?.GetValue<string>();
            TimeZoneInfo zone = s["timeZone"]?.GetValue<string>() is { } tz ? TimeZoneInfo.FindSystemTimeZoneById(tz) : TimeZoneInfo.Utc;
            string missed = s["missed"]?.GetValue<string>() ?? "skip";
            if (missed is not ("skip" or "runOnce" or "catchUp")) throw new FormatException($"missed must be skip, runOnce or catchUp, not '{missed}'.");
            if (cron is null && interval is null && at is null) throw new FormatException("A schedule needs cron, interval or at.");
            return new Schedule(
                cron is null ? null : CronExpression.Parse(cron, cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard),
                zone,
                interval is null ? null : Durations.Parse(interval),
                at is null ? null : DateTimeOffset.Parse(at, CultureInfo.InvariantCulture),
                missed);
        }

        public DateTimeOffset? Next(DateTimeOffset from)
        {
            if (Cron is not null) return Cron.GetNextOccurrence(from, Zone);
            if (Interval is TimeSpan every) return from + every;
            return At is DateTimeOffset at && at > from ? at : null;
        }

        public IEnumerable<DateTimeOffset> Between(DateTimeOffset from, DateTimeOffset to)
        {
            DateTimeOffset cursor = from;
            while (Next(cursor) is DateTimeOffset next && next <= to)
            {
                yield return next;
                cursor = next;
            }
        }
    }
}
