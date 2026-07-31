using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.TerminalGui;

public sealed class WorkspaceContent
{
    readonly Func<View> createView;

    public WorkspaceContent(Func<View> createView)
    {
        ArgumentNullException.ThrowIfNull(createView);
        this.createView = createView;
    }

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

public enum UiSeverity
{
    Trace,
    Info,
    Success,
    Warning,
    Error
}

internal sealed record WorkspacePanel(
    Workspace Workspace,
    string Key,
    string Title,
    WorkspaceContent Content,
    long Sequence,
    bool FullBleed = false);

internal sealed record UiLogLine(
    Workspace? Workspace,
    string Text,
    UiSeverity Severity,
    string? ExceptionDetails = null);

internal sealed record UiNotification(
    Workspace? Workspace,
    string Text,
    UiSeverity Severity,
    DateTimeOffset ExpiresAt)
{
    internal static TimeSpan DefaultTtl(UiSeverity severity)
        => severity >= UiSeverity.Warning ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5);

    internal static DateTimeOffset ExpiresAtFromNow(UiSeverity severity, TimeSpan? ttl = null)
        => DateTimeOffset.Now.Add(ttl ?? DefaultTtl(severity));
}
