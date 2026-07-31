namespace UmamusumeResponseAnalyzer.TerminalGui;

public sealed class HotkeyContext
{
    readonly List<HotkeyPopupLine> lines = [];
    readonly List<UiShortcut> shortcuts = [];

    public int LineCount => lines.Count;

    public HotkeyContext AddLine(string text = "")
    {
        lines.Add(new(text));
        return this;
    }

    public HotkeyContext BindShortcut(UiShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        shortcuts.Add(shortcut);
        return this;
    }

    internal HotkeyPopup ToPopup(int scrollOffset = 0, HotkeyPopupSelection? selection = null)
        => new(lines.ToArray(), scrollOffset, Selection: selection, Shortcuts: shortcuts.ToArray());
}
