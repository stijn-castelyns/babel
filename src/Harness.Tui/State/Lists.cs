using Harness.Client;

namespace Harness.Tui.State;

/// <summary>A selectable row of a list view: the lines it renders to and what it stands for.</summary>
public sealed record ListRow(Line Line, string? Id = null, string Kind = "", bool Selectable = true);

/// <summary>
/// The session list: grouped by named workspace (or folder), plus a Triggered group, with a fuzzy filter, running and
/// approval badges, and forks shown under their parent.
/// </summary>
public static class SessionTree
{
    public const string Triggered = "Triggered";

    public static IReadOnlyList<ListRow> Rows(TuiStore store, string filter, ISet<string> collapsed, int width, DateTimeOffset now, string? home = null)
    {
        List<SessionDto> sessions = [.. store.Sessions.Where(s => Matches(s, filter))];
        List<ListRow> rows = [];
        var groups = sessions
            .GroupBy(s => s.TriggerId is not null ? Triggered : s.WorkspaceName ?? s.Workspace)
            .OrderBy(g => g.Key == Triggered)
            .ThenByDescending(g => g.Max(s => s.UpdatedAt));
        foreach (var group in groups)
        {
            bool closed = collapsed.Contains(group.Key) && filter.Length == 0;
            SessionDto sample = group.First();
            string path = group.Key == Triggered ? "" : Tilde(sample.Workspace, home);
            string name = group.Key == Triggered ? Triggered : sample.WorkspaceName ?? Path.GetFileName(sample.Workspace.TrimEnd('/')) ?? sample.Workspace;
            LineBuilder header = new(width);
            header.Row([new Span(closed ? "▸ " : "▾ ", Style.Dim), new Span(name, Style.Header), new Span(path.Length > 0 && path != name ? "  " + path : "", Style.Dim)]);
            rows.Add(new ListRow(header.Lines[0], group.Key, "group"));
            if (closed) continue;

            Dictionary<string, SessionDto> byId = group.ToDictionary(s => s.Id);
            HashSet<string> placed = [];
            foreach (SessionDto s in group.OrderByDescending(s => s.UpdatedAt))
            {
                if (s.ParentId is not null && byId.ContainsKey(s.ParentId)) continue;   // shown under its parent
                Add(s, 0);
            }

            void Add(SessionDto s, int depth)
            {
                if (!placed.Add(s.Id)) return;
                rows.Add(new ListRow(SessionLine(store, s, width, depth, now), s.Id, "session"));
                foreach (SessionDto child in group.Where(c => c.ParentId == s.Id).OrderByDescending(c => c.UpdatedAt)) Add(child, depth + 1);
            }
        }
        if (rows.Count == 0)
            rows.Add(new ListRow(Line.Of(filter.Length > 0 ? "  no sessions match" : "  no sessions yet: n starts one", Style.Dim), Selectable: false));
        return rows;
    }

    private static Line SessionLine(TuiStore store, SessionDto s, int width, int depth, DateTimeOffset now)
    {
        (string badge, Style style) = Badge(store, s);
        string title = s.Title ?? s.Key ?? s.Id;
        string indent = depth > 0 ? new string(' ', 2 * (depth - 1)) + "↳ " : "";
        LineBuilder b = new(width);
        b.Row([new Span(badge + " ", style), new Span(indent + title, Style.Normal)], new Span(Format.Ago(s.UpdatedAt, now), Style.Dim));
        return b.Lines[0];
    }

    /// <summary>● running, ! waiting for approval, ✗ last run failed, ✓ last triggered run succeeded.</summary>
    public static (string Badge, Style Style) Badge(TuiStore store, SessionDto s)
    {
        if (store.ApprovalsWaiting(s.Id) > 0) return ("!", Style.Approval);
        RunDto? last = store.RunsOf(s.Id).LastOrDefault();
        if (last is null) return (" ", Style.Normal);
        if (TuiStore.ActiveStates.Contains(last.State)) return last.State == "awaiting_approval" ? ("!", Style.Approval) : ("●", Style.Running);
        if (last.State is "failed" or "invalid_output" or "timed_out") return ("✗", Style.Error);
        return s.TriggerId is not null && last.State == "succeeded" ? ("✓", Style.Ok) : (" ", Style.Normal);
    }

    public static bool Matches(SessionDto s, string filter) =>
        filter.Length == 0 || Fuzzy.Score($"{s.Title} {s.Key} {s.WorkspaceName} {s.Workspace} {s.Id} {s.TriggerId}", filter) is not null;

    public static string Tilde(string path, string? home)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}

/// <summary>Rows for the runs table, the runs sidebar, the approvals inbox and the triggers view.</summary>
public static class ListViews
{
    public static readonly string[] RunHeaders = ["RUN", "TRIGGER", "AGENT", "STATE", "ELAPSED", "TOKENS", "LAST TOOL"];

    public static IReadOnlyList<string> RunCells(RunDto r, DateTimeOffset now) =>
    [
        r.Id, r.TriggerId ?? "manual", r.Agent, r.State, Format.Duration(((r.FinishedAt ?? now) - (r.StartedAt ?? r.CreatedAt)) is { } d && d > TimeSpan.Zero ? d : TimeSpan.Zero),
        Format.Tokens(r.InputTokens + r.OutputTokens), r.LastTool ?? "",
    ];

    public static Style StateStyle(string state) => state switch
    {
        "succeeded" => Style.Ok,
        "awaiting_approval" => Style.Approval,
        "failed" or "invalid_output" or "timed_out" or "rejected" => Style.Error,
        "cancelled" => Style.Dim,
        _ => Style.Running,
    };

    public static IReadOnlyList<ListRow> RunsTable(TuiStore store, string filter, int width, DateTimeOffset now)
    {
        List<RunDto> runs = [.. store.RunsByRecency().Where(r => filter.Length == 0 || Fuzzy.Score($"{r.Id} {r.TriggerId} {r.Agent} {r.State} {r.LastTool}", filter) is not null).Take(200)];
        List<IReadOnlyList<string>> cells = [.. runs.Select(r => RunCells(r, now))];
        int[] widths = Table.Widths(RunHeaders, cells, width, RunHeaders.Length - 1);
        List<ListRow> rows = [new(Table.Row(RunHeaders, widths, Style.Header), Selectable: false)];
        for (int i = 0; i < runs.Count; i++)
        {
            Style state = StateStyle(runs[i].State);
            rows.Add(new ListRow(Table.Row(cells[i], widths, Style.Normal, cellStyles: [Style.Accent, Style.Normal, Style.Normal, state, Style.Dim, Style.Dim, Style.Dim]), runs[i].Id, "run"));
        }
        if (runs.Count == 0) rows.Add(new ListRow(Line.Of("no runs", Style.Dim), Selectable: false));
        return rows;
    }

    /// <summary>The compact runs list in the sidebar: active runs first, then the most recent few.</summary>
    public static IReadOnlyList<ListRow> RunsSidebar(TuiStore store, int width, DateTimeOffset now, int max)
    {
        List<ListRow> rows = [];
        foreach (RunDto r in store.RunsByRecency().Take(max))
        {
            string state = r.State switch { "awaiting_approval" => "approval", "invalid_output" => "invalid", _ => r.State };
            LineBuilder b = new(width);
            TimeSpan elapsed = (r.FinishedAt ?? now) - (r.StartedAt ?? r.CreatedAt);
            b.Row([new Span(Short(r.Id) + "  ", Style.Accent), new Span(state.PadRight(9), StateStyle(r.State)),
                new Span(" " + Format.Brief(elapsed).PadLeft(3), Style.Dim)],
                new Span(Format.Tokens(r.InputTokens + r.OutputTokens), Style.Dim));
            rows.Add(new ListRow(b.Lines[0], r.Id, "run"));
        }
        if (rows.Count == 0) rows.Add(new ListRow(Line.Of("  no runs yet", Style.Dim), Selectable: false));
        return rows;
    }

    public static string Short(string runId) => runId.Length > 8 ? runId[..8] : runId;

    public static IReadOnlyList<ListRow> Approvals(TuiStore store, int width, DateTimeOffset now, string? selectedId)
    {
        List<ListRow> rows = [];
        foreach (ApprovalDto a in store.Approvals)
        {
            SessionDto? s = store.Session(a.SessionId);
            LineBuilder b = new(width);
            b.Row([new Span("! ", Style.Approval), new Span(a.ToolName + " ", Style.Tool), new Span(Format.OneLine(a.Summary ?? a.Arguments.ToJsonString(), 200), Style.Normal)],
                new Span($"{s?.Title ?? a.SessionId} · {Short(a.RunId)} · {Format.Ago(a.RequestedAt, now)}", Style.Dim));
            if (a.RequestId == selectedId)
            {
                // The selected request shows exactly what will run.
                TranscriptItem item = new() { Kind = ItemKind.Approval, ToolName = a.ToolName, Arguments = a.Arguments, Summary = a.Summary };
                foreach (Line l in new TranscriptRenderer().RenderItem(item, width).Skip(1)) b.Add(l);
                b.Row([new Span("    [a]pprove  [d]eny  [A]pprove always for this session  Enter: open the session", Style.Key)]);
            }
            IReadOnlyList<Line> lines = b.Lines;
            for (int i = 0; i < lines.Count; i++) rows.Add(new ListRow(lines[i], a.RequestId, "approval", Selectable: i == 0));
        }
        if (rows.Count == 0) rows.Add(new ListRow(Line.Of("  no approvals waiting", Style.Dim), Selectable: false));
        return rows;
    }

    public static readonly string[] TriggerHeaders = ["TRIGGER", "SOURCE", "ENABLED", "NEXT FIRE", "LAST RUN", "BUDGET"];

    public static IReadOnlyList<ListRow> Triggers(TuiStore store, int width, DateTimeOffset now)
    {
        List<IReadOnlyList<string>> cells = [.. store.Triggers.Select(t => (IReadOnlyList<string>)
        [
            t.Id, t.SourceType, t.Enabled ? "yes" : "no",
            t.NextFireAt is { } next ? (next - now is { TotalSeconds: > 0 } left ? "in " + Format.Duration(left) : "due") : "-",
            t.LastRunId is null ? "-" : $"{t.LastState ?? "?"} ({Short(t.LastRunId)})",
            t.DailyTokens is long budget ? $"{Format.Tokens(t.TokensToday ?? 0)}/{Format.Tokens(budget)}" : "-",
        ])];
        int[] widths = Table.Widths(TriggerHeaders, cells, width, 4);
        List<ListRow> rows = [new(Table.Row(TriggerHeaders, widths, Style.Header), Selectable: false)];
        for (int i = 0; i < cells.Count; i++)
        {
            TriggerDto t = store.Triggers[i];
            rows.Add(new ListRow(Table.Row(cells[i], widths, Style.Normal,
                cellStyles: [Style.Accent, Style.Dim, t.Enabled ? Style.Ok : Style.Dim, Style.Normal, t.LastState is null ? Style.Dim : StateStyle(t.LastState), Style.Dim]), t.Id, "trigger"));
        }
        if (cells.Count == 0) rows.Add(new ListRow(Line.Of("no triggers (add YAML files under ~/.harness/triggers)", Style.Dim), Selectable: false));
        rows.Add(new ListRow(Line.Empty, Selectable: false));
        rows.Add(new ListRow(Line.Of("F fire (with text or inputs as JSON) · Space enable/disable · Enter latest run", Style.Dim), Selectable: false));
        return rows;
    }
}

/// <summary>
/// The run detail view: a summary header, the event timeline (one line per event; the selected one expanded as a
/// tool-call inspector with its arguments and result), then the output and files once the run is final.
/// </summary>
public static class RunTimeline
{
    private static readonly System.Text.Json.JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyList<ListRow> Rows(RunDto? run, IReadOnlyList<EventDto> events, long selectedSeq, IReadOnlySet<string> pendingRequests, int width, DateTimeOffset now)
    {
        List<ListRow> rows = [];
        if (run is not null)
        {
            LineBuilder h = new(width);
            h.Row([new Span(run.Id + "  ", Style.Accent), new Span(run.State, ListViews.StateStyle(run.State)),
                new Span($"  {run.Agent} · {run.Model}" + (run.TriggerId is null ? "" : $" · trigger {run.TriggerId}"), Style.Dim)],
                new Span($"{Format.Duration(((run.FinishedAt ?? now) - (run.StartedAt ?? run.CreatedAt)) is { } d && d > TimeSpan.Zero ? d : TimeSpan.Zero)} · {Format.Tokens(run.InputTokens)} in / {Format.Tokens(run.OutputTokens)} out", Style.Dim));
            if (run.Error is { } error) h.Paragraph(error, Style.Error, new Span("error: ", Style.Error), indent: 7);
            h.Blank();
            foreach (Line l in h.Lines) rows.Add(new ListRow(l, Selectable: false));
        }

        Dictionary<string, EventDto> results = [];
        foreach (EventDto e in events.Where(e => e.Type == "TOOL_CALL_RESULT"))
            if (e.Data["toolCallId"]?.GetValue<string>() is { } id) results[id] = e;

        foreach (EventDto e in events)
        {
            if (e.Type is "TOOL_CALL_RESULT" or "TOOL_CALL_END" or "TEXT_MESSAGE_START" or "TEXT_MESSAGE_CONTENT") continue;
            string? request = e.Type == "APPROVAL_REQUESTED" ? e.Data["requestId"]?.GetValue<string>() : null;
            bool pending = request is not null && pendingRequests.Contains(request);
            (string summary, Style style) = Describe(e, results, pending);
            LineBuilder b = new(width);
            b.Row([new Span($"{e.Ts.ToLocalTime():HH:mm:ss} ", Style.Dim), new Span(Label(e.Type).PadRight(11), style), new Span(summary, pending ? Style.Approval : Style.Normal)],
                pending ? new Span("[a]pprove [d]eny [A]lways", Style.Key) : null);
            if (e.Seq == selectedSeq) Inspect(b, e, results);
            IReadOnlyList<Line> lines = b.Lines;
            string id = e.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < lines.Count; i++) rows.Add(new ListRow(lines[i], id, pending ? "approval" : e.Type, Selectable: i == 0));
        }
        if (events.Count == 0) rows.Add(new ListRow(Line.Of("  loading events…", Style.Dim), Selectable: false));

        if (run is not null && !TuiStore.ActiveStates.Contains(run.State))
        {
            LineBuilder o = new(width);
            o.Blank();
            if (run.Output is { } output)
            {
                o.Row([new Span("Output", Style.Header)]);
                foreach (string l in output.ToJsonString(Pretty).Split('\n').Take(60)) o.Row([new Span("  " + l, Style.Code)]);
            }
            else if (run.ResultText is { Length: > 0 } text)
            {
                o.Row([new Span("Result", Style.Header)]);
                o.Paragraph(text, Style.Normal, new Span("  ", Style.Normal), indent: 2);
            }
            if (run.Files is { Count: > 0 } files)
            {
                o.Row([new Span("Files", Style.Header)]);
                foreach (string f in files) o.Row([new Span("  " + f, Style.Accent)]);
            }
            foreach (Line l in o.Lines) rows.Add(new ListRow(l, Selectable: false));
        }
        return rows;
    }

    private static string Label(string type) => type switch
    {
        "RUN_STARTED" => "started", "RUN_STATE" => "state", "RUN_FINISHED" => "finished", "RUN_ERROR" => "error",
        "TEXT_MESSAGE_END" => "message", "TOOL_CALL_START" => "tool", "APPROVAL_REQUESTED" => "approval?", "APPROVAL_RESOLVED" => "approval",
        "USAGE" => "usage", "WORKSPACE_STEP" => "workspace", "OUTPUT_VALIDATED" => "output", "OUTPUT_DELIVERED" => "delivery",
        "CHECKPOINT" => "checkpoint", "EGRESS_DENIED" => "egress", _ => type.ToLowerInvariant(),
    };

    private static (string, Style) Describe(EventDto e, Dictionary<string, EventDto> results, bool pending)
    {
        var d = e.Data;
        string S(string k) => d[k] is System.Text.Json.Nodes.JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : d[k]?.ToJsonString() ?? "";
        switch (e.Type)
        {
            case "RUN_STARTED": return (Format.OneLine(S("input"), 200), Style.Accent);
            case "RUN_STATE": return (S("state") + (d["notice"] is null ? "" : " · " + S("notice")), Style.Dim);
            case "RUN_FINISHED": return (S("state") + (d["error"] is null ? "" : " · " + S("error")), ListViews.StateStyle(S("state")));
            case "RUN_ERROR": return (S("message"), Style.Error);
            case "TEXT_MESSAGE_END": return (Format.OneLine(S("text"), 300), Style.Normal);
            case "TOOL_CALL_START":
                string id = S("toolCallId");
                string status = results.TryGetValue(id, out EventDto? r)
                    ? (TranscriptModel.IsError(r.Data["content"]?.GetValue<string>()) ? " ✗" : " ✓") : " …";
                return ($"{S("toolName")} {Format.OneLine(TranscriptItem.MainArgumentOf(d["arguments"] as System.Text.Json.Nodes.JsonObject), 160)}{status}", Style.Tool);
            case "APPROVAL_REQUESTED": return ($"{S("toolName")} {Format.OneLine(S("summary"), 160)}" + (pending ? "  (waiting)" : ""), pending ? Style.Approval : Style.Dim);
            case "APPROVAL_RESOLVED":
                bool ok = d["approved"]?.GetValue<bool>() == true;
                return ($"{(ok ? "approved" : "denied")} {S("toolName")} · {S("decidedBy")} via {S("via")}" + (d["reason"] is null ? "" : " · " + S("reason")), ok ? Style.Ok : Style.Error);
            case "USAGE": return ($"{d["inputTokens"]} in / {d["outputTokens"]} out (run {Format.Tokens(Json.Long(d["runInputTokens"]) ?? 0)} / {Format.Tokens(Json.Long(d["runOutputTokens"]) ?? 0)})", Style.Dim);
            default: return (Format.OneLine(d.ToJsonString(), 200), Style.Dim);
        }
    }

    /// <summary>The expanded event: a tool call with its arguments and result, or any other event's data.</summary>
    private static void Inspect(LineBuilder b, EventDto e, Dictionary<string, EventDto> results)
    {
        if (e.Type is "TOOL_CALL_START" or "APPROVAL_REQUESTED")
        {
            string? callId = e.Data["toolCallId"]?.GetValue<string>();
            string? result = callId is not null && results.TryGetValue(callId, out EventDto? r) ? r.Data["content"]?.GetValue<string>() : null;
            TranscriptItem item = new()
            {
                Kind = ItemKind.Tool, ToolName = e.Data["toolName"]?.GetValue<string>(), Arguments = e.Data["arguments"] as System.Text.Json.Nodes.JsonObject,
                Result = result, Ok = result is null ? null : !TranscriptModel.IsError(result), Expanded = true,
            };
            foreach (Line l in new TranscriptRenderer().RenderItem(item, b.Width).Skip(1)) b.Add(l);
            if (e.Type == "TOOL_CALL_START" && result is null) b.Row([new Span("    (no result yet)", Style.Dim)]);
            return;
        }
        string json = e.Data.ToJsonString(Pretty);
        foreach (string l in json.Split('\n').Take(40)) b.Row([new Span("    " + l, Style.Dim)]);
    }
}
