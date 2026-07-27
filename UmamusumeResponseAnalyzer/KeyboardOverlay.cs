using Terminal.Gui.Input;
using UmamusumeResponseAnalyzer.LiveDisplay;

namespace UmamusumeResponseAnalyzer
{
    internal interface IKeyboardOverlaySink
    {
        int PopupVisibleLineCount => 1;
        Task<bool> TryHandleWorkspaceCommandAsync(Command command);
        void ShowPopup(KeyboardPopup popup, int generation);
        void HidePopup(int generation);
    }

    internal sealed record KeyboardPopup(
        IReadOnlyList<KeyboardPopupLine> Lines,
        int ScrollOffset = 0,
        DateTimeOffset? ExpiresAt = null,
        KeyboardPopupSelection? Selection = null,
        IReadOnlyList<LiveDisplayShortcut>? Shortcuts = null);

    internal sealed record KeyboardPopupSelection(
        IReadOnlyList<int> LineIndexes,
        int SelectedIndex,
        Func<int, Task> ConfirmAsync)
    {
        public int BoundedSelectedIndex => LineIndexes.Count == 0
            ? -1
            : Math.Clamp(SelectedIndex, 0, LineIndexes.Count - 1);

        public int SelectedLineIndex => BoundedSelectedIndex < 0 ? -1 : LineIndexes[BoundedSelectedIndex];

        public KeyboardPopupSelection Normalize()
        {
            var selectedIndex = BoundedSelectedIndex;
            return selectedIndex == SelectedIndex ? this : this with { SelectedIndex = selectedIndex };
        }
    }

    internal sealed record KeyboardPopupLine(string Text, ConsoleColor Color);

    public sealed class KeyboardHandlerContext
    {
        readonly List<KeyboardPopupLine> lines = [];
        readonly List<LiveDisplayShortcut> shortcuts = [];

        public int LineCount => lines.Count;

        public KeyboardHandlerContext WriteLine(string text = "", ConsoleColor color = ConsoleColor.White)
        {
            lines.Add(new KeyboardPopupLine(text, color));
            return this;
        }

        public KeyboardHandlerContext BindShortcut(LiveDisplayShortcut shortcut)
        {
            ArgumentNullException.ThrowIfNull(shortcut);
            shortcuts.Add(shortcut);
            return this;
        }

        internal KeyboardPopup ToPopup(int scrollOffset = 0, KeyboardPopupSelection? selection = null)
        {
            return new KeyboardPopup(lines.ToArray(), scrollOffset, Selection: selection, Shortcuts: shortcuts.ToArray());
        }
    }
}
