using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Harness.Tui.Views;

/// <summary>Normalises Terminal.Gui keys to the names the <see cref="State.Keymap"/> uses: <c>g</c>, <c>G</c>, <c>?</c>, <c>Space</c>, <c>Ctrl+K</c>, <c>Alt+Enter</c>, <c>Up</c>.</summary>
internal static class KeyNames
{
    public static string Of(Key key)
    {
        KeyCode code = key.KeyCode;
        KeyCode bare = code & ~(KeyCode.CtrlMask | KeyCode.AltMask | KeyCode.ShiftMask);
        // Terminals send Alt+Enter as ESC CR, which arrives as Ctrl+Alt+M (CR is Ctrl+M).
        if (key.IsAlt && (bare == KeyCode.Enter || (key.IsCtrl && bare == KeyCode.M))) return "Alt+Enter";
        if (!key.IsCtrl && !key.IsAlt && key.TryGetPrintableRune(out System.Text.Rune rune) && !System.Text.Rune.IsControl(rune))
            return rune.Value == ' ' ? "Space" : rune.ToString();
        return key.ToString().Replace("Cursor", "", StringComparison.Ordinal);
    }
}
