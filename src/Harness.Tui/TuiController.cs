using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Client;
using Harness.Tui.State;

namespace Harness.Tui;

/// <summary>The main pane's screens.</summary>
public enum Screen { Session, Runs, RunDetail, Approvals, Triggers }

/// <summary>How the TUI was opened.</summary>
public sealed class TuiOptions
{
    public Screen Start { get; init; } = Screen.Session;
    /// <summary>Open this session straight away (<c>harness chat --resume</c>).</summary>
    public string? SessionId { get; init; }
    /// <summary>Open this run's detail view (<c>harness runs attach</c>).</summary>
    public string? RunId { get; init; }
    /// <summary>How to create a session when the first message is sent and none is open (the current folder, locally).</summary>
    public CreateSessionRequest NewSession { get; init; } = new();
    /// <summary>Resume the most recent session in <see cref="NewSession"/>'s workspace instead of starting a new one.</summary>
    public bool Continue { get; init; }
    public string Theme { get; init; } = "dark";
    public Keymap Keymap { get; init; } = Keymap.Default();
    /// <summary>Folder that <c>e</c> and <c>/export</c> write transcripts to.</summary>
    public string ExportDirectory { get; init; } = Directory.GetCurrentDirectory();
}

/// <summary>
/// Everything the TUI does that is not drawing: it holds the one firehose subscription to <c>/api/events</c>, follows
/// the open session's runs (journal replay, then live, resuming with <c>Last-Event-ID</c>), and turns user commands into
/// API calls. State changes happen on the UI thread through <c>post</c>; <see cref="Changed"/> tells the views to redraw.
/// Tested against a real daemon with a synchronous <c>post</c>.
/// </summary>
public sealed class TuiController : IAsyncDisposable
{
    private readonly HarnessClient _client;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, CancellationTokenSource> _following = [];
    private readonly List<Task> _background = [];
    private CancellationTokenSource? _detailCts;
    private DateTime _lastInterrupt = DateTime.MinValue;
    private bool _refreshing;

    public TuiController(HarnessClient client, TuiOptions options, Action<Action> post)
    {
        _client = client;
        Options = options;
        _post = post;
        Screen = options.Start;
        PendingSession = options.NewSession;
    }

    public TuiOptions Options { get; }
    public TuiStore Store { get; } = new();
    public ComposerModel Composer { get; } = new();
    public Screen Screen { get; set; }
    public string? OpenSessionId { get; private set; }
    /// <summary>The session the first message will create when none is open.</summary>
    public CreateSessionRequest PendingSession { get; private set; }
    public string? DetailRunId { get; private set; }
    public List<EventDto> DetailEvents { get; } = [];
    public bool Connected { get; private set; }
    public string? Flash { get; private set; }
    public DateTime FlashAt { get; private set; }
    public bool QuitRequested { get; private set; }

    /// <summary>Raised on the UI thread after any state change.</summary>
    public event Action? Changed;

    public TranscriptModel? Transcript => OpenSessionId is null ? null : Store.Transcript(OpenSessionId);
    public SessionDto? OpenSession => OpenSessionId is null ? null : Store.Session(OpenSessionId);
    public RunDto? ActiveRun => OpenSessionId is null ? null : Store.ActiveRunOf(OpenSessionId);

    private void Notify() => Changed?.Invoke();

    private void Post(Action action) => _post(() =>
    {
        action();
        Notify();
    });

    public void Say(string message)
    {
        Flash = message;
        FlashAt = DateTime.UtcNow;
        Notify();
    }

    // ---- start-up and the firehose ----

    public async Task StartAsync()
    {
        await RefreshAsync();
        Connected = true;
        if (Options.SessionId is { } sid) await OpenSessionAsync(sid);
        else if (Options.Continue) await ContinueLatestAsync();
        if (Options.RunId is { } rid) await OpenRunAsync(rid);
        Track(Task.Run(FirehoseAsync));
    }

    private async Task ContinueLatestAsync()
    {
        CreateSessionRequest p = PendingSession;
        SessionDto? last = Store.Sessions
            .Where(s => p.WorkspaceName is not null ? s.WorkspaceName == p.WorkspaceName : s.Workspace == p.Workspace)
            .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        if (last is not null) await OpenSessionAsync(last.Id);
    }

    /// <summary>Reloads the lists. Events are the primary source; this catches up after start-up and reconnects.</summary>
    public async Task RefreshAsync()
    {
        CancellationToken ct = _cts.Token;
        Task<StatusDto> status = _client.StatusAsync(ct);
        Task<IReadOnlyList<SessionDto>> sessions = _client.SessionsAsync(limit: 300, ct: ct);
        Task<IReadOnlyList<RunDto>> runs = _client.RunsAsync(limit: 100, ct: ct);
        Task<IReadOnlyList<ApprovalDto>> approvals = _client.ApprovalsAsync(ct);
        Task<IReadOnlyList<TriggerDto>> triggers = _client.TriggersAsync(ct);
        Task<IReadOnlyList<WorkspaceDto>> workspaces = _client.WorkspacesAsync(ct);
        await Task.WhenAll(status, sessions, runs, approvals, triggers, workspaces);
        Post(() =>
        {
            Store.LoadStatus(status.Result);
            Store.LoadSessions(sessions.Result);
            Store.LoadRuns(runs.Result);
            Store.LoadApprovals(approvals.Result);
            Store.LoadTriggers(triggers.Result);
            Store.LoadWorkspaces(workspaces.Result);
        });
    }

    private async Task FirehoseAsync()
    {
        CancellationToken ct = _cts.Token;
        TimeSpan backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (EventDto e in _client.EventsAsync(null, Store.LastHubSeq, ct))
                {
                    backoff = TimeSpan.FromSeconds(1);
                    Post(() => OnFirehose(e));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or HarnessApiException or JsonException) { }
            if (ct.IsCancellationRequested) return;
            Post(() =>
            {
                Connected = false;
                Say("lost the connection to the daemon; reconnecting…");
            });
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(15, backoff.TotalSeconds * 2));
            try
            {
                await RefreshAsync();
                Post(() =>
                {
                    Connected = true;
                    Say("reconnected");
                    if (OpenSessionId is { } id) foreach (RunDto r in Store.RunsOf(id).Where(r => TuiStore.ActiveStates.Contains(r.State))) FollowRun(r.Id);
                });
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or HarnessApiException) { }
        }
    }

    private void OnFirehose(EventDto e)
    {
        Store.Apply(e);
        if (Store.NeedsRefresh && !_refreshing)
        {
            Store.NeedsRefresh = false;
            _refreshing = true;
            Track(Task.Run(async () =>
            {
                try { IReadOnlyList<SessionDto> s = await _client.SessionsAsync(limit: 300, ct: _cts.Token); Post(() => Store.LoadSessions(s)); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or HarnessApiException or OperationCanceledException) { }
                finally { _post(() => _refreshing = false); }
            }));
        }
        if (e.Type == "RUN_STARTED" && e.SessionId == OpenSessionId && e.RunId is not null) FollowRun(e.RunId);
        // The daemon titles a session after its first message; pick that up.
        if (e.Type is "RUN_STARTED" or "RUN_FINISHED" && e.SessionId is { } sid && Store.Session(sid) is { Title: null }) RefreshSessionSoon(sid);
        if (e.Type == "RUN_FINISHED" && Store.Run(e.RunId ?? "")?.TriggerId is not null) RefreshTriggersSoon();
        if (e.Type == "RUN_FINISHED" && e.SessionId == OpenSessionId && Composer.Queued.Count > 0 && ActiveRun is null)
        {
            string next = Composer.Queued.Dequeue();
            Track(SendNowAsync(next));
        }
    }

    private void RefreshSessionSoon(string sessionId) => Track(Task.Run(async () =>
    {
        try { SessionDto s = await _client.SessionAsync(sessionId, _cts.Token); Post(() => Store.UpsertSession(s)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or HarnessApiException or OperationCanceledException) { }
    }));

    private void RefreshTriggersSoon() => Track(Task.Run(async () =>
    {
        try { IReadOnlyList<TriggerDto> t = await _client.TriggersAsync(_cts.Token); Post(() => Store.LoadTriggers(t)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or HarnessApiException or OperationCanceledException) { }
    }));

    private void Track(Task task)
    {
        lock (_background)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    // ---- sessions and transcripts ----

    /// <summary>Opens a session: the latest history page, then a replay of each active run's events, then live events.</summary>
    public async Task OpenSessionAsync(string sessionId)
    {
        SessionDto session = await _client.SessionAsync(sessionId, _cts.Token);
        IReadOnlyList<RunDto> runs = await _client.RunsAsync(sessionId: sessionId, limit: 20, ct: _cts.Token);
        MessagesPage page = await _client.MessagesAsync(sessionId, null, 100, _cts.Token);
        Post(() =>
        {
            foreach (string old in _following.Keys.ToList()) StopFollowing(old);
            Store.UpsertSession(session);
            Store.LoadRuns(runs);
            OpenSessionId = sessionId;
            Screen = Screen.Session;
            HashSet<string> active = [.. runs.Where(r => TuiStore.ActiveStates.Contains(r.State)).Select(r => r.Id)];
            TranscriptModel t = Store.Transcript(sessionId);
            t.LoadHistory(page, active);
            foreach (string runId in active) FollowRun(runId);
        });
    }

    /// <summary>Loads the next older history page, as the user scrolls up.</summary>
    public async Task LoadOlderAsync()
    {
        if (Transcript is not { HasMoreBefore: true, OldestSeq: long before } t) return;
        await Guard(async () =>
        {
            MessagesPage page = await _client.MessagesAsync(t.SessionId, before, 100, _cts.Token);
            Post(() => t.PrependHistory(page));
        });
    }

    /// <summary>Follows one run of the open session: its journal from the start, then live until it finishes, resuming after drops.</summary>
    private void FollowRun(string runId)
    {
        if (_following.ContainsKey(runId) || OpenSessionId is not { } sessionId) return;
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _following[runId] = cts;
        TranscriptModel transcript = Store.Transcript(sessionId);
        transcript.BeginReplay(runId);
        Track(Task.Run(async () =>
        {
            long? after = null;
            for (int attempt = 0; attempt < 30 && !cts.IsCancellationRequested; attempt++)
            {
                try
                {
                    await foreach (EventDto e in _client.RunEventsAsync(runId, after, cts.Token))
                    {
                        if (e.Type != "TEXT_MESSAGE_CONTENT") after = e.Seq;
                        Post(() => transcript.Apply(e));
                        if (e.Type == "RUN_FINISHED") return;
                    }
                    return;   // the stream ended: the run is final
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { }
                catch (HarnessApiException) { return; }
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + attempt)), cts.Token); } catch (OperationCanceledException) { return; }
            }
        }).ContinueWith(_ => _post(() => _following.Remove(runId)), TaskScheduler.Default));
    }

    private void StopFollowing(string runId)
    {
        if (_following.Remove(runId, out CancellationTokenSource? cts)) cts.Cancel();
    }

    /// <summary>Starts a new, empty session in the same workspace as the open one; it is created when the first message is sent.</summary>
    public void NewSession(string? agent = null, string? model = null)
    {
        SessionDto? current = OpenSession;
        CreateSessionRequest basis = current is null
            ? PendingSession
            : new CreateSessionRequest(current.Agent, current.WorkspaceName is null ? current.Workspace : null, current.WorkspaceName, Model: null);
        PendingSession = basis with { Agent = agent ?? basis.Agent, Model = model ?? basis.Model };
        foreach (string old in _following.Keys.ToList()) StopFollowing(old);
        OpenSessionId = null;
        Screen = Screen.Session;
        Notify();
    }

    /// <summary>Sends a message (or runs a slash command). While a turn is running, plain messages are queued until it ends.</summary>
    public async Task<string?> SubmitAsync(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) && Composer.Attachments.Count == 0) return null;
        if (ComposerModel.ParseSlash(raw) is { } slash) return await SlashAsync(slash);
        string text = Composer.Compose(raw);
        Composer.Remember(raw.Trim());
        if (ActiveRun is not null)
        {
            Composer.Queued.Enqueue(text);
            Say($"queued; sent when the turn ends ({Composer.Queued.Count} waiting)");
            return null;
        }
        await SendNowAsync(text);
        return null;
    }

    private async Task SendNowAsync(string text)
    {
        try
        {
            if (OpenSessionId is null)
            {
                SessionDto created = await _client.CreateSessionAsync(PendingSession, _cts.Token);
                Post(() =>
                {
                    Store.UpsertSession(created);
                    OpenSessionId = created.Id;
                    Store.Transcript(created.Id).LoadHistory(new MessagesPage([], false));
                });
            }
            string sessionId = OpenSessionId!;
            SendMessageResponse sent = await _client.SendAsync(sessionId, text, _cts.Token);
            Post(() =>
            {
                if (Store.Run(sent.RunId) is null)
                {
                    SessionDto? s = Store.Session(sessionId);
                    Store.UpsertRun(new RunDto(sent.RunId, sessionId, s?.Agent ?? "", s?.Model ?? "", null, "queued", DateTimeOffset.UtcNow,
                        null, null, 0, 0, null, null, null));
                }
                if (OpenSessionId == sessionId) FollowRun(sent.RunId);
            });
        }
        catch (Exception ex) when (ex is HarnessApiException or HttpRequestException or IOException)
        {
            Post(() => Say("could not send: " + ex.Message));
        }
    }

    private async Task<string?> SlashAsync(SlashCommand slash)
    {
        switch (slash.Name)
        {
            case "new":
                NewSession();
                Say("new session: type a message to start it");
                return null;
            case "agent" or "model" when slash.Argument.Length == 0:
                Say($"usage: /{slash.Name} <name>");
                return null;
            case "agent":
                NewSession(agent: slash.Argument);
                Say($"new session with agent {slash.Argument}: type a message to start it");
                return null;
            case "model":
                NewSession(model: slash.Argument);
                Say($"new session with model {slash.Argument}: type a message to start it");
                return null;
            case "fork":
                await ForkAsync(OpenSessionId, null);
                return null;
            case "export":
                await ExportAsync(OpenSessionId);
                return null;
            case "usage":
                if (OpenSession is { } s)
                    Say($"{s.Id}: {Format.Tokens(s.InputTokens)} in / {Format.Tokens(s.OutputTokens)} out over {Store.RunsOf(s.Id).Count} recent runs · last call {Format.Tokens(Transcript?.LastInputTokens ?? 0)} input tokens");
                else Say("no session yet");
                return null;
            case "compact" or "approvals":
                Say($"/{slash.Name} is not available yet: the daemon has no API for it (compaction runs automatically at the start of a turn)");
                return null;
            case "help":
                return "help";
            default:
                Say($"unknown command /{slash.Name}; type / to see the list");
                return null;
        }
    }

    public async Task ForkAsync(string? sessionId, long? atSeq)
    {
        if (sessionId is null) { Say("nothing to fork yet"); return; }
        await Guard(async () =>
        {
            SessionDto fork = await _client.ForkAsync(sessionId, new ForkRequest(atSeq), _cts.Token);
            Post(() => Store.UpsertSession(fork));
            await OpenSessionAsync(fork.Id);
            Post(() => Say(atSeq is null ? $"forked into {fork.Id}" : $"forked at #{atSeq} into {fork.Id}"));
        });
    }

    public async Task ExportAsync(string? sessionId)
    {
        if (sessionId is null) { Say("nothing to export yet"); return; }
        await Guard(async () =>
        {
            string markdown = await _client.ExportAsync(sessionId, "md", _cts.Token);
            string path = Path.Combine(Options.ExportDirectory, $"{sessionId}.md");
            await File.WriteAllTextAsync(path, markdown, _cts.Token);
            Post(() => Say($"exported to {path}"));
        });
    }

    public async Task RenameAsync(string sessionId, string title) => await Guard(async () =>
    {
        SessionDto s = await _client.RenameSessionAsync(sessionId, title, _cts.Token);
        Post(() => Store.UpsertSession(s));
    });

    public async Task DeleteAsync(string sessionId) => await Guard(async () =>
    {
        await _client.DeleteSessionAsync(sessionId, _cts.Token);
        Post(() =>
        {
            Store.RemoveSession(sessionId);
            if (OpenSessionId == sessionId) NewSession();
            Say($"deleted {sessionId}");
        });
    });

    public Task<IReadOnlyList<string>> CompleteFilesAsync(string query, CancellationToken ct)
    {
        if (OpenSessionId is { } id) return _client.SessionFilesAsync(id, query, ct);
        if (PendingSession.WorkspaceName is { } name) return _client.WorkspaceFilesAsync(name, query, ct);
        return Task.FromResult(LocalFiles(PendingSession.Workspace, query));
    }

    /// <summary>Before the first message there is no session to ask the daemon about; a local folder is matched here.</summary>
    private static IReadOnlyList<string> LocalFiles(string? root, string query)
    {
        if (root is null || !Directory.Exists(root)) return [];
        List<(int Score, string Path)> hits = [];
        int seen = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            if (++seen > 20_000) break;
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith(".git/", StringComparison.Ordinal) || rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("node_modules/")) continue;
            if (Fuzzy.Score(rel, query) is int s) hits.Add((s, rel));
        }
        return [.. hits.OrderBy(h => h.Score).ThenBy(h => h.Path.Length).Take(20).Select(h => h.Path)];
    }

    // ---- runs and approvals ----

    public async Task DecideAsync(string runId, string requestId, bool approved, string? reason = null, bool always = false) => await Guard(async () =>
    {
        try
        {
            await _client.DecideAsync(runId, requestId, new ApprovalDecisionRequest(approved, reason, always, Environment.UserName), _cts.Token);
            Post(() => Say(approved ? (always ? "approved for the rest of the session" : "approved") : "denied"));
        }
        catch (HarnessApiException ex) when (ex.Status == System.Net.HttpStatusCode.Conflict)
        {
            Post(() => Say("already answered elsewhere"));
        }
    });

    public async Task CancelRunAsync(string runId) => await Guard(async () =>
    {
        await _client.CancelAsync(runId, _cts.Token);
        Post(() => Say($"cancelling {runId}"));
    });

    /// <summary>
    /// Ctrl+C: cancels the open session's running turn (or the attached run); with nothing to cancel, or pressed again
    /// within two seconds, it quits.
    /// </summary>
    public async Task InterruptAsync()
    {
        bool again = (DateTime.UtcNow - _lastInterrupt).TotalSeconds < 2;
        _lastInterrupt = DateTime.UtcNow;
        if (again)
        {
            QuitRequested = true;
            Notify();
            return;
        }
        string? target = Screen == Screen.RunDetail && DetailRunId is { } d && Store.Run(d) is { } r && TuiStore.ActiveStates.Contains(r.State)
            ? d
            : Screen == Screen.Session ? ActiveRun?.Id : null;
        if (target is not null)
        {
            Composer.Queued.Clear();
            await CancelRunAsync(target);
            Say(Screen == Screen.RunDetail ? "cancelling the run · Ctrl+C again to detach" : "cancelling the turn · Ctrl+C again to quit");
        }
        else Say(Screen == Screen.RunDetail ? "Ctrl+C again to detach" : "Ctrl+C again to quit");
    }

    public void Quit()
    {
        QuitRequested = true;
        Notify();
    }

    /// <summary>Opens a run's detail view: its whole event log replayed, then live.</summary>
    public async Task OpenRunAsync(string runId)
    {
        _detailCts?.Cancel();
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _detailCts = cts;
        RunDto run = await _client.RunAsync(runId, cts.Token);
        Post(() =>
        {
            Store.UpsertRun(run);
            DetailRunId = runId;
            DetailEvents.Clear();
            Screen = Screen.RunDetail;
        });
        Track(Task.Run(async () =>
        {
            long? after = null;
            for (int attempt = 0; attempt < 30 && !cts.IsCancellationRequested; attempt++)
            {
                try
                {
                    await foreach (EventDto e in _client.RunEventsAsync(runId, after, cts.Token))
                    {
                        if (e.Type == "TEXT_MESSAGE_CONTENT") continue;
                        after = e.Seq;
                        Post(() =>
                        {
                            if (DetailRunId == runId && DetailEvents.All(x => x.Seq != e.Seq)) DetailEvents.Add(e);
                        });
                    }
                    // Final: pick up the stored output and files.
                    RunDto done = await _client.RunAsync(runId, cts.Token);
                    Post(() => Store.UpsertRun(done));
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { }
                catch (HarnessApiException) { return; }
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 + attempt)), cts.Token); } catch (OperationCanceledException) { return; }
            }
        }));
    }

    // ---- triggers ----

    /// <summary>Fires a trigger. Input that parses as a JSON object becomes the inputs; anything else is the event text.</summary>
    public async Task FireTriggerAsync(string id, string input) => await Guard(async () =>
    {
        JsonObject? inputs = null;
        string? text = input.Length == 0 ? null : input;
        if (input.TrimStart().StartsWith('{'))
        {
            try
            {
                inputs = JsonNode.Parse(input) as JsonObject;
                text = null;
            }
            catch (JsonException) { }
        }
        SendMessageResponse sent = await _client.FireTriggerAsync(id, new FireTriggerRequest(text, inputs), _cts.Token);
        Post(() => Say($"fired {id}: run {sent.RunId}"));
        RefreshTriggersSoon();
    });

    public async Task ToggleTriggerAsync(TriggerDto trigger) => await Guard(async () =>
    {
        await _client.SetTriggerEnabledAsync(trigger.Id, !trigger.Enabled, _cts.Token);
        IReadOnlyList<TriggerDto> all = await _client.TriggersAsync(_cts.Token);
        Post(() =>
        {
            Store.LoadTriggers(all);
            Say($"{trigger.Id} {(trigger.Enabled ? "disabled" : "enabled")}");
        });
    });

    private async Task Guard(Func<Task> body)
    {
        try { await body(); }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HarnessApiException or HttpRequestException or IOException)
        {
            Post(() => Say("error: " + ex.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Task[] pending;
        lock (_background) pending = [.. _background];
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
