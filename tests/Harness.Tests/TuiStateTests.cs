using System.Text.Json.Nodes;
using Harness.Client;
using Harness.Tui.State;

namespace Harness.Tests;

/// <summary>The TUI's logic without a terminal: keymap, store reducer, transcript model, composer and list views.</summary>
public class TuiStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static EventDto E(long seq, string type, JsonObject data, string run = "r_1", string session = "s_1") =>
        new(seq, T0.AddSeconds(seq), run, session, type, data);

    private static SessionDto Session(string id, string? title, string workspace = "/src/api", string? name = "api", string? parent = null,
        string? trigger = null, int minutesAgo = 0) =>
        new(id, title, "coder", "local", workspace, name, null, parent, "idle", trigger, T0, T0.AddMinutes(-minutesAgo), 0, 0, 0);

    /// <summary>A recorded stream of one run: a tool call that needed approval, a person approved it, then the answer.</summary>
    private static List<EventDto> RecordedRun() =>
    [
        E(1, "RUN_STARTED", new() { ["agent"] = "coder", ["model"] = "local", ["interactive"] = true, ["input"] = "run the tests" }),
        E(2, "RUN_STATE", new() { ["state"] = "running" }),
        E(3, "USAGE", new() { ["inputTokens"] = 1500, ["outputTokens"] = 20, ["runInputTokens"] = 1500, ["runOutputTokens"] = 20 }),
        E(4, "APPROVAL_REQUESTED", new() { ["requestId"] = "q1", ["toolCallId"] = "c1", ["toolName"] = "shell", ["arguments"] = new JsonObject { ["command"] = "dotnet test" }, ["summary"] = "dotnet test" }),
        E(5, "APPROVAL_RESOLVED", new() { ["requestId"] = "q1", ["toolName"] = "shell", ["approved"] = true, ["decidedBy"] = "sam", ["via"] = "cli" }),
        E(6, "TOOL_CALL_START", new() { ["toolCallId"] = "c1", ["toolName"] = "shell", ["arguments"] = new JsonObject { ["command"] = "dotnet test" } }),
        E(7, "TOOL_CALL_END", new() { ["toolCallId"] = "c1", ["toolName"] = "shell", ["ok"] = true }),
        E(8, "TOOL_CALL_RESULT", new() { ["toolCallId"] = "c1", ["toolName"] = "shell", ["content"] = "exit 0 · 3.1s · cwd .\nPassed: 12" }),
        E(9, "TEXT_MESSAGE_START", new() { ["messageId"] = "m1" }),
        E(0, "TEXT_MESSAGE_CONTENT", new() { ["messageId"] = "m1", ["delta"] = "All 12 " }),
        E(0, "TEXT_MESSAGE_CONTENT", new() { ["messageId"] = "m1", ["delta"] = "tests pass." }),
        E(10, "TEXT_MESSAGE_END", new() { ["messageId"] = "m1", ["text"] = "All 12 tests pass." }),
        E(11, "USAGE", new() { ["inputTokens"] = 1800, ["outputTokens"] = 30, ["runInputTokens"] = 3300, ["runOutputTokens"] = 50 }),
        E(12, "RUN_FINISHED", new() { ["state"] = "succeeded", ["inputTokens"] = 3300, ["outputTokens"] = 50, ["elapsedMs"] = 4200 }),
    ];

    // ---- keymap ----

    [Fact]
    public void Keymap_resolves_sequences_and_scopes()
    {
        Keymap k = Keymap.Default();
        Assert.True(k.Feed("g", KeyScope.Transcript, KeyScope.Global).Pending);
        Assert.Equal(Commands.GoRuns, k.Feed("r", KeyScope.Transcript, KeyScope.Global).Command);
        Assert.True(k.Feed("g", KeyScope.List, KeyScope.Global).Pending);
        Assert.Equal(Commands.Top, k.Feed("g", KeyScope.List, KeyScope.Global).Command);   // g g
        Assert.Equal(Commands.Bottom, k.Feed("G", KeyScope.List, KeyScope.Global).Command);

        // An unknown second key is swallowed, not typed.
        k.Feed("g", KeyScope.List, KeyScope.Global);
        KeyResult dropped = k.Feed("x", KeyScope.List, KeyScope.Global);
        Assert.True(dropped.Handled);
        Assert.Null(dropped.Command);
        Assert.False(k.IsPending);

        // The focused scope wins: 'd' denies on an approval card, deletes in the session list.
        Assert.Equal(Commands.Deny, k.Feed("d", KeyScope.Approval, KeyScope.Transcript, KeyScope.Global).Command);
        Assert.Equal(Commands.Delete, k.Feed("d", KeyScope.SessionList, KeyScope.List, KeyScope.Global).Command);
        Assert.Equal(Commands.Expand, k.Feed("Space", KeyScope.Transcript, KeyScope.Global).Command);
    }

    [Fact]
    public void Keymap_leaves_typing_alone_in_the_composer()
    {
        Keymap k = Keymap.Default();
        foreach (string key in new[] { "g", "s", "?", ":", "j", "Space" })
            Assert.False(k.Feed(key, KeyScope.Composer, KeyScope.Global).Handled);
        Assert.Equal(Commands.Palette, k.Feed("Ctrl+K", KeyScope.Composer, KeyScope.Global).Command);
        Assert.Equal(Commands.Send, k.Feed("Enter", KeyScope.Composer, KeyScope.Global).Command);
        Assert.Equal(Commands.Newline, k.Feed("Alt+Enter", KeyScope.Composer, KeyScope.Global).Command);
        Assert.Equal(Commands.Back, k.Feed("Esc", KeyScope.Composer, KeyScope.Global).Command);
    }

    [Fact]
    public void Keymap_overrides_replace_bindings_and_reject_unknown_commands()
    {
        Keymap k = Keymap.Default();
        k.Override(new Dictionary<string, IReadOnlyList<string>> { [Commands.Palette] = ["Ctrl+P"], [Commands.GoRuns] = ["Ctrl+R"] });
        Assert.Equal(Commands.Palette, k.Feed("Ctrl+P", KeyScope.Transcript, KeyScope.Global).Command);
        Assert.Null(k.Feed("Ctrl+K", KeyScope.Transcript, KeyScope.Global).Command);
        Assert.Null(k.Feed(":", KeyScope.List, KeyScope.Global).Command);   // the ':' binding went with the old ones
        Assert.Equal(Commands.GoRuns, k.Feed("Ctrl+R", KeyScope.Composer, KeyScope.Global).Command);
        Assert.Equal(["Ctrl+P"], k.KeysFor(Commands.Palette));
        Assert.Throws<ArgumentException>(() => k.Override(new Dictionary<string, IReadOnlyList<string>> { ["no.such"] = ["x"] }));
    }

    [Fact]
    public void Tui_yaml_sets_theme_and_keys()
    {
        string dir = Path.Combine(Path.GetTempPath(), "harness-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "tui.yaml");
        File.WriteAllText(file, "theme: light\nkeys:\n  palette: [\"Ctrl+P\", \":\"]\n  go.runs: Ctrl+R\n");
        (string? theme, Keymap keymap) = Harness.Cli.TuiLauncher.LoadConfig(file);
        Assert.Equal("light", theme);
        Assert.Equal(Commands.GoRuns, keymap.Feed("Ctrl+R", KeyScope.List, KeyScope.Global).Command);
        Assert.Contains("Ctrl+P", keymap.KeysFor(Commands.Palette));

        File.WriteAllText(file, "keys:\n  pallete: Ctrl+P\n");
        Assert.Throws<Harness.Core.Config.ConfigException>(() => Harness.Cli.TuiLauncher.LoadConfig(file));
        File.WriteAllText(file, "colours: dark\n");
        Assert.Throws<Harness.Core.Config.ConfigException>(() => Harness.Cli.TuiLauncher.LoadConfig(file));
    }

    // ---- store ----

    [Fact]
    public void Store_tracks_runs_and_approvals_from_a_recorded_stream()
    {
        TuiStore store = new();
        store.LoadSessions([Session("s_1", "tests")]);
        List<EventDto> events = RecordedRun();

        foreach (EventDto e in events.Take(4)) store.Apply(e);
        RunDto run = store.Run("r_1")!;
        Assert.Equal("awaiting_approval", run.State);
        Assert.Equal(1520, run.InputTokens + run.OutputTokens);
        Assert.Single(store.Approvals);
        Assert.Equal(1, store.ApprovalsWaiting("s_1"));
        Assert.Equal(("!", Style.Approval), SessionTree.Badge(store, store.Session("s_1")!));

        foreach (EventDto e in events.Skip(4).Take(2)) store.Apply(e);
        Assert.Empty(store.Approvals);
        Assert.Equal("running", store.Run("r_1")!.State);
        Assert.Equal("shell: dotnet test", store.Run("r_1")!.LastTool);
        Assert.Equal(("●", Style.Running), SessionTree.Badge(store, store.Session("s_1")!));

        foreach (EventDto e in events.Skip(6)) store.Apply(e);
        Assert.Equal("succeeded", store.Run("r_1")!.State);
        Assert.Null(store.ActiveRunOf("s_1"));
        Assert.Equal(3350, store.Session("s_1")!.InputTokens + store.Session("s_1")!.OutputTokens);
        Assert.Equal(12, store.LastHubSeq);
    }

    [Fact]
    public void Store_asks_for_a_refresh_when_an_unknown_session_starts_a_run()
    {
        TuiStore store = new();
        store.Apply(E(1, "RUN_STARTED", new() { ["agent"] = "helper", ["triggerId"] = "whatsapp" }, run: "r_9", session: "s_new"));
        Assert.True(store.NeedsRefresh);
        Assert.Equal("whatsapp", store.Run("r_9")!.TriggerId);
    }

    // ---- transcript ----

    [Fact]
    public void Transcript_from_events_folds_tools_and_records_approvals()
    {
        TranscriptModel t = new("s_1");
        foreach (EventDto e in RecordedRun()) t.Apply(e);
        Assert.Equal([ItemKind.User, ItemKind.Approval, ItemKind.Tool, ItemKind.Assistant, ItemKind.RunEnd], t.Items.Select(i => i.Kind));
        TranscriptItem approval = t.Items[1];
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        Assert.Equal("sam", approval.DecidedBy);
        Assert.True(t.Items[2].Ok);
        Assert.Equal("All 12 tests pass.", t.Items[3].Text.ToString());
        Assert.False(t.Items[3].Streaming);
        Assert.Equal(1800, t.LastInputTokens);

        string text = string.Join('\n', new TranscriptRenderer().Render(t, 80).Select(l => l.Text));
        Assert.Contains("you   run the tests", text);
        Assert.Contains("▸ shell dotnet test · exit 0 · 3.1s · cwd .", text);
        Assert.Contains("✓ approved shell dotnet test · sam", text);
        Assert.Contains("── succeeded · 3k in / 50 out · 4.2s", text);
    }

    [Fact]
    public void Transcript_replays_a_resumed_stream_once_and_drops_policy_approvals()
    {
        TranscriptModel t = new("s_1");
        List<EventDto> events = RecordedRun();
        foreach (EventDto e in events.Take(8)) t.Apply(e);
        // Reconnecting with Last-Event-ID can overlap; persisted events apply once.
        foreach (EventDto e in events.Skip(5)) t.Apply(e);
        Assert.Single(t.Items, i => i.Kind == ItemKind.Tool);
        Assert.Single(t.Items, i => i.Kind == ItemKind.Assistant);

        TranscriptModel auto = new("s_1");
        auto.Apply(E(1, "APPROVAL_REQUESTED", new() { ["requestId"] = "q", ["toolName"] = "shell", ["summary"] = "git status" }));
        Assert.Single(auto.PendingApprovals);
        auto.Apply(E(2, "APPROVAL_RESOLVED", new() { ["requestId"] = "q", ["approved"] = true, ["via"] = "policy", ["reason"] = "allowlist" }));
        Assert.Empty(auto.Items);
    }

    [Fact]
    public void Transcript_history_skips_runs_that_are_replayed_and_pages_older_messages()
    {
        MessageDto user = new(1, T0, "r_old", "user", "hello", [new ContentDto("text", "hello")]);
        MessageDto call = new(2, T0, "r_old", "assistant", "", [new ContentDto("tool_call", ToolName: "read", CallId: "c1", Arguments: new JsonObject { ["path"] = "a.txt" })]);
        MessageDto result = new(3, T0, "r_old", "tool", "", [new ContentDto("tool_result", CallId: "c1", Result: "Error: no such file")]);
        MessageDto active = new(4, T0, "r_live", "user", "now this", [new ContentDto("text", "now this")]);
        TranscriptModel t = new("s_1");
        t.LoadHistory(new MessagesPage([call, result, active], HasMore: true), new HashSet<string> { "r_live" });
        Assert.Equal([ItemKind.Tool], t.Items.Select(i => i.Kind));
        Assert.False(t.Items[0].Ok);
        Assert.Equal(2, t.OldestSeq);
        Assert.True(t.HasMoreBefore);

        Assert.Equal(1, t.PrependHistory(new MessagesPage([user], HasMore: false)));
        Assert.Equal(ItemKind.User, t.Items[0].Kind);
        Assert.False(t.HasMoreBefore);
        Assert.Equal(0, t.PreviousUserTurn(1));
        Assert.Equal(1, t.Find("no such", 0, forward: true));
    }

    [Fact]
    public void Expanded_edit_shows_a_diff_and_pending_approvals_show_what_will_run()
    {
        TranscriptModel t = new("s_1");
        t.Apply(E(1, "TOOL_CALL_START", new() { ["toolCallId"] = "c", ["toolName"] = "edit", ["arguments"] = new JsonObject
        {
            ["path"] = "Program.cs", ["old"] = "a\nb\nc", ["new"] = "a\nB\nc",
        } }));
        t.Apply(E(2, "APPROVAL_REQUESTED", new() { ["requestId"] = "q", ["toolName"] = "shell", ["arguments"] = new JsonObject { ["command"] = "git push" }, ["summary"] = "git push" }));
        TranscriptRenderer r = new();
        Assert.DoesNotContain("- b", string.Join('\n', r.Render(t, 60).Select(l => l.Text)));
        t.Items[0].Expanded = true;
        List<Line> lines = [.. r.Render(t, 60)];
        Assert.Contains(lines, l => l.Text.Trim() == "- b" && l.Spans.Any(s => s.Style == Style.DiffDel));
        Assert.Contains(lines, l => l.Text.Trim() == "+ B" && l.Spans.Any(s => s.Style == Style.DiffAdd));
        Assert.Contains(lines, l => l.Text.Contains("[a]pprove [d]eny [A]lways") && l.Item == 1);
        Assert.Contains(lines, l => l.Text.Trim() == "$ git push");
        Assert.All(lines, l => Assert.True(l.Text.Length <= 60));
    }

    // ---- composer ----

    [Fact]
    public void Composer_recalls_messages_and_expands_paste_chips_and_mentions()
    {
        ComposerModel c = new();
        Assert.Null(c.Recall(""));
        c.Remember("first");
        c.Remember("second");
        Assert.Equal("second", c.Recall(""));
        Assert.Equal("first", c.Recall("second"));
        Assert.Null(c.Recall("typed something"));
        Assert.Equal("second", c.RecallNext("first"));
        Assert.Equal("", c.RecallNext("second"));

        string paste = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line {i}"));
        Assert.True(ComposerModel.IsLargePaste(paste));
        Assert.False(ComposerModel.IsLargePaste("short"));
        string chip = c.AddPaste(paste);
        Assert.Equal("[paste #1: 20 lines]", chip);
        string sent = c.Compose($"look at @src/Program.cs, then this: {chip}");
        Assert.Contains("```\nline 0\n", sent);
        Assert.DoesNotContain("[paste #1", sent);
        Assert.EndsWith("Referenced files (read them with the read tool): src/Program.cs", sent);
        Assert.Empty(c.Attachments);

        Assert.Equal((5, "src/Pro"), ComposerModel.MentionAt("look @src/Pro", 13));
        Assert.Null(ComposerModel.MentionAt("mail me@example.com", 19));
        Assert.Equal(new SlashCommand("model", "azure"), ComposerModel.ParseSlash(" /model azure "));
        Assert.Null(ComposerModel.ParseSlash("/usr/bin is a path\nwith two lines"));
        Assert.Null(ComposerModel.ParseSlash("/ not a command"));
        Assert.Equal(["/fork"], ComposerModel.SlashMatches("/fo").Select(m => m.Name));
    }

    // ---- lists ----

    [Fact]
    public void Session_tree_groups_by_workspace_nests_forks_and_filters()
    {
        TuiStore store = new();
        store.LoadSessions(
        [
            Session("s_a", "Rate limiting", minutesAgo: 2),
            Session("s_b", "Fix flaky test", minutesAgo: 60),
            Session("s_c", "fork of rate limiting", parent: "s_a", minutesAgo: 30),
            Session("s_d", "Upgrade to Vite", workspace: "/src/web", name: "web", minutesAgo: 3 * 24 * 60),
            Session("s_e", "whatsapp: +3212", workspace: "/srv/assistant", name: null, trigger: "whatsapp", minutesAgo: 0),
        ]);
        store.UpsertRun(new RunDto("r_e", "s_e", "helper", "local", "whatsapp", "failed", T0, T0, T0, 0, 0, null, "boom", null));
        List<string> rows = [.. SessionTree.Rows(store, "", new HashSet<string>(), 40, T0).Select(r => r.Line.Text.TrimEnd())];
        Assert.Equal("▾ api  /src/api", rows[0]);
        Assert.StartsWith("  Rate limiting", rows[1]);
        Assert.StartsWith("  ↳ fork of rate limiting", rows[2]);
        Assert.StartsWith("  Fix flaky test", rows[3]);
        Assert.Equal("▾ web  /src/web", rows[4]);
        Assert.EndsWith("3d", rows[5]);
        Assert.Equal("▾ Triggered", rows[6]);
        Assert.StartsWith("✗ whatsapp", rows[7]);

        Assert.Equal(["group", "session"], SessionTree.Rows(store, "upgrade", new HashSet<string>(), 40, T0).Select(r => r.Kind));
        Assert.Equal(2, SessionTree.Rows(store, "", new HashSet<string> { "api" }, 40, T0).Count(r => r.Kind == "session"));
    }

    [Fact]
    public void Run_timeline_marks_pending_approvals_and_inspects_tool_calls()
    {
        List<EventDto> events = [.. RecordedRun().Take(4).Where(e => e.Type != "TEXT_MESSAGE_CONTENT")];
        RunDto run = new("r_1", "s_1", "coder", "local", null, "awaiting_approval", T0, T0, null, 1500, 20, null, null, null);
        IReadOnlyList<ListRow> rows = RunTimeline.Rows(run, events, selectedSeq: 4, new HashSet<string> { "q1" }, 90, T0.AddMinutes(1));
        ListRow pending = Assert.Single(rows, r => r.Kind == "approval" && r.Selectable);
        Assert.Contains("[a]pprove", pending.Line.Text);
        Assert.Contains(rows, r => !r.Selectable && r.Id == "4" && r.Line.Text.Trim() == "$ dotnet test");

        List<EventDto> all = [.. RecordedRun().Where(e => e.Type != "TEXT_MESSAGE_CONTENT")];
        RunDto done = run with { State = "succeeded", FinishedAt = T0.AddSeconds(5), ResultText = "All 12 tests pass." };
        string text = string.Join('\n', RunTimeline.Rows(done, all, 6, new HashSet<string>(), 90, T0).Select(r => r.Line.Text));
        Assert.Contains("tool       shell dotnet test ✓", text);
        Assert.Contains("Passed: 12", text);   // the inspector pairs the call with its result
        Assert.Contains("Result", text);
    }
}
