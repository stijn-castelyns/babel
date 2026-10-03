using Harness.Tui.State;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Harness.Tui.Views;

/// <summary>
/// A scrolling view of styled lines with an item cursor. Lines come from <see cref="Source"/> for the current width, so
/// only the visible rows are drawn; each line's <see cref="Line.Item"/> groups it under a selectable item. With
/// <see cref="Follow"/>, the view sticks to the bottom as lines arrive, unless the user has moved up, in which case a
/// "↓ new output" marker appears instead.
/// </summary>
internal sealed class LinesView : View
{
    private readonly Theme _theme;
    private IReadOnlyList<Line> _lines = [];
    private int _width = -1;
    private bool _dirty = true;
    private int _top;
    private int _lastCount;

    public LinesView(Theme theme)
    {
        _theme = theme;
        CanFocus = true;
    }

    public Func<int, IReadOnlyList<Line>> Source { get; set; } = _ => [];
    /// <summary>Highlight the whole selected row (lists) instead of only a gutter mark (transcript).</summary>
    public bool RowHighlight { get; init; }
    public bool Gutter { get; init; } = true;
    /// <summary>Stick to the bottom as lines are added; off once the user moves up, on again at the bottom.</summary>
    public bool Follow { get; set; }
    public bool NewOutputBelow { get; private set; }
    public int Selected { get; private set; } = -1;

    public event Action<int>? SelectionChanged;
    /// <summary>The user tried to move above the first line (load older history).</summary>
    public event Action? ReachedTop;

    public int ContentWidth => Math.Max(10, Viewport.Width - (Gutter ? 2 : 0));

    public IReadOnlyList<Line> Lines
    {
        get
        {
            Ensure();
            return _lines;
        }
    }

    public void Invalidate()
    {
        _dirty = true;
        SetNeedsDraw();
    }

    private void Ensure()
    {
        int width = ContentWidth;
        if (!_dirty && width == _width) return;
        _dirty = false;
        _width = width;
        _lines = Source(width);
        List<int> stops = Stops();
        if (Follow && stops.Count > 0)
        {
            Selected = stops[^1];
            _top = Math.Max(0, _lines.Count - Math.Max(1, Viewport.Height));
            NewOutputBelow = false;
        }
        else
        {
            if (stops.Count > 0 && !stops.Contains(Selected)) Selected = stops.Where(s => s <= Selected).DefaultIfEmpty(stops[0]).Max();
            if (stops.Count == 0) Selected = -1;
            if (FollowCapable && _lines.Count > _lastCount && _lastCount > 0 && _top + Viewport.Height < _lines.Count) NewOutputBelow = true;
            _top = Math.Clamp(_top, 0, Math.Max(0, _lines.Count - 1));
        }
        _lastCount = _lines.Count;
    }

    private List<int> Stops()
    {
        List<int> stops = [];
        foreach (Line l in _lines)
            if (l.Item >= 0 && (stops.Count == 0 || stops[^1] != l.Item)) stops.Add(l.Item);
        return stops;
    }

    public void Select(int item, bool scroll = true)
    {
        Ensure();
        Selected = item;
        List<int> stops = Stops();
        Follow = stops.Count > 0 && item == stops[^1] && FollowCapable;
        if (scroll) EnsureVisible();
        SelectionChanged?.Invoke(item);
        SetNeedsDraw();
    }

    /// <summary>Whether this view follows output at all (the transcript and the run timeline do; lists do not).</summary>
    public bool FollowCapable { get; init; }

    public void Move(int delta)
    {
        Ensure();
        List<int> stops = Stops();
        if (stops.Count == 0) return;
        int at = stops.IndexOf(Selected);
        if (at < 0) at = delta > 0 ? -1 : stops.Count;
        int next = at + delta;
        if (next < 0)
        {
            _top = 0;
            ReachedTop?.Invoke();
            next = 0;
        }
        Select(stops[Math.Clamp(next, 0, stops.Count - 1)]);
    }

    public void MoveTop()
    {
        Ensure();
        List<int> stops = Stops();
        if (stops.Count == 0) return;
        _top = 0;
        ReachedTop?.Invoke();
        Select(stops[0]);
    }

    public void MoveBottom()
    {
        Ensure();
        List<int> stops = Stops();
        if (stops.Count == 0) return;
        Select(stops[^1]);
        _top = Math.Max(0, _lines.Count - Viewport.Height);
        NewOutputBelow = false;
    }

    public void Page(int direction)
    {
        Ensure();
        int height = Math.Max(1, Viewport.Height - 1);
        _top = Math.Clamp(_top + direction * height, 0, Math.Max(0, _lines.Count - Viewport.Height));
        // Select the first item that starts on screen.
        for (int i = _top; i < Math.Min(_lines.Count, _top + Viewport.Height); i++)
        {
            if (_lines[i].Item < 0) continue;
            Select(_lines[i].Item, scroll: false);
            break;
        }
        if (direction < 0 && _top == 0) ReachedTop?.Invoke();
        if (_top + Viewport.Height >= _lines.Count) NewOutputBelow = false;
        SetNeedsDraw();
    }

    /// <summary>Scrolls so the selected item is visible: its first line, and as much of the rest as fits.</summary>
    public void EnsureVisible()
    {
        Ensure();
        int first = -1, last = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Item != Selected) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return;
        int height = Math.Max(1, Viewport.Height);
        if (first < _top) _top = first;
        else if (last >= _top + height) _top = Math.Min(first, last - height + 1);
        if (_top + height >= _lines.Count) NewOutputBelow = false;
        SetNeedsDraw();
    }

    /// <summary>Keeps the same item at the same place after lines were inserted above it (older history loaded).</summary>
    public void KeepAnchor(int insertedItems)
    {
        if (insertedItems <= 0) return;
        int oldSelected = Selected;
        _dirty = true;
        Ensure();
        Selected = oldSelected + insertedItems;
        EnsureVisible();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Ensure();
        // Layout may have changed the height since the lines were computed; following means the last row is visible.
        if (Follow) _top = Math.Max(0, _lines.Count - Math.Max(1, Viewport.Height));
        Attribute normal = GetAttributeForRole(VisualRole.Normal);
        int width = Viewport.Width, height = Viewport.Height;
        string blank = new(' ', Math.Max(0, width));
        for (int row = 0; row < height; row++)
        {
            Move(0, row);
            SetAttribute(normal);
            int index = _top + row;
            if (index >= _lines.Count)
            {
                AddStr(blank);
                continue;
            }
            Line line = _lines[index];
            bool selected = line.Item >= 0 && line.Item == Selected;
            int col = 0;
            if (Gutter)
            {
                SetAttribute(_theme.For(Style.Accent, normal));
                AddStr(selected && HasFocus ? "▌ " : "  ");
                col = 2;
            }
            bool reverse = selected && RowHighlight && HasFocus;
            foreach (Span span in line.Spans)
            {
                if (col >= width) break;
                string text = span.Text.Length > width - col ? span.Text[..(width - col)] : span.Text;
                Attribute a = _theme.For(span.Style, normal);
                SetAttribute(reverse ? new Attribute(a.Foreground, a.Background, a.Style | TextStyle.Reverse) : a);
                AddStr(text);
                col += text.Length;
            }
            SetAttribute(reverse ? _theme.Selected(normal) : normal);
            if (col < width) AddStr(blank[..(width - col)]);
        }
        if (NewOutputBelow && width > 18)
        {
            Move(width - 17, height - 1);
            SetAttribute(_theme.For(Style.Key, normal));
            AddStr(" ↓ new output (G) ");
        }
        return true;
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown) || mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            Ensure();
            int delta = mouse.Flags.HasFlag(MouseFlags.WheeledDown) ? 3 : -3;
            _top = Math.Clamp(_top + delta, 0, Math.Max(0, _lines.Count - Viewport.Height));
            if (delta < 0)
            {
                Follow = false;
                if (_top == 0) ReachedTop?.Invoke();
            }
            if (_top + Viewport.Height >= _lines.Count) NewOutputBelow = false;
            SetNeedsDraw();
            return true;
        }
        if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) && mouse.Position is { } p)
        {
            SetFocus();
            int index = _top + p.Y;
            if (index < Lines.Count && _lines[index].Item >= 0) Select(_lines[index].Item, scroll: false);
            return true;
        }
        return base.OnMouseEvent(mouse);
    }
}
