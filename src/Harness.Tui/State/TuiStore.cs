using System.Text.Json.Nodes;
using Harness.Client;

namespace Harness.Tui.State;

/// <summary>
/// The TUI's in-memory store. API responses and firehose events both go through <see cref="Apply(EventDto)"/> and the
/// <c>Load*</c> methods; views render from it, so badges, the runs list, the approval count and the open transcript stay
/// in sync with work started anywhere. Pure state: tests replay recorded event streams into it.
/// </summary>
public sealed class TuiStore
{
    public static readonly IReadOnlySet<string> ActiveStates = new HashSet<string> { "queued", "preparing", "running", "awaiting_approval", "validating", "delivering" };

    private readonly Dictionary<string, SessionDto> _sessions = [];
    private readonly Dictionary<string, RunDto> _runs = [];
    private readonly Dictionary<string, ApprovalDto> _approvals = [];
    private readonly Dictionary<string, TranscriptModel> _transcripts = [];

    public IReadOnlyList<TriggerDto> Triggers { get; private set; } = [];
    public IReadOnlyList<WorkspaceDto> Workspaces { get; private set; } = [];
    public StatusDto? Status { get; private set; }
    /// <summary>Bumped on every change.</summary>
    public int Version { get; private set; }
    /// <summary>Set when an event names a session or run the store does not know, so the shell refreshes the lists.</summary>
    public bool NeedsRefresh { get; set; }
    /// <summary>Last firehose sequence number seen, for resuming with <c>Last-Event-ID</c>.</summary>
    public long? LastHubSeq { get; private set; }

    public IEnumerable<SessionDto> Sessions => _sessions.Values;
    public IEnumerable<RunDto> Runs => _runs.Values;

    /// <summary>Pending approvals, oldest first.</summary>
    public IReadOnlyList<ApprovalDto> Approvals => [.. _approvals.Values.OrderBy(a => a.RequestedAt)];

    public SessionDto? Session(string id) => _sessions.GetValueOrDefault(id);
    public RunDto? Run(string id) => _runs.GetValueOrDefault(id);

    public TranscriptModel Transcript(string sessionId)
    {
        if (!_transcripts.TryGetValue(sessionId, out TranscriptModel? t)) _transcripts[sessionId] = t = new TranscriptModel(sessionId);
        return t;
    }

    public bool HasTranscript(string sessionId) => _transcripts.ContainsKey(sessionId);

    public void ForgetTranscript(string sessionId) => _transcripts.Remove(sessionId);

    /// <summary>Active and recent runs: active first, then newest.</summary>
    public IReadOnlyList<RunDto> RunsByRecency() =>
        [.. _runs.Values.OrderByDescending(r => ActiveStates.Contains(r.State)).ThenByDescending(r => r.CreatedAt)];

    public IReadOnlyList<RunDto> RunsOf(string sessionId) => [.. _runs.Values.Where(r => r.SessionId == sessionId).OrderBy(r => r.CreatedAt)];

    public RunDto? ActiveRunOf(string sessionId) =>
        _runs.Values.Where(r => r.SessionId == sessionId && ActiveStates.Contains(r.State)).OrderByDescending(r => r.CreatedAt).FirstOrDefault();

    public int ApprovalsWaiting(string? sessionId = null) => sessionId is null ? _approvals.Count : _approvals.Values.Count(a => a.SessionId == sessionId);

    private void Changed() => Version++;

    // ---- loads ----

    public void LoadSessions(IEnumerable<SessionDto> sessions)
    {
        _sessions.Clear();
        foreach (SessionDto s in sessions) _sessions[s.Id] = s;
        Changed();
    }

    public void UpsertSession(SessionDto s)
    {
        _sessions[s.Id] = s;
        Changed();
    }

    public void RemoveSession(string id)
    {
        _sessions.Remove(id);
        _transcripts.Remove(id);
        Changed();
    }

    public void LoadRuns(IEnumerable<RunDto> runs)
    {
        foreach (RunDto r in runs) _runs[r.Id] = r;
        Changed();
    }

    public void UpsertRun(RunDto r)
    {
        _runs[r.Id] = r;
        Changed();
    }

    public void LoadApprovals(IEnumerable<ApprovalDto> approvals)
    {
        _approvals.Clear();
        foreach (ApprovalDto a in approvals) _approvals[a.RequestId] = a;
        Changed();
    }

    public void LoadTriggers(IReadOnlyList<TriggerDto> triggers)
    {
        Triggers = triggers;
        Changed();
    }

    public void LoadWorkspaces(IReadOnlyList<WorkspaceDto> workspaces)
    {
        Workspaces = workspaces;
        Changed();
    }

    public void LoadStatus(StatusDto status)
    {
        Status = status;
        Changed();
    }

    // ---- the reducer ----

    /// <summary>
    /// Applies one firehose event to the runs, approvals and sessions. Transcripts are fed separately from per-run streams
    /// (<see cref="TranscriptModel.Apply"/>), which replay what happened before the TUI looked.
    /// </summary>
    public void Apply(EventDto e, bool fromFirehose = true)
    {
        if (fromFirehose && e.Type != "TEXT_MESSAGE_CONTENT") LastHubSeq = e.Seq;
        if (e.RunId is null) return;
        JsonObject d = e.Data;
        RunDto? run = _runs.GetValueOrDefault(e.RunId);
        switch (e.Type)
        {
            case "RUN_STARTED":
                run ??= new RunDto(e.RunId, e.SessionId ?? "", Str(d, "agent") ?? "", Str(d, "model") ?? "", Str(d, "triggerId"), "queued",
                    e.Ts, null, null, 0, 0, null, null, null);
                _runs[e.RunId] = run;
                if (e.SessionId is not null)
                {
                    if (_sessions.TryGetValue(e.SessionId, out SessionDto? s)) _sessions[e.SessionId] = s with { UpdatedAt = e.Ts };
                    else NeedsRefresh = true;
                }
                break;
            case "RUN_STATE" when run is not null && Str(d, "state") is { } state:
                _runs[e.RunId] = run with { State = state, StartedAt = run.StartedAt ?? (state == "running" || state == "preparing" ? e.Ts : null) };
                break;
            case "TOOL_CALL_START" when run is not null:
                string main = TranscriptItem.MainArgumentOf(d["arguments"] as JsonObject);
                string tool = Str(d, "toolName") ?? "?";
                _runs[e.RunId] = run with { LastTool = main.Length == 0 ? tool : $"{tool}: {Format.OneLine(main, 80)}" };
                break;
            case "USAGE" when run is not null:
                _runs[e.RunId] = run with
                {
                    InputTokens = d["runInputTokens"]?.GetValue<long>() ?? run.InputTokens,
                    OutputTokens = d["runOutputTokens"]?.GetValue<long>() ?? run.OutputTokens,
                };
                break;
            case "APPROVAL_REQUESTED":
                // Policy and hook decisions follow within the same breath; the inbox removes them on APPROVAL_RESOLVED.
                _approvals[Str(d, "requestId") ?? ""] = new ApprovalDto(Str(d, "requestId") ?? "", e.RunId, e.SessionId ?? run?.SessionId ?? "",
                    Str(d, "toolName") ?? "?", Str(d, "toolCallId") ?? "", d["arguments"]?.DeepClone() as JsonObject ?? [], Str(d, "summary"), e.Ts);
                if (run is not null) _runs[e.RunId] = run with { State = "awaiting_approval" };
                break;
            case "APPROVAL_RESOLVED":
                _approvals.Remove(Str(d, "requestId") ?? "");
                if (run is not null && run.State == "awaiting_approval" && !_approvals.Values.Any(a => a.RunId == e.RunId))
                    _runs[e.RunId] = run with { State = "running" };
                break;
            case "RUN_FINISHED":
                run ??= new RunDto(e.RunId, e.SessionId ?? "", "", "", null, "", e.Ts, null, null, 0, 0, null, null, null);
                _runs[e.RunId] = run with
                {
                    State = Str(d, "state") ?? "failed", FinishedAt = e.Ts, Error = Str(d, "error"), ResultText = Str(d, "text"),
                    InputTokens = d["inputTokens"]?.GetValue<long>() ?? run.InputTokens,
                    OutputTokens = d["outputTokens"]?.GetValue<long>() ?? run.OutputTokens,
                    Output = d["output"]?.DeepClone(),
                };
                foreach (string id in _approvals.Values.Where(a => a.RunId == e.RunId).Select(a => a.RequestId).ToList()) _approvals.Remove(id);
                if (e.SessionId is not null && _sessions.TryGetValue(e.SessionId, out SessionDto? done))
                    _sessions[e.SessionId] = done with
                    {
                        UpdatedAt = e.Ts,
                        InputTokens = done.InputTokens + (d["inputTokens"]?.GetValue<long>() ?? 0),
                        OutputTokens = done.OutputTokens + (d["outputTokens"]?.GetValue<long>() ?? 0),
                    };
                break;
            default:
                return;
        }
        Changed();
    }

    private static string? Str(JsonObject d, string key) =>
        d[key] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;
}
