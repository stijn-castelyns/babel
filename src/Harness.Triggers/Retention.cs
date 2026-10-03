using Harness.Core;
using Harness.Core.Config;
using Harness.Core.Sessions;
using Harness.Runs;
using Harness.Runs.Templates;
using Harness.Sdk;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harness.Triggers;

/// <summary>What one retention sweep removed (or would remove, in a dry run).</summary>
public sealed record RetentionReport(IReadOnlyList<string> Sessions, IReadOnlyList<string> RunFolders, int Events, bool DryRun);

/// <summary>
/// Applies retention policies: sessions idle for longer than their age (<c>retention.sessions</c>, a trigger's own
/// <c>retention.sessions</c>, or <c>retention.interactiveSessions</c>), run folders of runs finished longer ago than
/// <c>retention.runs</c>, and handled trigger events older than <c>retention.events</c>. Sessions with an active or parked
/// run are never touched. The daemon sweeps every <c>retention.interval</c>; <c>harness sessions prune</c> sweeps on demand.
/// </summary>
public sealed class Retention(ConfigCatalog catalog, SessionStore sessions, RunOrchestrator runs, TriggerEngine triggers, TriggerQueue queue,
    WorkspaceBuilder workspaces, ILoggerFactory? loggerFactory = null) : BackgroundService
{
    private readonly ILogger _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Retention>();
    private readonly SemaphoreSlim _sweeping = new(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);   // let parked runs come back first
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan interval = TimeSpan.FromHours(1);
            try
            {
                interval = RetentionConfig.Age(catalog.Config.Retention.Interval, "retention.interval") ?? TimeSpan.FromHours(1);
                RetentionReport report = await SweepAsync(dryRun: false, stoppingToken);
                if (report.Sessions.Count + report.RunFolders.Count + report.Events > 0)
                    _log.LogInformation("Retention removed {Sessions} session(s), {Runs} run folder(s) and {Events} trigger event(s)",
                        report.Sessions.Count, report.RunFolders.Count, report.Events);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogError(ex, "Retention sweep failed"); }
            await Task.Delay(interval, stoppingToken);
        }
    }

    public async Task<RetentionReport> SweepAsync(bool dryRun, CancellationToken ct)
    {
        await _sweeping.WaitAsync(ct);
        try { return Sweep(dryRun, DateTimeOffset.UtcNow); }
        finally { _sweeping.Release(); }
    }

    private RetentionReport Sweep(bool dryRun, DateTimeOffset now)
    {
        RetentionConfig config = catalog.Config.Retention;
        TimeSpan? defaultSessions = RetentionConfig.Age(config.Sessions, "retention.sessions");
        TimeSpan? interactiveSessions = RetentionConfig.Age(config.InteractiveSessions, "retention.interactiveSessions");
        TimeSpan? defaultRuns = RetentionConfig.Age(config.Runs, "retention.runs");
        TimeSpan? events = RetentionConfig.Age(config.Events, "retention.events");

        TimeSpan? SessionAge(string? triggerId) => triggerId is null
            ? interactiveSessions
            : triggers.Get(triggerId)?.Retention.Sessions is { } own ? RetentionConfig.Age(own, "retention.sessions") : defaultSessions;
        TimeSpan? RunAge(string? triggerId) =>
            triggerId is not null && triggers.Get(triggerId)?.Retention.Runs is { } own ? RetentionConfig.Age(own, "retention.runs") : defaultRuns;

        HashSet<string> busySessions = [.. runs.ActiveRuns().Select(r => r.SessionId)];
        HashSet<string> busyRuns = [.. runs.ActiveRuns().Select(r => r.Id)];

        // Sessions first: deleting one also removes its runs' folders and records.
        List<string> deletedSessions = [];
        List<string> deletedFolders = [];
        TimeSpan? shortest = new[] { defaultSessions, interactiveSessions }
            .Concat(triggers.Definitions.Select(d => d.Retention.Sessions is { } s ? RetentionConfig.Age(s, "retention.sessions") : null))
            .Where(a => a is not null).Min();
        if (shortest is TimeSpan minAge)
            foreach ((string id, string? triggerId, DateTimeOffset updatedAt) in sessions.Index.SessionsIdleSince(now - minAge))
            {
                if (SessionAge(triggerId) is not TimeSpan age || updatedAt >= now - age || busySessions.Contains(id)) continue;
                // A run parked on an approval is not final either (and is only rehydrated once the daemon has started).
                IReadOnlyList<RunRecord> sessionRuns = sessions.Index.ListRuns(sessionId: id, limit: 10_000);
                if (sessionRuns.Any(r => !RunStates.IsFinal(r.State))) continue;
                if (dryRun)
                {
                    deletedSessions.Add(id);
                    deletedFolders.AddRange(sessionRuns.Select(r => r.Id).Where(r => Directory.Exists(workspaces.RunDirectory(r))));
                    continue;
                }
                try
                {
                    if (!sessions.Delete(id)) continue;
                }
                catch (SessionBusyException) { continue; }
                catch (IOException ex) { _log.LogWarning(ex, "Retention could not delete session {Id}", id); continue; }
                deletedSessions.Add(id);
                foreach (string runId in sessions.Index.DeleteRunsOfSession(id))
                    if (DeleteFolder(runId)) deletedFolders.Add(runId);
            }

        // Run folders of finished runs, and folders whose run record is gone.
        if (Directory.Exists(catalog.Paths.RunsDir))
            foreach (string dir in Directory.EnumerateDirectories(catalog.Paths.RunsDir))
            {
                string runId = Path.GetFileName(dir);
                if (runId == "scratch" || busyRuns.Contains(runId) || deletedFolders.Contains(runId)) continue;
                RunRecord? record = sessions.Index.GetRun(runId);
                if (record is not null && !RunStates.IsFinal(record.State)) continue;
                if (RunAge(record?.TriggerId) is not TimeSpan age) continue;
                DateTimeOffset finished = record?.FinishedAt ?? Directory.GetLastWriteTimeUtc(dir);
                if (finished >= now - age) continue;
                if (dryRun || DeleteFolder(runId)) deletedFolders.Add(runId);
            }

        int eventCount = events is TimeSpan eventAge && !dryRun ? queue.DeleteHandledBefore(now - eventAge) : 0;
        return new RetentionReport(deletedSessions, deletedFolders, eventCount, dryRun);
    }

    private bool DeleteFolder(string runId)
    {
        string dir = workspaces.RunDirectory(runId);
        if (!Directory.Exists(dir)) return false;
        try
        {
            WorkspaceBuilder.DeleteTree(dir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Retention could not delete run folder {Dir}", dir);
            return false;
        }
    }
}
