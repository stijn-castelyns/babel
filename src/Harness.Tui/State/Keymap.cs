namespace Harness.Tui.State;

/// <summary>Where a key binding applies. A key is looked up in the focused scope first, then in <see cref="Global"/>.</summary>
public enum KeyScope
{
    Global,
    /// <summary>Any list: the session list, runs, approvals, triggers, the run timeline.</summary>
    List,
    SessionList,
    Transcript,
    /// <summary>The selected item is a pending approval (a card in the transcript, a row in the inbox or the run timeline).</summary>
    Approval,
    Composer,
}

/// <summary>A named command with a description, shown by the palette and the <c>?</c> overlay.</summary>
public sealed record CommandInfo(string Name, string Description, KeyScope Scope);

/// <summary>Every command the TUI knows. Key bindings map to these names, so <c>tui.yaml</c> can remap any of them.</summary>
public static class Commands
{
    public const string Palette = "palette", Help = "help", Quit = "quit";
    public const string GoSessions = "go.sessions", GoRuns = "go.runs", GoApprovals = "go.approvals", GoTriggers = "go.triggers";
    public const string FocusNext = "focus.next", FocusPrev = "focus.prev", Back = "back", ToggleSidebar = "sidebar.toggle", Interrupt = "interrupt";
    public const string Down = "move.down", Up = "move.up", Top = "move.top", Bottom = "move.bottom", PageDown = "move.pagedown", PageUp = "move.pageup";
    public const string Open = "open", Search = "search", SearchNext = "search.next", SearchPrev = "search.prev";
    public const string NewSession = "session.new", Resume = "session.resume", Fork = "session.fork", Rename = "session.rename", Export = "session.export", Delete = "session.delete";
    public const string PrevTurn = "turn.prev", NextTurn = "turn.next", Expand = "toggle.expand", ExpandAll = "toggle.all", Copy = "copy", ForkHere = "fork.here", FocusComposer = "focus.composer";
    public const string Approve = "approve", Deny = "deny", ApproveAlways = "approve.always";
    public const string Send = "send", Newline = "newline", Recall = "recall", Editor = "editor";
    public const string FireTrigger = "trigger.fire", ToggleTrigger = "trigger.toggle", CancelRun = "run.cancel";

    public static readonly IReadOnlyList<CommandInfo> All =
    [
        new(Palette, "Command palette", KeyScope.Global),
        new(Help, "Bindings for the focused view", KeyScope.Global),
        new(GoSessions, "Go to sessions", KeyScope.Global),
        new(GoRuns, "Go to runs", KeyScope.Global),
        new(GoApprovals, "Go to the approvals inbox", KeyScope.Global),
        new(GoTriggers, "Go to triggers", KeyScope.Global),
        new(FocusNext, "Focus the next region", KeyScope.Global),
        new(FocusPrev, "Focus the previous region", KeyScope.Global),
        new(Back, "Back, or close the overlay", KeyScope.Global),
        new(ToggleSidebar, "Toggle the sidebar", KeyScope.Global),
        new(Interrupt, "Cancel the running turn; again within 2 seconds to quit", KeyScope.Global),
        new(Quit, "Quit", KeyScope.Global),
        new(Down, "Move down", KeyScope.List),
        new(Up, "Move up", KeyScope.List),
        new(Top, "Jump to the top", KeyScope.List),
        new(Bottom, "Jump to the bottom", KeyScope.List),
        new(PageDown, "Page down", KeyScope.List),
        new(PageUp, "Page up", KeyScope.List),
        new(Open, "Open the selected item", KeyScope.List),
        new(Search, "Filter the list, or search the transcript", KeyScope.List),
        new(SearchNext, "Next match", KeyScope.List),
        new(SearchPrev, "Previous match", KeyScope.List),
        new(NewSession, "New session", KeyScope.SessionList),
        new(Resume, "Resume the selected session", KeyScope.SessionList),
        new(Fork, "Fork the selected session", KeyScope.SessionList),
        new(Rename, "Rename the selected session", KeyScope.SessionList),
        new(Export, "Export the selected session as Markdown", KeyScope.SessionList),
        new(Delete, "Delete the selected session (with confirmation)", KeyScope.SessionList),
        new(PrevTurn, "Previous user turn", KeyScope.Transcript),
        new(NextTurn, "Next user turn", KeyScope.Transcript),
        new(Expand, "Expand or collapse the selected tool call", KeyScope.Transcript),
        new(ExpandAll, "Expand or collapse all tool calls", KeyScope.Transcript),
        new(Copy, "Copy the selected message (OSC 52)", KeyScope.Transcript),
        new(ForkHere, "Fork the session at the selected message", KeyScope.Transcript),
        new(FocusComposer, "Focus the composer", KeyScope.Transcript),
        new(Approve, "Approve", KeyScope.Approval),
        new(Deny, "Deny, with an optional reason", KeyScope.Approval),
        new(ApproveAlways, "Approve and allow this exact call for the rest of the session", KeyScope.Approval),
        new(FireTrigger, "Fire the selected trigger", KeyScope.List),
        new(ToggleTrigger, "Enable or disable the selected trigger", KeyScope.List),
        new(CancelRun, "Cancel the selected run", KeyScope.List),
        new(Send, "Send the message", KeyScope.Composer),
        new(Newline, "New line", KeyScope.Composer),
        new(Recall, "Recall the previous message (on an empty composer)", KeyScope.Composer),
        new(Editor, "Edit the message in $EDITOR", KeyScope.Composer),
    ];

    public static CommandInfo? Find(string name) => All.FirstOrDefault(c => c.Name == name);
}

/// <summary>What a key press resolved to.</summary>
public readonly record struct KeyResult(string? Command, bool Pending, bool Swallowed = false)
{
    public static readonly KeyResult None = new(null, false);
    public static readonly KeyResult Prefix = new(null, true);
    /// <summary>The second key of an unknown sequence (<c>g x</c>): nothing happens, and the key is not typed either.</summary>
    public static readonly KeyResult Dropped = new(null, false, true);
    public bool Handled => Command is not null || Pending || Swallowed;
}

/// <summary>
/// Key bindings as named commands, with multi-key sequences (<c>g s</c>, <c>g g</c>). Keys are written the way
/// <see cref="KeyNames"/> normalises them: <c>Ctrl+K</c>, <c>Alt+Enter</c>, <c>Shift+Tab</c>, <c>g</c>, <c>G</c>, <c>?</c>.
/// </summary>
public sealed class Keymap
{
    private readonly Dictionary<KeyScope, List<(string[] Keys, string Command)>> _bindings = [];
    private readonly List<string> _pending = [];
    private KeyScope[]? _pendingScopes;

    public static Keymap Default()
    {
        Keymap k = new();
        k.Bind(KeyScope.Global, Commands.Palette, "Ctrl+K");
        k.Bind(KeyScope.Global, Commands.Help, "?");
        k.Bind(KeyScope.Global, Commands.GoSessions, "g s");
        k.Bind(KeyScope.Global, Commands.GoRuns, "g r");
        k.Bind(KeyScope.Global, Commands.GoApprovals, "g a");
        k.Bind(KeyScope.Global, Commands.GoTriggers, "g t");
        k.Bind(KeyScope.Global, Commands.FocusNext, "Tab");
        k.Bind(KeyScope.Global, Commands.FocusPrev, "Shift+Tab");
        k.Bind(KeyScope.Global, Commands.Back, "Esc");
        k.Bind(KeyScope.Global, Commands.ToggleSidebar, "Ctrl+B");
        k.Bind(KeyScope.Global, Commands.Interrupt, "Ctrl+C");
        k.Bind(KeyScope.Global, Commands.Quit, "Ctrl+Q");
        // ':' opens the palette too, but not while typing in the composer (it is not a Global binding there).
        k.Bind(KeyScope.List, Commands.Palette, ":");
        k.Bind(KeyScope.Transcript, Commands.Palette, ":");

        foreach (KeyScope s in new[] { KeyScope.List, KeyScope.Transcript })
        {
            k.Bind(s, Commands.Down, "Down", "j");
            k.Bind(s, Commands.Up, "Up", "k");
            k.Bind(s, Commands.Top, "g g", "Home");
            k.Bind(s, Commands.Bottom, "G", "End");
            k.Bind(s, Commands.PageDown, "PageDown", "Ctrl+D");
            k.Bind(s, Commands.PageUp, "PageUp", "Ctrl+U");
            k.Bind(s, Commands.Search, "/");
            k.Bind(s, Commands.SearchNext, "n");
            k.Bind(s, Commands.SearchPrev, "N");
        }
        k.Bind(KeyScope.List, Commands.Open, "Enter", "l");
        k.Bind(KeyScope.List, Commands.FireTrigger, "F");
        k.Bind(KeyScope.List, Commands.ToggleTrigger, "Space");
        k.Bind(KeyScope.List, Commands.CancelRun, "x");

        k.Bind(KeyScope.SessionList, Commands.Open, "Enter", "l");
        k.Bind(KeyScope.SessionList, Commands.NewSession, "n");
        k.Bind(KeyScope.SessionList, Commands.Resume, "r");
        k.Bind(KeyScope.SessionList, Commands.Fork, "f");
        k.Bind(KeyScope.SessionList, Commands.Rename, "R");
        k.Bind(KeyScope.SessionList, Commands.Export, "e");
        k.Bind(KeyScope.SessionList, Commands.Delete, "d");

        k.Bind(KeyScope.Transcript, Commands.PrevTurn, "[");
        k.Bind(KeyScope.Transcript, Commands.NextTurn, "]");
        k.Bind(KeyScope.Transcript, Commands.Expand, "Space", "o", "Enter");
        k.Bind(KeyScope.Transcript, Commands.ExpandAll, "O");
        k.Bind(KeyScope.Transcript, Commands.Copy, "y");
        k.Bind(KeyScope.Transcript, Commands.ForkHere, "f");
        k.Bind(KeyScope.Transcript, Commands.FocusComposer, "i");

        k.Bind(KeyScope.Approval, Commands.Approve, "a");
        k.Bind(KeyScope.Approval, Commands.Deny, "d");
        k.Bind(KeyScope.Approval, Commands.ApproveAlways, "A");

        k.Bind(KeyScope.Composer, Commands.Send, "Enter");
        k.Bind(KeyScope.Composer, Commands.Newline, "Alt+Enter");
        k.Bind(KeyScope.Composer, Commands.Recall, "Up");
        k.Bind(KeyScope.Composer, Commands.Editor, "Ctrl+G");
        return k;
    }

    /// <summary>Adds bindings for a command. Each key string is one binding; spaces separate the keys of a sequence.</summary>
    public void Bind(KeyScope scope, string command, params string[] keys)
    {
        if (!_bindings.TryGetValue(scope, out var list)) _bindings[scope] = list = [];
        foreach (string key in keys) list.Add(([.. key.Split(' ', StringSplitOptions.RemoveEmptyEntries)], command));
    }

    /// <summary>
    /// Replaces the bindings of the named commands in every scope they appear in (the <c>tui.yaml</c> <c>keys:</c> section).
    /// Unknown command names are errors, so a typo does not silently do nothing.
    /// </summary>
    public void Override(IReadOnlyDictionary<string, IReadOnlyList<string>> overrides)
    {
        foreach ((string command, IReadOnlyList<string> keys) in overrides)
        {
            CommandInfo info = Commands.Find(command) ?? throw new ArgumentException($"Unknown TUI command '{command}' in key bindings.");
            HashSet<KeyScope> scopes = [.. _bindings.Where(kv => kv.Value.Any(b => b.Command == command)).Select(kv => kv.Key)];
            if (scopes.Count == 0) scopes.Add(info.Scope);
            foreach (KeyScope scope in scopes)
            {
                _bindings[scope].RemoveAll(b => b.Command == command);
                Bind(scope, command, [.. keys]);
            }
        }
    }

    /// <summary>The bindings of a command in a scope (or any scope), for the palette and the help overlay.</summary>
    public IReadOnlyList<string> KeysFor(string command, KeyScope? scope = null) =>
        [.. _bindings.Where(kv => scope is null || kv.Key == scope).SelectMany(kv => kv.Value).Where(b => b.Command == command)
            .Select(b => string.Join(' ', b.Keys)).Distinct()];

    public IReadOnlyList<(string Keys, string Command)> BindingsIn(KeyScope scope) =>
        _bindings.TryGetValue(scope, out var list) ? [.. list.GroupBy(b => b.Command).Select(g => (string.Join(" · ", g.Select(b => string.Join(' ', b.Keys))), g.Key))] : [];

    public bool IsPending => _pending.Count > 0;

    /// <summary>A key that types a character: a single character, or Space.</summary>
    public static bool IsPrintable(string key) => key.Length == 1 || key == "Space";

    public void Reset()
    {
        _pending.Clear();
        _pendingScopes = null;
    }

    /// <summary>
    /// Feeds one key. <paramref name="scopes"/> are searched in order (most specific first). Returns the command when a
    /// binding completes, <see cref="KeyResult.Prefix"/> while a sequence is incomplete, and <see cref="KeyResult.None"/>
    /// when nothing matches (the key then belongs to the focused widget, for example as typed text).
    /// </summary>
    public KeyResult Feed(string key, params KeyScope[] scopes)
    {
        if (_pending.Count > 0 && _pendingScopes is not null) scopes = _pendingScopes;
        _pending.Add(key);
        bool prefix = false, typing = scopes.Contains(KeyScope.Composer);
        foreach (KeyScope scope in scopes)
        {
            if (!_bindings.TryGetValue(scope, out var list)) continue;
            foreach ((string[] keys, string command) in list)
            {
                // While typing, printable global keys ('?', 'g s') are text; only modified keys (Ctrl+K, Esc, Tab) act.
                if (scope == KeyScope.Global && typing && IsPrintable(keys[0])) continue;
                if (keys.Length < _pending.Count || !keys.Take(_pending.Count).SequenceEqual(_pending)) continue;
                if (keys.Length == _pending.Count)
                {
                    Reset();
                    return new KeyResult(command, false);
                }
                prefix = true;
            }
        }
        if (prefix)
        {
            _pendingScopes = scopes;
            return KeyResult.Prefix;
        }
        bool wasSequence = _pending.Count > 1;
        Reset();
        // An unknown second key of a sequence ('g x') is swallowed rather than typed; a first key falls through.
        return wasSequence ? KeyResult.Dropped : KeyResult.None;
    }
}
