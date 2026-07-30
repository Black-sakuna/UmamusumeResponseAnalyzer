namespace UmamusumeResponseAnalyzer.TerminalGui
{
    internal sealed record WorkspacePanel(
        Workspace Workspace,
        string PluginId,
        string Key,
        string Title,
        WorkspaceContent Content,
        DateTimeOffset UpdatedAt,
        bool FullBleed = false);

    internal sealed record UiLogLine(
        Workspace? Workspace,
        string PluginId,
        string Text,
        UiSeverity Severity,
        string? ExceptionDetails = null);

    internal sealed record UiNotification(
        Workspace? Workspace,
        string PluginId,
        string Text,
        UiSeverity Severity,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<UiShortcut> Shortcuts,
        long ShortcutRegistrationId = 0)
    {
        internal static TimeSpan DefaultTtl(UiSeverity severity)
            => severity >= UiSeverity.Warning ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5);

        internal static DateTimeOffset ExpiresAtFromNow(UiSeverity severity, TimeSpan? ttl = null)
            => DateTimeOffset.Now.Add(ttl ?? DefaultTtl(severity));
    }

    internal sealed class PluginWorkspaceOutput(string pluginId, UiHost uiHost) : IWorkspaceOutput
    {
        readonly string pluginId = NormalizeComponent(pluginId, nameof(pluginId));

        public Workspace? CurrentWorkspace => uiHost.CurrentWorkspace;

        public Workspace CreateWorkspace(string title) => uiHost.CreateWorkspace(title);

        public void RemoveWorkspace(Workspace workspace) => uiHost.RemoveWorkspace(workspace);

        public void SwitchWorkspace(Workspace workspace) => uiHost.SwitchWorkspace(workspace);

        public void BindWorkspaceHotkey(
            Workspace workspace,
            ConsoleKey key,
            ConsoleModifiers modifiers = 0,
            string? description = null)
            => uiHost.BindWorkspaceHotkey(workspace, key, modifiers, description ?? $"切换到 {workspace.Title}");

        public void SetPanel(
            Workspace workspace,
            string key,
            string title,
            WorkspaceContent content,
            bool fullBleed = false,
            bool switchToWorkspace = true)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(content);
            uiHost.SetPanel(
                new WorkspacePanel(workspace, pluginId, key, title, content, DateTimeOffset.Now, fullBleed),
                switchToWorkspace);
        }

        public void Log(string text, UiSeverity severity = UiSeverity.Info)
            => Log(RequireCurrentWorkspace(), text, severity);

        public void Log(Workspace workspace, string text, UiSeverity severity = UiSeverity.Info)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            uiHost.Log(new UiLogLine(workspace, pluginId, text, severity));
        }

        public void Notify(
            string text,
            UiSeverity severity = UiSeverity.Info,
            TimeSpan? ttl = null,
            params UiShortcut[] shortcuts)
            => Notify(RequireCurrentWorkspace(), text, severity, ttl, shortcuts);

        public void Notify(
            Workspace workspace,
            string text,
            UiSeverity severity = UiSeverity.Info,
            TimeSpan? ttl = null,
            params UiShortcut[] shortcuts)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(shortcuts);
            uiHost.Notify(new UiNotification(
                workspace,
                pluginId,
                text,
                severity,
                UiNotification.ExpiresAtFromNow(severity, ttl),
                shortcuts.ToArray()));
        }

        Workspace RequireCurrentWorkspace()
            => CurrentWorkspace ?? throw new InvalidOperationException("当前没有 workspace，无法路由 WorkspaceOutput 输出。");

        static string NormalizeComponent(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Plugin id 不能为空。", parameterName);

            return value.Trim();
        }
    }
}
