using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Tui.State;

/// <summary>
/// Turns a <see cref="TranscriptModel"/> into styled lines for a given width. Tool calls fold to one line (tool, main
/// argument, result summary, status); expanded, <c>edit</c> shows a diff, <c>shell</c> the command, exit code and output
/// tail, other tools their arguments and result. Pending approvals always show exactly what will run, with their keys.
/// Lines are cached per item version and width, so only changed items are re-rendered.
/// </summary>
public sealed class TranscriptRenderer
{
    private const int ExpandedLines = 40;
    private readonly Dictionary<TranscriptItem, (int Version, int Width, bool Expanded, IReadOnlyList<Line> Lines)> _cache = [];

    public string AgentLabel { get; set; } = "agent";

    /// <summary>All lines of the transcript. Each line's <see cref="Line.Item"/> is the index of its item.</summary>
    public IReadOnlyList<Line> Render(TranscriptModel model, int width)
    {
        List<Line> lines = [];
        if (model.HasMoreBefore) lines.Add(Line.Of("  ↑ older messages: scroll up or press g g to load", Style.Dim));
        HashSet<TranscriptItem> live = [];
        for (int i = 0; i < model.Items.Count; i++)
        {
            TranscriptItem item = model.Items[i];
            live.Add(item);
            bool gap = i > 0 && (item.Kind == ItemKind.User || (item.Kind == ItemKind.Assistant && model.Items[i - 1].Kind != ItemKind.User));
            if (gap) lines.Add(Line.Empty with { Item = i });
            IReadOnlyList<Line> rendered = RenderItem(item, width);
            foreach (Line l in rendered) lines.Add(l with { Item = i });
        }
        foreach (TranscriptItem stale in _cache.Keys.Where(k => !live.Contains(k)).ToList()) _cache.Remove(stale);
        return lines;
    }

    public IReadOnlyList<Line> RenderItem(TranscriptItem item, int width)
    {
        if (_cache.TryGetValue(item, out var hit) && hit.Version == item.Version && hit.Width == width && hit.Expanded == item.Expanded && !item.Streaming)
            return hit.Lines;
        LineBuilder b = new(width);
        switch (item.Kind)
        {
            case ItemKind.User:
                string label = item.Label ?? "you";
                b.Paragraph(item.Text.ToString(), Style.Normal, new Span(label.PadRight(5) + " ", Style.User), indent: Math.Min(label.Length, 12) + 1);
                break;
            case ItemKind.Assistant:
                Markdown(b, item.Text.ToString(), item.Streaming);
                break;
            case ItemKind.Tool:
                Tool(b, item);
                break;
            case ItemKind.Approval:
                Approval(b, item);
                break;
            case ItemKind.Notice:
                Style style = item.Level switch { NoticeLevel.Error => Style.Error, NoticeLevel.Warning => Style.Warning, _ => Style.Dim };
                b.Paragraph(item.Text.ToString(), style, new Span(item.Level == NoticeLevel.Info ? "· " : "! ", style), indent: 2);
                break;
            case ItemKind.RunEnd:
                bool ok = item.State == "succeeded";
                b.Paragraph($"── {item.State} · {item.Text}" + (item.Reason is { } r ? $" · {r}" : ""), ok ? Style.Dim : Style.Error);
                break;
        }
        IReadOnlyList<Line> lines = b.Lines;
        _cache[item] = (item.Version, width, item.Expanded, lines);
        return lines;
    }

    private static void Tool(LineBuilder b, TranscriptItem item)
    {
        string status = item.Ok switch { true => "✓", false => "✗", null => "…" };
        Style statusStyle = item.Ok switch { true => Style.Ok, false => Style.Error, null => Style.Running };
        string summary = Summary(item);
        b.Row(
            [
                new Span(item.Expanded ? "▾ " : "▸ ", Style.Dim), new Span(item.ToolName ?? "?", Style.Tool), new Span(" ", Style.Normal),
                new Span(item.MainArgument, Style.Normal), new Span(summary.Length > 0 ? " · " + summary : "", Style.Dim),
            ],
            new Span(status, statusStyle));
        if (!item.Expanded) return;
        Details(b, item.ToolName, item.Arguments, item.Result, "    ");
    }

    private static void Details(LineBuilder b, string? tool, JsonObject? args, string? result, string pad)
    {
        switch (tool)
        {
            case "edit":
                Diff(b, Str(args, "old"), Str(args, "new"), pad);
                break;
            case "write":
                foreach (string l in Clip(Str(args, "content") ?? "", ExpandedLines, fromEnd: false)) b.Row([new Span(pad + "+ " + l, Style.DiffAdd)]);
                break;
            case "shell":
                if (Str(args, "command") is { } cmd) b.Paragraph(cmd, Style.Code, new Span(pad + "$ ", Style.Dim), indent: pad.Length + 2);
                break;
            default:
                if (args is { Count: > 0 })
                    foreach ((string key, JsonNode? value) in args)
                        b.Paragraph(value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : value?.ToJsonString() ?? "null",
                            Style.Normal, new Span($"{pad}{key}: ", Style.Dim), indent: pad.Length + 2);
                break;
        }
        if (result is null) return;
        // Shell output is most useful at its end; other results at their start.
        string[] lines = result.Replace("\r", "").Split('\n');
        if (tool == "shell" && lines.Length > 0)
        {
            b.Row([new Span(pad + lines[0], TranscriptModel.IsError(lines[0]) || !lines[0].StartsWith("exit 0", StringComparison.Ordinal) ? Style.Warning : Style.Dim)]);
            foreach (string l in Clip(string.Join('\n', lines.Skip(1)), ExpandedLines / 2, fromEnd: true)) b.Row([new Span(pad + l, Style.Normal)]);
        }
        else if (tool is not ("edit" or "write") || TranscriptModel.IsError(result))
            foreach (string l in Clip(result, ExpandedLines, fromEnd: false)) b.Row([new Span(pad + l, TranscriptModel.IsError(result) ? Style.Error : Style.Normal)]);
    }

    private static IEnumerable<string> Clip(string text, int max, bool fromEnd)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        if (lines.Length <= max) return lines;
        return fromEnd ? [$"… {lines.Length - max} lines above", .. lines[^max..]] : [.. lines[..max], $"… {lines.Length - max} more lines"];
    }

    /// <summary>A unified-style diff of an edit: removed lines, then added lines, with common leading and trailing lines as context.</summary>
    internal static void Diff(LineBuilder b, string? oldText, string? newText, string pad)
    {
        string[] a = (oldText ?? "").Replace("\r", "").Split('\n'), c = (newText ?? "").Replace("\r", "").Split('\n');
        int head = 0;
        while (head < a.Length && head < c.Length && a[head] == c[head]) head++;
        int tail = 0;
        while (tail < a.Length - head && tail < c.Length - head && a[^(tail + 1)] == c[^(tail + 1)]) tail++;
        int shown = 0;
        void Emit(string prefix, string text, Style style)
        {
            if (shown++ < ExpandedLines) b.Row([new Span(pad + prefix + text, style)]);
        }
        for (int i = 0; i < head; i++) Emit("  ", a[i], Style.Dim);
        for (int i = head; i < a.Length - tail; i++) Emit("- ", a[i], Style.DiffDel);
        for (int i = head; i < c.Length - tail; i++) Emit("+ ", c[i], Style.DiffAdd);
        for (int i = a.Length - tail; i < a.Length; i++) Emit("  ", a[i], Style.Dim);
        if (shown > ExpandedLines) b.Row([new Span($"{pad}… {shown - ExpandedLines} more lines", Style.Dim)]);
    }

    private static void Approval(LineBuilder b, TranscriptItem item)
    {
        string what = item.Summary ?? item.MainArgument;
        switch (item.Status)
        {
            case ApprovalStatus.Pending:
                b.Row([new Span("! ", Style.Approval), new Span(item.ToolName ?? "?", Style.Approval), new Span("  " + Format.OneLine(what, 200), Style.Approval)],
                    new Span("[a]pprove [d]eny [A]lways", Style.Key));
                Details(b, item.ToolName, item.Arguments, null, "    ");
                break;
            case ApprovalStatus.Approved:
                b.Row([new Span("✓ approved ", Style.Ok), new Span($"{item.ToolName} {Format.OneLine(what, 120)}", Style.Dim),
                    new Span(item.DecidedBy is null ? "" : $" · {item.DecidedBy}", Style.Dim)]);
                break;
            case ApprovalStatus.Denied:
                b.Paragraph($"{item.ToolName} {Format.OneLine(what, 120)}" + (item.Reason is { Length: > 0 } r ? $" · {r}" : ""), Style.Dim,
                    new Span("✗ denied ", Style.Error), indent: 2);
                break;
        }
    }

    /// <summary>Assistant Markdown, lightly: headings bold, fenced code and inline code in the code style, bullets kept.</summary>
    internal static void Markdown(LineBuilder b, string text, bool streaming)
    {
        bool fence = false;
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Span? cursor = streaming && i == lines.Length - 1 ? new Span("▍", Style.Accent) : null;
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                string lang = line.Trim().TrimStart('`');
                b.Row([new Span(fence ? "┌ " + lang : "└", Style.Dim)]);
                continue;
            }
            if (fence)
            {
                b.Paragraph(line, Style.Code, new Span("│ ", Style.Dim), indent: 2, suffix: cursor);
                continue;
            }
            if (line.StartsWith('#'))
            {
                b.Paragraph(line.TrimStart('#').Trim(), Style.Header, suffix: cursor);
                continue;
            }
            Inline(b, line, cursor);
        }
    }

    /// <summary>Splits a line on backticks so inline code gets its own style; wraps as one paragraph.</summary>
    private static void Inline(LineBuilder b, string line, Span? cursor)
    {
        if (!line.Contains('`'))
        {
            string trimmed = line.TrimStart();
            int bullet = trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) ? line.Length - trimmed.Length + 2 : 0;
            b.Paragraph(StripEmphasis(line), Style.Assistant, indent: bullet, suffix: cursor);
            return;
        }
        // Mixed styles on one wrapped paragraph: wrap the plain text, then re-apply code styling by position.
        List<(int Start, int End)> code = [];
        System.Text.StringBuilder plain = new();
        bool inCode = false;
        int start = 0;
        foreach (char ch in line)
        {
            if (ch == '`')
            {
                if (inCode) code.Add((start, plain.Length));
                else start = plain.Length;
                inCode = !inCode;
                continue;
            }
            plain.Append(ch);
        }
        LineBuilder inner = new(b.Width);
        inner.Paragraph(plain.ToString(), Style.Assistant, suffix: cursor);
        int offset = 0;
        foreach (Line l in inner.Lines)
        {
            List<Span> spans = [];
            foreach (Span s in l.Spans)
            {
                if (s.Style != Style.Assistant)
                {
                    spans.Add(s);
                    continue;
                }
                int pos = plain.ToString().IndexOf(s.Text, offset, StringComparison.Ordinal);
                if (pos < 0) pos = offset;
                for (int i = 0; i < s.Text.Length;)
                {
                    int abs = pos + i;
                    bool isCode = code.Any(r => abs >= r.Start && abs < r.End);
                    int j = i;
                    while (j < s.Text.Length && code.Any(r => pos + j >= r.Start && pos + j < r.End) == isCode) j++;
                    spans.Add(new Span(s.Text[i..j], isCode ? Style.Code : Style.Assistant));
                    i = j;
                }
                offset = pos + s.Text.Length;
            }
            b.Add(new Line(spans));
        }
    }

    private static string StripEmphasis(string line) => line.Replace("**", "");

    private static string? Str(JsonObject? args, string key) =>
        args?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>The first line of a tool result (its header), as the folded line's summary.</summary>
    internal static string Summary(TranscriptItem item)
    {
        if (item.Result is null) return item.Ok is null ? "running" : "";
        string first = item.Result.Replace("\r", "").Split('\n')[0].Trim();
        return Format.OneLine(first, 80);
    }
}
