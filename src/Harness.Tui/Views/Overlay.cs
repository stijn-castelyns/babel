using Harness.Tui.State;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Line = Harness.Tui.State.Line;

namespace Harness.Tui.Views;

/// <summary>An entry of an overlay list: what it shows and what choosing it does.</summary>
internal sealed record OverlayItem(Line Line, Action? OnChoose = null, string? Id = null);

/// <summary>
/// A centred box over the main window: the command palette, the <c>?</c> help, and one-line prompts (rename, deny
/// reason, trigger input, filters, confirmations). The input line is drawn by the box itself, so key routing stays in
/// the one dispatcher: printable keys edit <see cref="Input"/>, Up/Down move through the list, Enter accepts, Esc closes.
/// </summary>
internal sealed class Overlay : View
{
    private readonly Theme _theme;
    private readonly LinesView _list;
    private IReadOnlyList<OverlayItem> _items = [];

    public Overlay(Theme theme)
    {
        _theme = theme;
        X = Pos.Center();
        Y = Pos.Center();
        Width = Dim.Percent(70);
        Height = Dim.Percent(70);
        BorderStyle = LineStyle.Rounded;
        CanFocus = true;
        Visible = false;
        HotKeySpecifier = new System.Text.Rune(0xFFFF);
        _list = new LinesView(theme) { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), RowHighlight = true, CanFocus = false };
        _list.Source = width => [.. _items.Select((item, i) => item.Line with { Item = item.OnChoose is null && item.Id is null ? -1 : i })];
        Add(_list);
    }

    public bool HasInput { get; private set; }
    public string Input { get; private set; } = "";
    public string Prompt { get; private set; } = "";
    /// <summary>Recomputes the list from the input (palette filtering, live filters).</summary>
    public Func<string, IReadOnlyList<OverlayItem>>? Filter { get; private set; }
    /// <summary>Called with the input when Enter is pressed and the list has no selection to choose.</summary>
    public Action<string>? OnAccept { get; private set; }
    /// <summary>Called on every edit of the input (live filters).</summary>
    public Action<string>? OnChange { get; private set; }
    public Action? OnCancel { get; private set; }

    public void Open(string title, string? prompt, Func<string, IReadOnlyList<OverlayItem>>? filter, Action<string>? onAccept,
        string initial = "", Action<string>? onChange = null, Action? onCancel = null, bool compact = false)
    {
        Title = title;
        HasInput = prompt is not null;
        Prompt = prompt ?? "";
        Input = initial;
        Filter = filter;
        OnAccept = onAccept;
        OnChange = onChange;
        OnCancel = onCancel;
        Height = compact ? 5 : Dim.Percent(70);
        Width = compact ? Dim.Percent(60) : Dim.Percent(70);
        _list.Y = HasInput ? 2 : 0;
        Refilter();
        Visible = true;
        SetFocus();
        SetNeedsLayout();
        SetNeedsDraw();
    }

    public void Close()
    {
        Visible = false;
        HasInput = false;
        Filter = null;
        OnAccept = null;
        OnChange = null;
        OnCancel = null;
    }

    public void Cancel()
    {
        Action? cancel = OnCancel;
        Close();
        cancel?.Invoke();
    }

    private void Refilter()
    {
        _items = Filter?.Invoke(Input) ?? [];
        _list.Invalidate();
        int first = _items.ToList().FindIndex(i => i.OnChoose is not null || i.Id is not null);
        if (first >= 0) _list.Select(first);
        SetNeedsDraw();
    }

    public void Type(string text)
    {
        Input += text;
        OnChange?.Invoke(Input);
        Refilter();
    }

    public void Backspace()
    {
        if (Input.Length == 0) return;
        Input = Input[..^1];
        OnChange?.Invoke(Input);
        Refilter();
    }

    public void Move(int delta) => _list.Move(delta);

    public void Page(int direction) => _list.Page(direction);

    public void Accept()
    {
        OverlayItem? chosen = _list.Selected >= 0 && _list.Selected < _items.Count ? _items[_list.Selected] : null;
        Action<string>? accept = OnAccept;
        string input = Input;
        Close();
        if (chosen?.OnChoose is { } choose) choose();
        else accept?.Invoke(input);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (!HasInput) return false;
        Terminal.Gui.Drawing.Attribute normal = GetAttributeForRole(VisualRole.Normal);
        Move(0, 0);
        SetAttribute(_theme.For(Style.Accent, normal));
        AddStr(Prompt);
        SetAttribute(normal);
        string shown = Input.Length > Viewport.Width - Prompt.Length - 2 ? "…" + Input[^(Viewport.Width - Prompt.Length - 3)..] : Input;
        AddStr(shown);
        SetAttribute(_theme.For(Style.Accent, normal));
        AddStr("▏");
        SetAttribute(normal);
        int used = Prompt.Length + shown.Length + 1;
        if (used < Viewport.Width) AddStr(new string(' ', Viewport.Width - used));
        Move(0, 1);
        SetAttribute(_theme.For(Style.Dim, normal));
        AddStr(new string('─', Math.Max(0, Viewport.Width)));
        return false;
    }
}
