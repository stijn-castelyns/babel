using Terminal.Gui.Views;

namespace Harness.Tui.Views;

/// <summary>
/// The message editor: a multi-line <see cref="TextView"/> with word wrap. Enter, Alt+Enter, Up and Ctrl+G are routed
/// through the keymap before the widget sees them; large pastes are turned into attachment chips here.
/// </summary>
// TextView is marked obsolete in favour of the separate tui-cs Editor package, which has no stable release yet.
#pragma warning disable CS0618
internal sealed class ComposerView : TextView
#pragma warning restore CS0618
{
    public ComposerView()
    {
        Multiline = true;
        WordWrap = true;
        TabKeyAddsTab = false;
        EnterKeyAddsLine = false;
    }

    /// <summary>Returns the chip to insert instead of a large paste, or null to paste as is.</summary>
    public Func<string, string?>? PasteFilter { get; set; }

    public string Value => Text.Replace("\r\n", "\n");

    /// <summary>Offset of the cursor in <see cref="Value"/>.</summary>
    public int CursorIndex
    {
        get
        {
            string[] lines = Value.Split('\n');
            int row = Math.Clamp(CurrentRow, 0, lines.Length - 1), index = 0;
            for (int r = 0; r < row; r++) index += lines[r].Length + 1;
            return index + Math.Clamp(CurrentColumn, 0, lines[row].Length);
        }
    }

    public void SetValue(string text)
    {
        Text = text;
        MoveEnd();
    }

    public void NewLine() => InsertText("\n");

    /// <summary>Replaces the <paramref name="length"/> characters before the cursor with <paramref name="text"/>.</summary>
    public void ReplaceBeforeCursor(int length, string text)
    {
        for (int i = 0; i < length; i++) DeleteCharLeft();
        InsertText(text);
    }

    protected override bool OnPaste(string text)
    {
        if (PasteFilter?.Invoke(text.Replace("\r\n", "\n")) is { } chip)
        {
            InsertText(chip);
            return true;
        }
        return base.OnPaste(text);
    }
}
