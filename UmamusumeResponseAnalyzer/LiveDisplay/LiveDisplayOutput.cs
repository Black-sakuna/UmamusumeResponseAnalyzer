namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    internal sealed record LiveDisplayPanel(
        LiveDisplayWorkspace Workspace,
        string PluginId,
        string Key,
        string Title,
        LiveDisplayContent Content,
        DateTimeOffset UpdatedAt,
        bool FullBleed = false);

    internal sealed record LiveDisplayLogLine(
        LiveDisplayWorkspace? Workspace,
        string PluginId,
        string Text,
        LiveDisplaySeverity Severity);

    internal sealed record LiveDisplayNotification(
        LiveDisplayWorkspace? Workspace,
        string PluginId,
        string Text,
        LiveDisplaySeverity Severity,
        DateTimeOffset ExpiresAt,
        IReadOnlyList<LiveDisplayShortcut> Shortcuts,
        long ShortcutRegistrationId = 0)
    {
        internal static TimeSpan DefaultTtl(LiveDisplaySeverity severity)
            => severity >= LiveDisplaySeverity.Warning ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5);

        internal static DateTimeOffset ExpiresAtFromNow(LiveDisplaySeverity severity, TimeSpan? ttl = null)
            => DateTimeOffset.Now.Add(ttl ?? DefaultTtl(severity));
    }

    internal sealed class PluginLiveDisplayOutput(string pluginId, UiHost uiHost) : ILiveDisplayOutput
    {
        readonly string pluginId = NormalizeComponent(pluginId, nameof(pluginId));

        public LiveDisplayWorkspace? CurrentWorkspace => uiHost.CurrentWorkspace;

        public LiveDisplayWorkspace CreateWorkspace(string title, int historyCapacity = 0)
            => uiHost.CreateWorkspace(title, historyCapacity);

        public void RemoveWorkspace(LiveDisplayWorkspace workspace) => uiHost.RemoveWorkspace(workspace);

        public void CaptureWorkspaceSnapshot(LiveDisplayWorkspace workspace) => uiHost.CaptureWorkspaceSnapshot(workspace);

        public void SwitchWorkspace(LiveDisplayWorkspace workspace) => uiHost.SwitchWorkspace(workspace);

        public void BindWorkspaceHotkey(
            LiveDisplayWorkspace workspace,
            ConsoleKey key,
            ConsoleModifiers modifiers = 0,
            string? description = null)
            => uiHost.BindWorkspaceHotkey(workspace, key, modifiers, description ?? $"切换到 {workspace.Title}");

        public void SetPanel(
            LiveDisplayWorkspace workspace,
            string key,
            string title,
            LiveDisplayContent content,
            bool fullBleed = false,
            bool switchToWorkspace = true)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(content);
            uiHost.SetPanel(
                new LiveDisplayPanel(workspace, pluginId, key, title, content, DateTimeOffset.Now, fullBleed),
                switchToWorkspace);
        }

        public void Log(string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
            => Log(RequireCurrentWorkspace(), text, severity);

        public void Log(LiveDisplayWorkspace workspace, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            uiHost.Log(new LiveDisplayLogLine(workspace, pluginId, text, severity));
        }

        public void Notify(
            string text,
            LiveDisplaySeverity severity = LiveDisplaySeverity.Info,
            TimeSpan? ttl = null,
            params LiveDisplayShortcut[] shortcuts)
            => Notify(RequireCurrentWorkspace(), text, severity, ttl, shortcuts);

        public void Notify(
            LiveDisplayWorkspace workspace,
            string text,
            LiveDisplaySeverity severity = LiveDisplaySeverity.Info,
            TimeSpan? ttl = null,
            params LiveDisplayShortcut[] shortcuts)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(shortcuts);
            uiHost.Notify(new LiveDisplayNotification(
                workspace,
                pluginId,
                text,
                severity,
                LiveDisplayNotification.ExpiresAtFromNow(severity, ttl),
                shortcuts.ToArray()));
        }

        LiveDisplayWorkspace RequireCurrentWorkspace()
            => CurrentWorkspace ?? throw new InvalidOperationException("当前没有 workspace，无法路由 LiveDisplay 输出。");

        static string NormalizeComponent(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Plugin id 不能为空。", parameterName);

            return value.Trim();
        }
    }
}
