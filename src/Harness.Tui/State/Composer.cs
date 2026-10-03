using System.Text;
using System.Text.RegularExpressions;

namespace Harness.Tui.State;

/// <summary>A large paste kept out of the composer as a chip; it is expanded into the message when it is sent.</summary>
public sealed record Attachment(int Number, string Text)
{
    public int LineCount => Text.Split('\n').Length;
    public string Chip => $"[paste #{Number}: {LineCount} lines]";
}

public sealed record SlashCommand(string Name, string Argument);

/// <summary>
/// The composer's logic, apart from the text widget: message recall, paste chips, <c>@</c> file references, slash
/// commands and the queue of messages typed while a turn is running.
/// </summary>
public sealed partial class ComposerModel
{
    public const int LargePasteLines = 12, LargePasteChars = 1500;

    public static readonly IReadOnlyList<(string Name, string Help)> SlashCommands =
    [
        ("/new", "start a new session in this workspace"),
        ("/fork", "fork this session at the latest message"),
        ("/model", "/model <profile>: new session with another model profile"),
        ("/agent", "/agent <name>: new session with another agent"),
        ("/export", "write the transcript as Markdown to a file"),
        ("/usage", "token usage of this session"),
        ("/compact", "summarise older history before the next turn"),
        ("/approvals", "session approval policy"),
        ("/help", "keys and commands"),
    ];

    private readonly List<string> _history = [];
    private readonly List<Attachment> _attachments = [];
    private int _recall = -1;
    private int _pastes;

    public Queue<string> Queued { get; } = new();
    public IReadOnlyList<Attachment> Attachments => _attachments;

    /// <summary>Remembers a sent message for recall.</summary>
    public void Remember(string text)
    {
        if (text.Length > 0 && (_history.Count == 0 || _history[^1] != text)) _history.Add(text);
        _recall = -1;
    }

    /// <summary>
    /// Up on an empty composer (or on a recalled message, unchanged) steps back through sent messages. Returns null when there
    /// is nothing to recall, so the key moves the cursor instead.
    /// </summary>
    public string? Recall(string current)
    {
        if (_history.Count == 0) return null;
        bool recalling = _recall >= 0 && current == _history[_recall];
        if (current.Length > 0 && !recalling) return null;
        _recall = recalling ? Math.Max(0, _recall - 1) : _history.Count - 1;
        return _history[_recall];
    }

    /// <summary>Down while recalling steps forward again; past the newest it clears the composer.</summary>
    public string? RecallNext(string current)
    {
        if (_recall < 0 || current != _history[_recall]) return null;
        _recall++;
        if (_recall < _history.Count) return _history[_recall];
        _recall = -1;
        return "";
    }

    public static bool IsLargePaste(string text) => text.Length > LargePasteChars || text.Count(c => c == '\n') >= LargePasteLines;

    /// <summary>Keeps a large paste as an attachment and returns the chip to insert in its place.</summary>
    public string AddPaste(string text)
    {
        Attachment a = new(++_pastes, text.Replace("\r", ""));
        _attachments.Add(a);
        return a.Chip;
    }

    /// <summary>
    /// The message as sent: paste chips are replaced by their text in fenced blocks, and <c>@path</c> mentions are listed as
    /// files the agent should read (references, not inline content).
    /// </summary>
    public string Compose(string text)
    {
        StringBuilder sb = new(text.TrimEnd());
        foreach (Attachment a in _attachments.ToList())
        {
            int at = sb.ToString().IndexOf(a.Chip, StringComparison.Ordinal);
            if (at < 0) continue;
            sb.Remove(at, a.Chip.Length).Insert(at, $"\n```\n{a.Text.TrimEnd('\n')}\n```\n");
        }
        _attachments.Clear();
        List<string> files = [.. Mentions(text).Distinct()];
        if (files.Count > 0) sb.Append("\n\nReferenced files (read them with the read tool): ").Append(string.Join(", ", files));
        return sb.ToString().Trim();
    }

    public static IEnumerable<string> Mentions(string text) =>
        MentionPattern().Matches(text).Select(m => m.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')'));

    [GeneratedRegex(@"(?:^|\s)@([\w./\-]+)")]
    private static partial Regex MentionPattern();

    /// <summary>The <c>@</c> token ending at <paramref name="cursor"/>, if the user is typing one: its start (at the @) and the query.</summary>
    public static (int Start, string Query)? MentionAt(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        int i = cursor - 1;
        while (i >= 0 && !char.IsWhiteSpace(text[i]) && text[i] != '@') i--;
        if (i < 0 || text[i] != '@') return null;
        if (i > 0 && !char.IsWhiteSpace(text[i - 1])) return null;
        return (i, text[(i + 1)..cursor]);
    }

    /// <summary>A message that starts with <c>/name</c> (and is one line) is a slash command.</summary>
    public static SlashCommand? ParseSlash(string text)
    {
        string t = text.Trim();
        if (!t.StartsWith('/') || t.Contains('\n') || t.Length < 2 || !char.IsLetter(t[1])) return null;
        int space = t.IndexOf(' ');
        return space < 0 ? new SlashCommand(t[1..].ToLowerInvariant(), "") : new SlashCommand(t[1..space].ToLowerInvariant(), t[(space + 1)..].Trim());
    }

    /// <summary>Slash commands matching what is typed so far, for the hint line.</summary>
    public static IEnumerable<(string Name, string Help)> SlashMatches(string text)
    {
        string t = text.TrimStart();
        if (!t.StartsWith('/') || t.Contains(' ') || t.Contains('\n')) return [];
        return SlashCommands.Where(c => c.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase));
    }
}
