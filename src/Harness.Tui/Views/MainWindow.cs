using Harness.Client;
using Harness.Tui.State;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Line = Harness.Tui.State.Line;

namespace Harness.Tui.Views;

/// <summary>
/// The full-screen layout: a sidebar (sessions above, live runs below), the main pane (a session's transcript and
/// composer, or the runs, run detail, approvals and triggers views) and a one-line status bar. Every key goes through
/// one dispatcher and the <see cref="Keymap"/>; everything else is rendered from the controller's store.
/// </summary>
internal sealed class MainWindow : Runnable
{
    private enum Region { Sessions, Runs, Body, Composer }

    private const int SidebarWidth = 34, RunsHeight = 8, NarrowWidth = 90;

    private readonly TuiController _c;
    private readonly Theme _theme;
    private readonly Keymap _keys;
    private readonly TranscriptRenderer _renderer = new();
    private readonly View _sidebar, _sessionsFrame, _runsFrame, _main, _status, _hint;
    private readonly LinesView _sessions, _runs, _body;
    private readonly ComposerView _composer;
    private readonly Overlay _overlay;
    private readonly HashSet<string> _collapsed = [];
    private readonly List<string?> _sessionIds = [], _runIds = [], _bodyIds = [];
    private string _sessionFilter = "", _listFilter = "", _search = "";
    private bool _filteringSessions;
    private bool? _sidebarShown;
    private long _detailSelectedSeq = -1;
    private string? _approvalSelected;
    private IReadOnlyList<string> _completions = [];
    private int _completionIndex;
    private (int Start, string Query)? _mention;
    private CancellationTokenSource? _completionCts;
    private int _lastTranscriptCount;
    private int _composerRows = 1;

    public MainWindow(TuiController controller, Theme theme)
    {
        _c = controller;
        _theme = theme;
        _keys = controller.Options.Keymap;
        CanFocus = true;

        _sidebar = new View { X = 0, Y = 0, Width = SidebarWidth, Height = Dim.Fill(1) };
        _sessionsFrame = new View { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(RunsHeight), BorderStyle = LineStyle.Single, Title = "Sessions" };
        _sessions = new LinesView(theme) { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), RowHighlight = true, Gutter = false };
        _sessions.Source = SessionLines;
        _sessionsFrame.Add(new FilterLine(this, theme) { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 }, _sessions);
        _runsFrame = new View { X = 0, Y = Pos.AnchorEnd(RunsHeight), Width = Dim.Fill(), Height = RunsHeight, BorderStyle = LineStyle.Single, Title = "Runs" };
        _runs = new LinesView(theme) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), RowHighlight = true, Gutter = false };
        _runs.Source = RunLines;
        _runsFrame.Add(_runs);
        _sidebar.Add(_sessionsFrame, _runsFrame);
        // A view only takes focus when every container above it can.
        _sidebar.CanFocus = _sessionsFrame.CanFocus = _runsFrame.CanFocus = true;

        _main = new View { X = Pos.Right(_sidebar), Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1), BorderStyle = LineStyle.Single };
        _body = new LinesView(theme) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3), FollowCapable = true, Follow = true };
        _body.Source = BodyLines;
        _body.ReachedTop += () => { if (_c.Screen == Screen.Session) _ = _c.LoadOlderAsync(); };
        _body.SelectionChanged += _ => OnBodySelection();
        _hint = new HintLine(this, theme) { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
        _composer = new ComposerView { X = 0, Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 2, BorderStyle = LineStyle.Single };
        _composer.Border!.Thickness = new Thickness(0, 1, 0, 0);
        _composer.PasteFilter = text => ComposerModel.IsLargePaste(text) ? _c.Composer.AddPaste(text) : null;
        _composer.ContentsChanged += (_, _) => OnComposerChanged();
        _composer.UnwrappedCursorPositionChanged += (_, _) => UpdateMention();
        _main.Add(_body, _hint, _composer);
        _main.CanFocus = true;
        // Titles hold ids like s_0abc; '_' would otherwise mark a hotkey and disappear.
        foreach (View v in new[] { _main, _sessionsFrame, _runsFrame }) v.HotKeySpecifier = new System.Text.Rune(0xFFFF);

        _status = new StatusLine(this, theme) { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        _overlay = new Overlay(theme);
        Add(_sidebar, _main, _status, _overlay);

        _c.Changed += Refresh;
        Initialized += (_, _) => ApplyScreen();
    }

    /// <summary>Called once the window runs: the composer for a session, the list otherwise.</summary>
    public void FocusInitial()
    {
        ApplyScreen();
        FocusRegion(_c.Screen == Screen.Session ? Region.Composer : Region.Body);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _c.Changed -= Refresh;
        base.Dispose(disposing);
    }

    // ---- layout ----

    private bool SidebarVisible => _sidebarShown ?? (App?.Screen.Width ?? 120) >= NarrowWidth;

    public void Relayout()
    {
        bool sidebar = SidebarVisible;
        _sidebar.Visible = sidebar;
        // Short terminals keep the session list and drop the runs box (the runs view is a key away: g r).
        bool runsBox = (App?.Screen.Height ?? 40) >= 20;
        _runsFrame.Visible = runsBox;
        _sessionsFrame.Height = runsBox ? Dim.Fill(RunsHeight) : Dim.Fill();
        _main.X = sidebar ? Pos.Right(_sidebar) : 0;
        bool composer = _c.Screen == Screen.Session;
        int rows = composer ? Math.Clamp(_composerRows, 1, 8) : 0;
        _composer.Visible = composer;
        _hint.Visible = composer;
        _composer.Height = rows + 1;
        _composer.Y = Pos.AnchorEnd(rows + 1);
        _hint.Y = Pos.AnchorEnd(rows + 2);
        _body.Height = composer ? Dim.Fill(rows + 2) : Dim.Fill();
        SetNeedsLayout();
    }

    private void ApplyScreen()
    {
        _listFilter = "";
        _body.Follow = _c.Screen is Screen.Session or Screen.RunDetail;
        _body.Invalidate();
        Relayout();
        Refresh();
        if (_c.Screen == Screen.RunDetail) _body.MoveBottom();
    }

    public void Refresh()
    {
        SessionDto? open = _c.OpenSession;
        _renderer.AgentLabel = open?.Agent ?? "agent";
        _main.Title = Title2();
        _sessions.Invalidate();
        _runs.Invalidate();
        if (_c.Transcript is { } t)
        {
            // An older page inserted above keeps the cursor on the same message.
            TranscriptItem? first = t.Items.Count > 0 ? t.Items[0] : null;
            int added = t.Items.Count - _lastTranscriptCount;
            if (_prependPending && added > 0 && _firstItem is not null && first != _firstItem)
            {
                _prependPending = false;
                _body.KeepAnchor(t.Items.IndexOf(_firstItem));
            }
            _firstItem = first;
            _lastTranscriptCount = t.Items.Count;
        }
        _body.Invalidate();
        _status.SetNeedsDraw();
        _hint.SetNeedsDraw();
        if (_c.QuitRequested) App?.RequestStop();
        SetNeedsDraw();
    }

    private bool _prependPending;
    private TranscriptItem? _firstItem;

    private string Title2()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        switch (_c.Screen)
        {
            case Screen.Session:
                if (_c.OpenSession is not { } s)
                    return $"new session · {_c.PendingSession.Agent ?? "default agent"} · {_c.PendingSession.WorkspaceName ?? SessionTree.Tilde(_c.PendingSession.Workspace ?? "", null)}";
                string state = _c.ActiveRun is { } r
                    ? $"{(r.State == "awaiting_approval" ? "! approval" : "● " + r.State)} {Format.Clock(now - (r.StartedAt ?? r.CreatedAt))}"
                    : "idle";
                return $"{s.Title ?? s.Id} · {s.Agent} · {s.Model} · {state}";
            case Screen.Runs: return "Runs" + (_listFilter.Length > 0 ? $" · /{_listFilter}" : "");
            case Screen.Approvals: return $"Approvals · {_c.Store.ApprovalsWaiting()} waiting";
            case Screen.Triggers: return "Triggers";
            case Screen.RunDetail:
                RunDto? run = _c.DetailRunId is { } id ? _c.Store.Run(id) : null;
                return $"Run {_c.DetailRunId} · {run?.State ?? "?"}";
        }
        return "";
    }

    // ---- line sources ----

    private IReadOnlyList<Line> SessionLines(int width) =>
        ToLines(SessionTree.Rows(_c.Store, _sessionFilter, _collapsed, width, DateTimeOffset.UtcNow), _sessionIds,
            id => id is not null && id == _c.OpenSessionId);

    private IReadOnlyList<Line> RunLines(int width) =>
        ToLines(ListViews.RunsSidebar(_c.Store, width, DateTimeOffset.UtcNow, 30), _runIds);

    private IReadOnlyList<Line> BodyLines(int width)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        switch (_c.Screen)
        {
            case Screen.Session:
                _bodyIds.Clear();
                if (_c.Transcript is { } t) return _renderer.Render(t, width);
                LineBuilder b = new(width);
                b.Blank();
                b.Paragraph($"New session in {_c.PendingSession.WorkspaceName ?? _c.PendingSession.Workspace ?? "the default workspace"}.", Style.Header);
                b.Paragraph("Type a message below and press Enter to start. Alt+Enter adds a line, / opens slash commands, @ completes file paths.", Style.Dim);
                b.Paragraph("Ctrl+K opens the command palette, ? lists the keys, g s / g r / g a / g t go to sessions, runs, approvals and triggers.", Style.Dim);
                return b.Lines;
            case Screen.Runs:
                return ToLines(ListViews.RunsTable(_c.Store, _listFilter, width, now), _bodyIds);
            case Screen.Approvals:
                return ToLines(ListViews.Approvals(_c.Store, width, now, _approvalSelected), _bodyIds);
            case Screen.Triggers:
                return ToLines(ListViews.Triggers(_c.Store, width, now), _bodyIds);
            case Screen.RunDetail:
                HashSet<string> pending = [.. _c.Store.Approvals.Where(a => a.RunId == _c.DetailRunId).Select(a => a.RequestId)];
                return ToLines(RunTimeline.Rows(_c.DetailRunId is { } id ? _c.Store.Run(id) : null, _c.DetailEvents, _detailSelectedSeq, pending, width, now), _bodyIds);
        }
        return [];
    }

    /// <summary>List rows to lines: each selectable row starts an item; detail rows with the same id belong to it.</summary>
    private static IReadOnlyList<Line> ToLines(IReadOnlyList<ListRow> rows, List<string?> ids, Func<string?, bool>? current = null)
    {
        ids.Clear();
        List<Line> lines = [];
        int item = -1;
        string? lastId = null;
        foreach (ListRow row in rows)
        {
            if (row.Selectable && row.Id is not null)
            {
                item = ids.Count;
                ids.Add(row.Id);
                lastId = row.Id;
            }
            else if (row.Id is null || row.Id != lastId) item = -1;
            Line line = row.Line with { Item = item };
            if (current?.Invoke(row.Id) == true && row.Selectable)
                line = line with { Spans = [.. line.Spans.Select(s => s.Style == Style.Normal ? s with { Style = Style.Bold } : s)] };
            lines.Add(line);
        }
        return lines;
    }

    private string? SelectedId(LinesView view, List<string?> ids)
    {
        _ = view.Lines;
        return view.Selected >= 0 && view.Selected < ids.Count ? ids[view.Selected] : null;
    }

    private TranscriptItem? SelectedItem =>
        _c.Screen == Screen.Session && _c.Transcript is { } t && _body.Selected >= 0 && _body.Selected < t.Items.Count ? t.Items[_body.Selected] : null;

    private void OnBodySelection()
    {
        string? id = SelectedId(_body, _bodyIds);
        if (_c.Screen == Screen.Approvals && id != _approvalSelected)
        {
            _approvalSelected = id;
            _body.Invalidate();
        }
        if (_c.Screen == Screen.RunDetail && long.TryParse(id, out long seq) && seq != _detailSelectedSeq)
        {
            _detailSelectedSeq = seq;
            _body.Invalidate();
        }
    }

    // ---- focus ----

    private Region Focus2 =>
        _composer.HasFocus ? Region.Composer : _sessions.HasFocus ? Region.Sessions : _runs.HasFocus ? Region.Runs : Region.Body;

    private void FocusRegion(Region r)
    {
        switch (r)
        {
            case Region.Sessions when SidebarVisible:
                _sessions.SetFocus();
                EnsureSessionSelected();
                break;
            case Region.Runs when SidebarVisible && _runsFrame.Visible: _runs.SetFocus(); break;
            case Region.Composer when _c.Screen == Screen.Session: _composer.SetFocus(); break;
            default: _body.SetFocus(); break;
        }
        SetNeedsDraw();
    }

    private void CycleFocus(int direction)
    {
        List<Region> order = [];
        if (SidebarVisible) order.Add(Region.Sessions);
        if (SidebarVisible && _runsFrame.Visible) order.Add(Region.Runs);
        order.Add(Region.Body);
        if (_c.Screen == Screen.Session) order.Add(Region.Composer);
        int at = order.IndexOf(Focus2);
        FocusRegion(order[((at + direction) % order.Count + order.Count) % order.Count]);
    }

    private KeyScope[] Scopes()
    {
        switch (Focus2)
        {
            case Region.Composer: return [KeyScope.Composer, KeyScope.Global];
            case Region.Sessions: return [KeyScope.SessionList, KeyScope.List, KeyScope.Global];
            case Region.Runs: return [KeyScope.List, KeyScope.Global];
        }
        return _c.Screen switch
        {
            Screen.Session when SelectedItem is { Kind: ItemKind.Approval, Status: ApprovalStatus.Pending } => [KeyScope.Approval, KeyScope.Transcript, KeyScope.Global],
            Screen.Session => [KeyScope.Transcript, KeyScope.Global],
            Screen.Approvals => [KeyScope.Approval, KeyScope.List, KeyScope.Global],
            Screen.RunDetail when PendingDetailApproval() is not null => [KeyScope.Approval, KeyScope.List, KeyScope.Global],
            _ => [KeyScope.List, KeyScope.Global],
        };
    }

    // ---- key dispatch ----

    /// <summary>Called for every key before the focused widget. Returns true when the key was used.</summary>
    public bool Dispatch(Key key)
    {
        string name = KeyNames.Of(key);
        if (_overlay.Visible) return OverlayKey(name);
        if (_filteringSessions) return FilterKey(name);
        if (_completions.Count > 0 && Focus2 == Region.Composer && CompletionKey(name)) return true;

        KeyResult result = _keys.Feed(name, Scopes());
        _status.SetNeedsDraw();
        if (result.Command is { } command) return Execute(command);
        return result.Handled;
    }

    private bool OverlayKey(string name)
    {
        switch (name)
        {
            case "Esc": _overlay.Cancel(); break;
            case "Enter": _overlay.Accept(); break;
            case "Down" or "Ctrl+N": _overlay.Move(1); break;
            case "Up" or "Ctrl+P": _overlay.Move(-1); break;
            case "PageDown": _overlay.Page(1); break;
            case "PageUp": _overlay.Page(-1); break;
            case "Backspace": _overlay.Backspace(); break;
            case "Space" when _overlay.HasInput: _overlay.Type(" "); break;
            case "j" when !_overlay.HasInput: _overlay.Move(1); break;
            case "k" when !_overlay.HasInput: _overlay.Move(-1); break;
            case "?" or "q" when !_overlay.HasInput: _overlay.Close(); break;
            default:
                if (_overlay.HasInput && name.Length == 1) _overlay.Type(name);
                break;
        }
        if (!_overlay.Visible) RestoreFocus();
        SetNeedsDraw();
        return true;
    }

    private Region _focusBeforeOverlay = Region.Body;

    private void RestoreFocus() => FocusRegion(_focusBeforeOverlay);

    private void OpenOverlay(string title, string? prompt, Func<string, int, IReadOnlyList<OverlayItem>>? filter, Action<string>? accept,
        string initial = "", Action<string>? change = null, Action? cancel = null, bool compact = false)
    {
        if (!_overlay.Visible) _focusBeforeOverlay = Focus2;
        _keys.Reset();
        _overlay.Open(title, prompt, filter, accept, initial, change, cancel, compact);
        SetNeedsDraw();
    }

    private void Ask(string title, string prompt, Action<string> accept, string initial = "") =>
        OpenOverlay(title, prompt, null, accept, initial, compact: true);

    private bool FilterKey(string name)
    {
        switch (name)
        {
            case "Esc":
                _sessionFilter = "";
                _filteringSessions = false;
                break;
            case "Enter" or "Down" or "Tab":
                _filteringSessions = false;
                _sessions.Invalidate();
                EnsureSessionSelected(force: true);
                break;
            case "Backspace":
                if (_sessionFilter.Length > 0) _sessionFilter = _sessionFilter[..^1];
                break;
            case "Space": _sessionFilter += " "; break;
            default:
                if (name.Length == 1) _sessionFilter += name;
                break;
        }
        _sessions.Invalidate();
        SetNeedsDraw();
        return true;
    }

    private bool Execute(string command)
    {
        Region focus = Focus2;
        LinesView? list = focus switch { Region.Sessions => _sessions, Region.Runs => _runs, Region.Body => _body, _ => null };
        switch (command)
        {
            case Commands.Palette: OpenPalette(); return true;
            case Commands.Help: OpenHelp(); return true;
            case Commands.Quit: _c.Quit(); return true;
            case Commands.GoSessions:
                if (!SidebarVisible) _sidebarShown = true;
                Relayout();
                FocusRegion(Region.Sessions);
                if (_c.Screen != Screen.Session) Show(Screen.Session);
                return true;
            case Commands.GoRuns: Show(Screen.Runs); return true;
            case Commands.GoApprovals: Show(Screen.Approvals); return true;
            case Commands.GoTriggers:
                Show(Screen.Triggers);
                _ = _c.RefreshAsync();
                return true;
            case Commands.FocusNext: CycleFocus(1); return true;
            case Commands.FocusPrev: CycleFocus(-1); return true;
            case Commands.ToggleSidebar:
                _sidebarShown = !SidebarVisible;
                Relayout();
                if (!SidebarVisible && focus is Region.Sessions or Region.Runs) FocusRegion(Region.Body);
                return true;
            case Commands.Back: return Back(focus);
            case Commands.Interrupt: _ = _c.InterruptAsync(); return true;
        }

        if (list is not null)
        {
            switch (command)
            {
                case Commands.Down: list.Move(1); return true;
                case Commands.Up: list.Move(-1); return true;
                case Commands.Top: list.MoveTop(); return true;
                case Commands.Bottom: list.MoveBottom(); return true;
                case Commands.PageDown: list.Page(1); return true;
                case Commands.PageUp: list.Page(-1); return true;
            }
        }

        return focus switch
        {
            Region.Sessions => SessionCommand(command),
            Region.Runs => RunsCommand(command, SelectedId(_runs, _runIds)),
            Region.Composer => ComposerCommand(command),
            _ => _c.Screen switch
            {
                Screen.Session => TranscriptCommand(command),
                Screen.Runs => RunsCommand(command, SelectedId(_body, _bodyIds)),
                Screen.Approvals => ApprovalsCommand(command),
                Screen.Triggers => TriggersCommand(command),
                Screen.RunDetail => DetailCommand(command),
                _ => false,
            },
        };
    }

    private bool Back(Region focus)
    {
        if (_keys.IsPending) { _keys.Reset(); return true; }
        if (_listFilter.Length > 0 && _c.Screen == Screen.Runs) { _listFilter = ""; _body.Invalidate(); Refresh(); return true; }
        if (_sessionFilter.Length > 0 && focus == Region.Sessions) { _sessionFilter = ""; _sessions.Invalidate(); return true; }
        if (_completions.Count > 0) { ClearCompletions(); return true; }
        if (_c.Screen != Screen.Session) { Show(Screen.Session); return true; }
        if (focus == Region.Composer) { FocusRegion(Region.Body); return true; }
        if (focus != Region.Body) { FocusRegion(Region.Body); return true; }
        return true;
    }

    private void Show(Screen screen)
    {
        _c.Screen = screen;
        ApplyScreen();
        FocusRegion(screen == Screen.Session ? Region.Composer : Region.Body);
        if (screen != Screen.Session) _body.MoveTop();
    }

    // ---- session list ----

    private bool SessionCommand(string command)
    {
        string? id = SelectedId(_sessions, _sessionIds);
        bool isGroup = id is not null && _c.Store.Session(id) is null;
        switch (command)
        {
            case Commands.Search:
                _filteringSessions = true;
                SetNeedsDraw();
                return true;
            case Commands.Open or Commands.Resume when isGroup:
                if (!_collapsed.Remove(id!)) _collapsed.Add(id!);
                _sessions.Invalidate();
                return true;
            case Commands.Open or Commands.Resume when id is not null:
                _ = OpenSession(id);
                return true;
            case Commands.NewSession:
                _c.NewSession();
                Show(Screen.Session);
                return true;
            case Commands.Fork when id is not null && !isGroup:
                _ = _c.ForkAsync(id, null);
                return true;
            case Commands.Rename when id is not null && !isGroup:
                Ask("Rename session", "title: ", title => { if (title.Trim().Length > 0) _ = _c.RenameAsync(id, title); }, _c.Store.Session(id)?.Title ?? "");
                return true;
            case Commands.Export when id is not null && !isGroup:
                _ = _c.ExportAsync(id);
                return true;
            case Commands.Delete when id is not null && !isGroup:
                Ask($"Delete {_c.Store.Session(id)?.Title ?? id}?", "type yes to delete: ", answer =>
                {
                    if (answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)) _ = _c.DeleteAsync(id);
                    else _c.Say("not deleted");
                });
                return true;
        }
        return false;
    }

    /// <summary>Puts the list cursor on a session (the open one, else the first) when it is on nothing or on a group.</summary>
    private void EnsureSessionSelected(bool force = false)
    {
        string? current = SelectedId(_sessions, _sessionIds);
        if (!force && current is not null && _c.Store.Session(current) is not null) return;
        int open = _c.OpenSessionId is { } o ? _sessionIds.IndexOf(o) : -1;
        int first = open >= 0 ? open : _sessionIds.FindIndex(id => id is not null && _c.Store.Session(id) is not null);
        if (first >= 0) _sessions.Select(first);
    }

    private async Task OpenSession(string id)
    {
        _lastTranscriptCount = 0;
        await _c.OpenSessionAsync(id);
        App?.Invoke(() =>
        {
            _body.Follow = true;
            ApplyScreen();
            FocusRegion(Region.Composer);
        });
    }

    // ---- transcript ----

    private bool TranscriptCommand(string command)
    {
        TranscriptModel? t = _c.Transcript;
        TranscriptItem? item = SelectedItem;
        switch (command)
        {
            case Commands.FocusComposer: FocusRegion(Region.Composer); return true;
            case Commands.PrevTurn when t is not null:
                int prev = t.PreviousUserTurn(_body.Selected);
                if (prev == _body.Selected) _ = LoadOlder();
                _body.Select(prev);
                return true;
            case Commands.NextTurn when t is not null: _body.Select(t.NextUserTurn(_body.Selected)); return true;
            case Commands.Top:
                _ = LoadOlder();
                _body.MoveTop();
                return true;
            case Commands.Expand when item is { Kind: ItemKind.Tool }:
                item.Expanded = !item.Expanded;
                item.Touch();
                _body.Invalidate();
                _body.EnsureVisible();
                return true;
            case Commands.ExpandAll when t is not null:
                bool expand = !t.Items.Where(i => i.Kind == ItemKind.Tool).All(i => i.Expanded);
                foreach (TranscriptItem i in t.Items.Where(i => i.Kind == ItemKind.Tool)) { i.Expanded = expand; i.Touch(); }
                _body.Invalidate();
                return true;
            case Commands.Copy when item is not null:
                Console.Out.Write(Osc52.Sequence(TranscriptModel.SearchText(item)));
                Console.Out.Flush();
                _c.Say("copied the message (OSC 52)");
                return true;
            case Commands.ForkHere when item is not null && _c.OpenSessionId is { } sid:
                // Fork after the selected message: everything up to and including it.
                long at = t!.Items.Skip(_body.Selected + 1).FirstOrDefault(i => i.Seq > item.Seq)?.Seq ?? 0;
                _ = _c.ForkAsync(sid, at > 0 ? at : null);
                return true;
            case Commands.Search:
                Ask("Search the transcript", "/", q =>
                {
                    _search = q;
                    FindNext(true);
                }, _search);
                return true;
            case Commands.SearchNext: FindNext(true); return true;
            case Commands.SearchPrev: FindNext(false); return true;
            case Commands.Approve or Commands.Deny or Commands.ApproveAlways when item is { Kind: ItemKind.Approval, Status: ApprovalStatus.Pending, RunId: { } run, RequestId: { } req }:
                return Decide(command, run, req);
        }
        return false;
    }

    private async Task LoadOlder()
    {
        _prependPending = true;
        await _c.LoadOlderAsync();
    }

    private void FindNext(bool forward)
    {
        if (_c.Transcript is not { } t || _search.Length == 0) return;
        if (t.Find(_search, _body.Selected, forward) is int i)
        {
            _body.Select(i);
            _c.Say($"/{_search}: n next, N previous");
        }
        else _c.Say($"/{_search}: no match");
    }

    private bool Decide(string command, string runId, string requestId)
    {
        if (command == Commands.Deny)
            Ask("Deny", "reason (optional): ", reason => _ = _c.DecideAsync(runId, requestId, false, reason.Trim().Length == 0 ? null : reason.Trim()));
        else _ = _c.DecideAsync(runId, requestId, true, always: command == Commands.ApproveAlways);
        return true;
    }

    // ---- composer ----

    private bool ComposerCommand(string command)
    {
        switch (command)
        {
            case Commands.Send:
                if (ComposerModel.SlashMatches(_composer.Value) is var slash && slash.Count() == 1 && ComposerModel.ParseSlash(_composer.Value) is { } partial
                    && slash.First().Name != "/" + partial.Name && partial.Argument.Length == 0)
                {
                    _composer.SetValue(slash.First().Name + " ");
                    return true;
                }
                string text = _composer.Value;
                _composer.SetValue("");
                ClearCompletions();
                _ = Submit(text);
                return true;
            case Commands.Newline:
                _composer.NewLine();
                return true;
            case Commands.Recall:
                if (_c.Composer.Recall(_composer.Value) is { } previous)
                {
                    _composer.SetValue(previous);
                    return true;
                }
                return false;
            case Commands.Editor:
                EditInEditor();
                return true;
        }
        return false;
    }

    private async Task Submit(string text)
    {
        if (await _c.SubmitAsync(text) == "help") App?.Invoke(OpenHelp);
        App?.Invoke(() =>
        {
            _body.Follow = true;
            _body.Invalidate();
            Refresh();
        });
    }

    /// <summary>Set when Ctrl+G asked for <c>$EDITOR</c>; the shell suspends the UI, runs it and reopens the window.</summary>
    public string? EditorRequest { get; private set; }

    public string ComposerText
    {
        get => _composer.Value;
        set => _composer.SetValue(value);
    }

    private void EditInEditor()
    {
        EditorRequest = _composer.Value;
        App?.RequestStop();
    }

    private void OnComposerChanged()
    {
        int rows = Math.Max(1, _composer.Value.Split('\n').Length);
        if (rows != _composerRows)
        {
            _composerRows = rows;
            Relayout();
        }
        UpdateMention();
        _hint.SetNeedsDraw();
    }

    private void UpdateMention()
    {
        (int Start, string Query)? mention = ComposerModel.MentionAt(_composer.Value, _composer.CursorIndex);
        if (mention == _mention) return;
        _mention = mention;
        _completionCts?.Cancel();
        if (mention is not { } m)
        {
            ClearCompletions();
            return;
        }
        CancellationTokenSource cts = new();
        _completionCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token);
                IReadOnlyList<string> files = await _c.CompleteFilesAsync(m.Query, cts.Token);
                App?.Invoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _completions = files;
                    _completionIndex = 0;
                    _hint.SetNeedsDraw();
                });
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or HarnessApiException or IOException) { }
        });
    }

    private void ClearCompletions()
    {
        _completions = [];
        _completionIndex = 0;
        _hint.SetNeedsDraw();
    }

    private bool CompletionKey(string name)
    {
        switch (name)
        {
            case "Tab" or "Enter" when _mention is { } m:
                ApplyCompletion(m, _completions[_completionIndex]);
                return true;
            case "Down" or "Ctrl+N":
                _completionIndex = (_completionIndex + 1) % _completions.Count;
                _hint.SetNeedsDraw();
                return true;
            case "Up" or "Ctrl+P":
                _completionIndex = (_completionIndex - 1 + _completions.Count) % _completions.Count;
                _hint.SetNeedsDraw();
                return true;
            case "Esc":
                ClearCompletions();
                return true;
        }
        return false;
    }

    private void ApplyCompletion((int Start, string Query) m, string path)
    {
        // The query typed after '@' is replaced by the chosen path.
        int typed = _composer.CursorIndex - (m.Start + 1);
        _composer.ReplaceBeforeCursor(Math.Max(0, typed), path + " ");
        _mention = null;
        ClearCompletions();
    }

    // ---- runs, approvals, triggers, run detail ----

    private bool RunsCommand(string command, string? runId)
    {
        switch (command)
        {
            case Commands.Open when runId is not null:
                _ = OpenRun(runId);
                return true;
            case Commands.CancelRun when runId is not null:
                _ = _c.CancelRunAsync(runId);
                return true;
            case Commands.Search when _c.Screen == Screen.Runs:
                OpenOverlay("Filter runs", "/", null, _ => { }, _listFilter, change: q => { _listFilter = q; _body.Invalidate(); Refresh(); }, compact: true);
                return true;
        }
        return false;
    }

    public async Task OpenRun(string runId)
    {
        _detailSelectedSeq = -1;
        await _c.OpenRunAsync(runId);
        App?.Invoke(() =>
        {
            ApplyScreen();
            FocusRegion(Region.Body);
        });
    }

    private bool ApprovalsCommand(string command)
    {
        string? id = SelectedId(_body, _bodyIds);
        ApprovalDto? a = _c.Store.Approvals.FirstOrDefault(x => x.RequestId == id);
        switch (command)
        {
            case Commands.Approve or Commands.Deny or Commands.ApproveAlways when a is not null:
                return Decide(command, a.RunId, a.RequestId);
            case Commands.Open when a is not null:
                _ = OpenSession(a.SessionId);
                return true;
        }
        return false;
    }

    private bool TriggersCommand(string command)
    {
        string? id = SelectedId(_body, _bodyIds);
        TriggerDto? t = _c.Store.Triggers.FirstOrDefault(x => x.Id == id);
        switch (command)
        {
            case Commands.FireTrigger when t is not null:
                Ask($"Fire {t.Id}", "text or {inputs as JSON}: ", input => _ = _c.FireTriggerAsync(t.Id, input.Trim()));
                return true;
            case Commands.ToggleTrigger when t is not null:
                _ = _c.ToggleTriggerAsync(t);
                return true;
            case Commands.Open when t?.LastRunId is { } run:
                _ = OpenRun(run);
                return true;
        }
        return false;
    }

    private ApprovalDto? PendingDetailApproval()
    {
        string? id = SelectedId(_body, _bodyIds);
        if (!long.TryParse(id, out long seq)) return null;
        EventDto? e = _c.DetailEvents.FirstOrDefault(x => x.Seq == seq);
        string? request = e?.Type == "APPROVAL_REQUESTED" ? e.Data["requestId"]?.GetValue<string>() : null;
        return request is null ? null : _c.Store.Approvals.FirstOrDefault(a => a.RequestId == request);
    }

    private bool DetailCommand(string command)
    {
        switch (command)
        {
            case Commands.Approve or Commands.Deny or Commands.ApproveAlways when PendingDetailApproval() is { } a:
                return Decide(command, a.RunId, a.RequestId);
            case Commands.CancelRun when _c.DetailRunId is { } run:
                _ = _c.CancelRunAsync(run);
                return true;
            case Commands.Open when _c.DetailRunId is { } run2 && _c.Store.Run(run2) is { } r:
                _ = OpenSession(r.SessionId);
                return true;
        }
        return false;
    }

    // ---- palette and help ----

    private void OpenPalette()
    {
        IReadOnlyList<OverlayItem> Filter(string q, int width)
        {
            List<(int Score, OverlayItem Item)> hits = [];
            foreach (CommandInfo info in Commands.All)
            {
                if (Fuzzy.Score($"{info.Name} {info.Description}", q) is not int score) continue;
                string keys = string.Join(" · ", _keys.KeysFor(info.Name));
                LineBuilder b = new(width);
                b.Row([new Span(info.Description, Style.Normal), new Span("  " + info.Name, Style.Dim)], new Span(keys, Style.Key));
                string name = info.Name;
                hits.Add((score, new OverlayItem(b.Lines[0], () => RunFromPalette(name))));
            }
            foreach ((string slash, string help) in ComposerModel.SlashCommands)
                if (Fuzzy.Score($"{slash} {help}", q) is int score)
                {
                    LineBuilder b = new(width);
                    b.Row([new Span(slash, Style.Accent), new Span("  " + help, Style.Dim)]);
                    string s = slash;
                    hits.Add((score + 5, new OverlayItem(b.Lines[0], () => { Show(Screen.Session); _composer.SetValue(s + " "); })));
                }
            return [.. hits.OrderBy(h => h.Score).Select(h => h.Item)];
        }
        OpenOverlay("Commands", "› ", Filter, _ => { });
    }

    private void RunFromPalette(string command)
    {
        RestoreFocus();
        if (!Execute(command)) _c.Say($"{command}: not available here");
    }

    private void OpenHelp()
    {
        List<OverlayItem> items = [];
        KeyScope[] scopes = Scopes();
        foreach (KeyScope scope in scopes.Concat(new[] { KeyScope.Global }).Distinct())
        {
            items.Add(new OverlayItem(Line.Of(scope switch
            {
                KeyScope.Global => "Everywhere", KeyScope.List => "Lists", KeyScope.SessionList => "Session list", KeyScope.Transcript => "Transcript",
                KeyScope.Approval => "Approval", KeyScope.Composer => "Composer", _ => scope.ToString(),
            }, Style.Header)));
            foreach ((string keys, string command) in _keys.BindingsIn(scope))
            {
                LineBuilder b = new(76);
                b.Row([new Span("  " + keys.PadRight(22), Style.Key), new Span(Commands.Find(command)?.Description ?? command, Style.Normal)]);
                items.Add(new OverlayItem(b.Lines[0], Id: command));
            }
            items.Add(new OverlayItem(Line.Empty));
        }
        items.Add(new OverlayItem(Line.Of("Remap any command in ~/.harness/tui.yaml (keys: { command: [\"Ctrl+X\"] }). Esc closes.", Style.Dim)));
        OpenOverlay("Keys", null, (_, _) => items, null);
    }

    // ---- small drawn views ----

    /// <summary>The "/ filter" row of the session list.</summary>
    private sealed class FilterLine(MainWindow w, Theme theme) : View
    {
        protected override bool OnDrawingContent(DrawContext? context)
        {
            Attribute normal = GetAttributeForRole(VisualRole.Normal);
            Move(0, 0);
            string text = w._filteringSessions ? "/ " + w._sessionFilter + "▏" : w._sessionFilter.Length > 0 ? "/ " + w._sessionFilter : "/ filter";
            SetAttribute(theme.For(w._filteringSessions ? Style.Accent : Style.Dim, normal));
            AddStr(text.Length > Viewport.Width ? text[..Viewport.Width] : text.PadRight(Viewport.Width));
            return true;
        }
    }

    /// <summary>The line above the composer: file completions, slash commands, queued messages, or the composer keys.</summary>
    private sealed class HintLine(MainWindow w, Theme theme) : View
    {
        protected override bool OnDrawingContent(DrawContext? context)
        {
            Attribute normal = GetAttributeForRole(VisualRole.Normal);
            List<Span> spans = [];
            if (w._completions.Count > 0)
            {
                spans.Add(new Span("@ ", Style.Accent));
                for (int i = 0; i < w._completions.Count && i < 8; i++)
                    spans.Add(new Span(w._completions[i] + "  ", i == w._completionIndex ? Style.Key : Style.Dim));
                spans.Add(new Span("Tab picks", Style.Dim));
            }
            else if (ComposerModel.SlashMatches(w._composer.Value).ToList() is { Count: > 0 } slash)
                foreach ((string name, string help) in slash.Take(6))
                {
                    spans.Add(new Span(name, Style.Accent));
                    spans.Add(new Span(slash.Count == 1 ? "  " + help : "  ", Style.Dim));
                }
            else
            {
                if (w._c.Composer.Queued.Count > 0) spans.Add(new Span($"{w._c.Composer.Queued.Count} queued · ", Style.Warning));
                if (w._c.Composer.Attachments.Count > 0) spans.Add(new Span($"{w._c.Composer.Attachments.Count} pasted · ", Style.Dim));
                spans.Add(new Span("@ file  / command  Ctrl+G editor  Alt+Enter newline  Enter send", Style.Dim));
            }
            DrawSpans(this, theme, normal, spans);
            return true;
        }
    }

    /// <summary>ctx · tokens · approvals waiting · flash messages · help hints.</summary>
    private sealed class StatusLine(MainWindow w, Theme theme) : View
    {
        protected override bool OnDrawingContent(DrawContext? context)
        {
            Attribute normal = GetAttributeForRole(VisualRole.Normal);
            List<Span> spans = [new(" ", Style.Normal)];
            TuiController c = w._c;
            if (!c.Connected) spans.Add(new Span("● disconnected · ", Style.Error));
            if (c.Flash is { } flash && (DateTime.UtcNow - c.FlashAt).TotalSeconds < 6)
                spans.Add(new Span(flash, Style.Warning));
            else
            {
                if (c.Transcript is { LastInputTokens: > 0 } t) spans.Add(new Span($"ctx {Format.Tokens(t.LastInputTokens)} · ", Style.Dim));
                if (c.OpenSession is { } s) spans.Add(new Span($"{Format.Tokens(s.InputTokens + s.OutputTokens)} tokens · ", Style.Dim));
                int waiting = c.Store.ApprovalsWaiting();
                if (waiting > 0) spans.Add(new Span($"{waiting} approval{(waiting == 1 ? "" : "s")} waiting (g a) · ", Style.Approval));
                if (w._keys.IsPending) spans.Add(new Span("g… · ", Style.Key));
                spans.Add(new Span("? help · Ctrl+K commands", Style.Dim));
            }
            DrawSpans(this, theme, normal, spans);
            return true;
        }
    }

    private static void DrawSpans(View v, Theme theme, Attribute normal, List<Span> spans)
    {
        v.Move(0, 0);
        int col = 0, width = v.Viewport.Width;
        foreach (Span s in LineBuilder.Clip(spans, width))
        {
            v.SetAttribute(theme.For(s.Style, normal));
            v.AddStr(s.Text);
            col += s.Text.Length;
        }
        v.SetAttribute(normal);
        if (col < width) v.AddStr(new string(' ', width - col));
    }
}
