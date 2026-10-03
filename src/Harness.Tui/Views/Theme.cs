using Harness.Tui.State;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Harness.Tui.Views;

/// <summary>
/// Maps the semantic <see cref="Style"/>s to terminal attributes. Uses the 16 named colours (which every terminal has)
/// over the terminal's own background; <c>none</c> (for <c>NO_COLOR</c>) uses only bold, faint, underline and reverse.
/// </summary>
internal sealed class Theme
{
    private readonly Dictionary<Style, (ColorName16? Fg, TextStyle Text)> _map;

    public string Name { get; }
    public bool Colour => Name != "none";

    private Theme(string name, Dictionary<Style, (ColorName16?, TextStyle)> map)
    {
        Name = name;
        _map = map;
    }

    public static Theme Create(string name) => name switch
    {
        "none" => new Theme("none", new()
        {
            [Style.Dim] = (null, TextStyle.Faint), [Style.Bold] = (null, TextStyle.Bold), [Style.User] = (null, TextStyle.Bold),
            [Style.Header] = (null, TextStyle.Bold), [Style.Approval] = (null, TextStyle.Bold), [Style.Key] = (null, TextStyle.Underline),
            [Style.Error] = (null, TextStyle.Bold), [Style.Code] = (null, TextStyle.None), [Style.DiffDel] = (null, TextStyle.Faint),
        }),
        "light" => new Theme("light", new()
        {
            [Style.Dim] = (ColorName16.DarkGray, TextStyle.None), [Style.Bold] = (null, TextStyle.Bold), [Style.User] = (ColorName16.Blue, TextStyle.Bold),
            [Style.Code] = (ColorName16.Magenta, TextStyle.None), [Style.Tool] = (ColorName16.Cyan, TextStyle.None), [Style.Ok] = (ColorName16.Green, TextStyle.None),
            [Style.Error] = (ColorName16.Red, TextStyle.None), [Style.Warning] = (ColorName16.Magenta, TextStyle.None),
            [Style.Approval] = (ColorName16.Red, TextStyle.Bold), [Style.Key] = (ColorName16.Blue, TextStyle.Bold),
            [Style.DiffAdd] = (ColorName16.Green, TextStyle.None), [Style.DiffDel] = (ColorName16.Red, TextStyle.None),
            [Style.Header] = (null, TextStyle.Bold), [Style.Accent] = (ColorName16.Blue, TextStyle.None), [Style.Running] = (ColorName16.Blue, TextStyle.None),
        }),
        _ => new Theme("dark", new()
        {
            [Style.Dim] = (ColorName16.DarkGray, TextStyle.None), [Style.Bold] = (null, TextStyle.Bold), [Style.User] = (ColorName16.BrightBlue, TextStyle.Bold),
            [Style.Code] = (ColorName16.BrightCyan, TextStyle.None), [Style.Tool] = (ColorName16.Cyan, TextStyle.None), [Style.Ok] = (ColorName16.BrightGreen, TextStyle.None),
            [Style.Error] = (ColorName16.BrightRed, TextStyle.None), [Style.Warning] = (ColorName16.Yellow, TextStyle.None),
            [Style.Approval] = (ColorName16.BrightYellow, TextStyle.Bold), [Style.Key] = (ColorName16.BrightMagenta, TextStyle.Bold),
            [Style.DiffAdd] = (ColorName16.Green, TextStyle.None), [Style.DiffDel] = (ColorName16.Red, TextStyle.None),
            [Style.Header] = (null, TextStyle.Bold), [Style.Accent] = (ColorName16.BrightCyan, TextStyle.None), [Style.Running] = (ColorName16.BrightBlue, TextStyle.None),
        }),
    };

    public Attribute For(Style style, Attribute normal)
    {
        if (!_map.TryGetValue(style, out var m)) return normal;
        Color fg = m.Fg is { } name ? new Color(name) : normal.Foreground;
        return new Attribute(fg, normal.Background, m.Text);
    }

    public Attribute Selected(Attribute normal) => new(normal.Foreground, normal.Background, TextStyle.Reverse);
}

public static class ThemeName
{
    /// <summary>The theme from <c>tui.yaml</c> or <c>HARNESS_THEME</c>; <c>NO_COLOR</c> always wins.</summary>
    public static string Resolve(string? configured) =>
        Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 } ? "none"
        : Environment.GetEnvironmentVariable("HARNESS_THEME") is { Length: > 0 } env ? env
        : configured ?? "dark";
}
