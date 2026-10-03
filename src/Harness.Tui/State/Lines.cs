using System.Text;

namespace Harness.Tui.State;

/// <summary>Semantic styles; the view maps them to terminal attributes per theme (dark, light, or no colour).</summary>
public enum Style
{
    Normal, Dim, Bold, User, Assistant, Code, Tool, Ok, Error, Warning, Approval, Key, DiffAdd, DiffDel, Header, Accent, Running,
}

public readonly record struct Span(string Text, Style Style);

/// <summary>One rendered row. <see cref="Item"/> is the transcript item or list row it belongs to (-1 for none).</summary>
public sealed record Line(IReadOnlyList<Span> Spans, int Item = -1)
{
    public static readonly Line Empty = new([]);

    public string Text => string.Concat(Spans.Select(s => s.Text));

    public static Line Of(string text, Style style = Style.Normal, int item = -1) => new([new Span(text, style)], item);
}

/// <summary>Builds lines from spans with word wrapping and a hanging indent.</summary>
public sealed class LineBuilder(int width)
{
    private readonly List<Line> _lines = [];
    private List<Span> _current = [];
    private int _col;

    public int Width { get; } = Math.Max(10, width);
    public int Item { get; set; } = -1;

    public IReadOnlyList<Line> Lines
    {
        get
        {
            Flush();
            return _lines;
        }
    }

    private void Flush()
    {
        if (_current.Count == 0) return;
        _lines.Add(new Line(_current, Item));
        _current = [];
        _col = 0;
    }

    public void Add(Line line) => _lines.Add(line with { Item = Item });

    public void Blank() => _lines.Add(Line.Empty with { Item = Item });

    /// <summary>
    /// Writes a paragraph: <paramref name="prefix"/> on the first line, <paramref name="indent"/> spaces on continuation
    /// lines, text wrapped at word boundaries (long words are split). Embedded newlines start new lines.
    /// </summary>
    public void Paragraph(string text, Style style, Span? prefix = null, int indent = 0, Span? suffix = null)
    {
        string pad = new(' ', indent);
        bool first = true;
        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            Flush();
            if (first && prefix is { } p) Write(p);
            else if (indent > 0) Write(new Span(pad, Style.Normal));
            first = false;
            int avail = Width - _col;
            string rest = raw.Replace("\t", "    ");
            while (rest.Length > avail)
            {
                int cut = rest.LastIndexOf(' ', Math.Min(avail, rest.Length - 1));
                if (cut <= 0) cut = avail;
                Write(new Span(rest[..cut], style));
                rest = rest[cut..].TrimStart(' ');
                Flush();
                if (indent > 0) Write(new Span(pad, Style.Normal));
                avail = Width - _col;
            }
            Write(new Span(rest, style));
        }
        if (suffix is { } s)
        {
            if (_col + s.Text.Length > Width) Flush();
            Write(s);
        }
        Flush();
    }

    /// <summary>One line, clipped to the width, with <paramref name="right"/> right-aligned when it fits.</summary>
    public void Row(IReadOnlyList<Span> left, Span? right = null)
    {
        Flush();
        int rightLen = right?.Text.Length ?? 0;
        int room = Width - (rightLen > 0 ? rightLen + 1 : 0);
        List<Span> spans = Clip(left, room);
        int used = spans.Sum(s => s.Text.Length);
        if (right is { } r && rightLen < Width)
        {
            spans.Add(new Span(new string(' ', Math.Max(1, Width - used - rightLen)), Style.Normal));
            spans.Add(r);
        }
        _lines.Add(new Line(spans, Item));
    }

    public static List<Span> Clip(IReadOnlyList<Span> spans, int width)
    {
        List<Span> result = [];
        int used = 0;
        foreach (Span s in spans)
        {
            if (used >= width) break;
            string text = s.Text.ReplaceLineEndings(" ");
            if (used + text.Length > width)
            {
                int keep = Math.Max(0, width - used - 1);
                result.Add(new Span(text[..keep] + "…", s.Style));
                used = width;
                break;
            }
            result.Add(s with { Text = text });
            used += text.Length;
        }
        return result;
    }

    private void Write(Span s)
    {
        if (s.Text.Length == 0 && _current.Count > 0) return;
        _current.Add(s);
        _col += s.Text.Length;
    }
}

/// <summary>Fixed-width columns for the runs, approvals and triggers tables.</summary>
public static class Table
{
    /// <summary>Column widths that fit <paramref name="width"/>: natural widths, with the last flexible column taking what is left.</summary>
    public static int[] Widths(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, int width, int flexColumn)
    {
        int[] w = [.. headers.Select(h => h.Length)];
        foreach (IReadOnlyList<string> row in rows)
            for (int i = 0; i < w.Length && i < row.Count; i++) w[i] = Math.Max(w[i], Math.Min(row[i].Length, 60));
        int total = w.Sum() + 2 * (w.Length - 1);
        if (total > width)
        {
            w[flexColumn] = Math.Max(4, w[flexColumn] - (total - width));
        }
        return w;
    }

    public static Line Row(IReadOnlyList<string> cells, int[] widths, Style style, int item = -1, IReadOnlyList<Style>? cellStyles = null)
    {
        List<Span> spans = [];
        for (int i = 0; i < widths.Length; i++)
        {
            string cell = i < cells.Count ? cells[i] : "";
            if (cell.Length > widths[i]) cell = widths[i] <= 1 ? cell[..widths[i]] : cell[..(widths[i] - 1)] + "…";
            spans.Add(new Span(i == widths.Length - 1 ? cell : cell.PadRight(widths[i]) + "  ", cellStyles?[i] ?? style));
        }
        return new Line(spans, item);
    }
}

/// <summary>Subsequence matching for filters and the palette; lower scores are better.</summary>
public static class Fuzzy
{
    public static int? Score(string text, string query)
    {
        if (query.Length == 0) return 0;
        int score = 0, last = -1, qi = 0;
        for (int i = 0; i < text.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(query[qi])) continue;
            score += last < 0 ? Math.Min(i, 10) : i - last - 1;
            last = i;
            qi++;
        }
        return qi == query.Length ? score : null;
    }
}

/// <summary>OSC 52 clipboard writes: they reach the local clipboard even over SSH.</summary>
public static class Osc52
{
    public static string Sequence(string text) => $"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\u0007";
}
