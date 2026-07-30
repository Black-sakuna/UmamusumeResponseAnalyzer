using Terminal.Gui.Input;

namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    // UiHost 的 UI 事件联合：通过 SingleReader channel 投递，渲染循环 DrainEvents 后用
    // pattern match 派发到对应状态变更。sealed record 子类是不可变载荷。
    internal abstract record UiEvent
    {
        public sealed record RegisterWorkspace(LiveDisplayWorkspace Workspace) : UiEvent;
        public sealed record RemoveWorkspace(LiveDisplayWorkspace Workspace, LiveDisplayWorkspace? Replacement) : UiEvent;
        public sealed record SetWorkspaceShortcut(
            LiveDisplayWorkspace Workspace,
            ConsoleKey Key,
            ConsoleModifiers Modifiers,
            string ShortcutText,
            KeyboardManager.HotkeyEntry Entry) : UiEvent;
        public sealed record SetPanel(LiveDisplayPanel Panel, bool SwitchToWorkspace) : UiEvent;
        public sealed record Log(LiveDisplayLogLine Line) : UiEvent;
        public sealed record Notify(LiveDisplayNotification Notification) : UiEvent;
        public sealed record SwitchWorkspace(LiveDisplayWorkspace Workspace) : UiEvent;
        public sealed record NavigateWorkspace(Command Command, TaskCompletionSource<bool> Completion) : UiEvent;
        public sealed record RunCommand(string Command) : UiEvent;
        public sealed record ShowPopup(KeyboardPopup Popup, int Generation) : UiEvent;
        public sealed record HidePopup(int Generation) : UiEvent;
        public sealed record Shutdown : UiEvent;
    }
}
