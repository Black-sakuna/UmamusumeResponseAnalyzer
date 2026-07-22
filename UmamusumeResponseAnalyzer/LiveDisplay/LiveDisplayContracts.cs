using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.LiveDisplay;

public interface ILiveDisplayOutput
{
    LiveDisplayWorkspace? CurrentWorkspace { get; }
    LiveDisplayWorkspace CreateWorkspace(string title, int historyCapacity = 0);
    void RemoveWorkspace(LiveDisplayWorkspace workspace);
    void CaptureWorkspaceSnapshot(LiveDisplayWorkspace workspace);
    void SwitchWorkspace(LiveDisplayWorkspace workspace);
    void BindWorkspaceHotkey(LiveDisplayWorkspace workspace, ConsoleKey key, ConsoleModifiers modifiers = 0, string? description = null);
    void SetPanel(
        LiveDisplayWorkspace workspace,
        string key,
        string title,
        LiveDisplayContent content,
        bool fullBleed = false,
        bool switchToWorkspace = true);
    void Log(string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info);
    void Log(LiveDisplayWorkspace workspace, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info);
    void Notify(string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info, TimeSpan? ttl = null, params LiveDisplayShortcut[] shortcuts);
    void Notify(LiveDisplayWorkspace workspace, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info, TimeSpan? ttl = null, params LiveDisplayShortcut[] shortcuts);
}

public sealed class LiveDisplayContent(Func<View> createView)
{
    public View CreateView()
        => createView() ?? throw new InvalidOperationException("LiveDisplayContent factory 返回了 null。");

    public static LiveDisplayContent Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(() => new TextView
        {
            Text = text,
            ReadOnly = true,
            WordWrap = false,
            CanFocus = false
        });
    }
}

public sealed record LiveDisplayShortcut(
    ConsoleKey Key,
    Func<Task> Handler,
    ConsoleModifiers Modifiers = 0);

public sealed class LiveDisplayWorkspace : IEquatable<LiveDisplayWorkspace>
{
    static readonly StringComparer TitleComparer = StringComparer.OrdinalIgnoreCase;

    LiveDisplayWorkspace(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Workspace title 不能为空。", nameof(title));

        Title = title;
    }

    public string Title { get; }

    public static LiveDisplayWorkspace Create(string title) => new(title);

    public bool Equals(LiveDisplayWorkspace? other)
        => other is not null && TitleComparer.Equals(Title, other.Title);

    public override bool Equals(object? obj) => obj is LiveDisplayWorkspace other && Equals(other);

    public override int GetHashCode() => TitleComparer.GetHashCode(Title);

    public override string ToString() => Title;

    public static bool operator ==(LiveDisplayWorkspace? left, LiveDisplayWorkspace? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(LiveDisplayWorkspace? left, LiveDisplayWorkspace? right) => !(left == right);
}

public enum LiveDisplaySeverity
{
    Trace,
    Info,
    Success,
    Warning,
    Error,
}
