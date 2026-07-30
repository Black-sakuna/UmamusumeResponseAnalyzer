using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.TerminalGui;

public interface IWorkspaceOutput
{
    Workspace? CurrentWorkspace { get; }
    Workspace CreateWorkspace(string title);
    void RemoveWorkspace(Workspace workspace);
    void SwitchWorkspace(Workspace workspace);
    void BindWorkspaceHotkey(Workspace workspace, ConsoleKey key, ConsoleModifiers modifiers = 0, string? description = null);
    void SetPanel(
        Workspace workspace,
        string key,
        string title,
        WorkspaceContent content,
        bool fullBleed = false,
        bool switchToWorkspace = true);
    void Log(string text, UiSeverity severity = UiSeverity.Info);
    void Log(Workspace workspace, string text, UiSeverity severity = UiSeverity.Info);
    void Notify(string text, UiSeverity severity = UiSeverity.Info, TimeSpan? ttl = null, params UiShortcut[] shortcuts);
    void Notify(Workspace workspace, string text, UiSeverity severity = UiSeverity.Info, TimeSpan? ttl = null, params UiShortcut[] shortcuts);
}

public sealed class WorkspaceContent(Func<View> createView)
{
    public View CreateView()
        => createView() ?? throw new InvalidOperationException("WorkspaceContent factory 返回了 null。");

    public static WorkspaceContent Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(() =>
        {
            var label = new Label
            {
                Text = text,
                Width = Dim.Fill(),
                Height = Dim.Auto()
            };
            label.TextFormatter.WordWrap = true;
            return label;
        });
    }
}

public sealed record UiShortcut(
    ConsoleKey Key,
    Func<Task> Handler,
    ConsoleModifiers Modifiers = 0);

public sealed class Workspace : IEquatable<Workspace>
{
    static readonly StringComparer TitleComparer = StringComparer.OrdinalIgnoreCase;

    Workspace(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Workspace title 不能为空。", nameof(title));

        Title = title;
    }

    public string Title { get; }

    public static Workspace Create(string title) => new(title);

    public bool Equals(Workspace? other)
        => other is not null && TitleComparer.Equals(Title, other.Title);

    public override bool Equals(object? obj) => obj is Workspace other && Equals(other);

    public override int GetHashCode() => TitleComparer.GetHashCode(Title);

    public override string ToString() => Title;

    public static bool operator ==(Workspace? left, Workspace? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Workspace? left, Workspace? right) => !(left == right);
}

public enum UiSeverity
{
    Trace,
    Info,
    Success,
    Warning,
    Error,
}
