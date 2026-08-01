namespace UmamusumeResponseAnalyzer.TerminalGui;

public sealed class Workspace
{
    internal Workspace(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Workspace title 不能为空。", nameof(title));

        Title = title;
    }

    public string Title { get; }

    internal bool IsRemoved { get; set; }

    public static Workspace? Current => TerminalUi.RequireHost().GetCurrentWorkspace();

    public static Workspace Create(string title)
        => TerminalUi.RequireHost().CreateWorkspace(title);

    public void SetPanel(
        string key,
        string title,
        WorkspaceContent content,
        bool fullBleed = false,
        bool switchToWorkspace = true)
        => TerminalUi.RequireHost().SetPanel(
            this,
            key,
            title,
            content,
            fullBleed,
            switchToWorkspace);

    public bool RemovePanel(string key)
        => TerminalUi.RequireHost().RemovePanel(this, key);

    public void Log(string text, UiSeverity severity = UiSeverity.Info)
        => TerminalUi.RequireHost().Log(this, text, severity);

    public void Notify(
        string text,
        UiSeverity severity = UiSeverity.Info,
        TimeSpan? ttl = null,
        params UiShortcut[] shortcuts)
        => TerminalUi.RequireHost().Notify(this, text, severity, ttl, shortcuts);

    public void SwitchTo()
        => TerminalUi.RequireHost().SwitchWorkspace(this);

    public void BindHotkey(
        ConsoleKey key,
        ConsoleModifiers modifiers = 0,
        string? description = null)
        => TerminalUi.RequireHost().BindWorkspaceHotkey(this, key, modifiers, description);

    public void Remove()
        => TerminalUi.RequireHost().RemoveWorkspace(this);
}
