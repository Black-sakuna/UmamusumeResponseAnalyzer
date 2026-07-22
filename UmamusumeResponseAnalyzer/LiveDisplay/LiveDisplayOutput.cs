using Spectre.Console;
using Spectre.Console.Rendering;

namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    internal sealed record LiveDisplayPanel(
        LiveDisplayWorkspace Workspace,
        string PluginId,
        string Key,
        string Title,
        IRenderable Content,
        DateTimeOffset UpdatedAt,
        bool FullBleed = false);

    internal sealed record LiveDisplayLogLine(
        LiveDisplayWorkspace? Workspace,
        string PluginId,
        string Text,
        LiveDisplaySeverity Severity,
        bool IsMarkup);

    internal sealed record LiveDisplayNotification(
        LiveDisplayWorkspace? Workspace,
        string PluginId,
        string Text,
        LiveDisplaySeverity Severity,
        DateTimeOffset ExpiresAt)
    {
        internal static TimeSpan DefaultTtl(LiveDisplaySeverity severity)
        {
            return severity >= LiveDisplaySeverity.Warning
                ? TimeSpan.FromSeconds(10)
                : TimeSpan.FromSeconds(5);
        }

        internal static DateTimeOffset ExpiresAtFromNow(LiveDisplaySeverity severity, TimeSpan? ttl = null)
        {
            return DateTimeOffset.Now.Add(ttl ?? DefaultTtl(severity));
        }
    }

    internal sealed class PluginLiveDisplayOutput : ILiveDisplayOutput
    {
        readonly string pluginId;
        readonly UiHost uiHost;

        public PluginLiveDisplayOutput(string pluginId, UiHost uiHost)
        {
            this.pluginId = NormalizeComponent(pluginId, nameof(pluginId));
            this.uiHost = uiHost;
        }

        public LiveDisplayWorkspace? CurrentWorkspace => uiHost.CurrentWorkspace;

        public LiveDisplayWorkspace CreateWorkspace(string title)
        {
            return uiHost.CreateWorkspace(title);
        }

        public void SwitchWorkspace(LiveDisplayWorkspace workspace)
        {
            uiHost.SwitchWorkspace(workspace);
        }

        public void BindWorkspaceHotkey(LiveDisplayWorkspace workspace, ConsoleKey key, ConsoleModifiers modifiers = 0, string? description = null)
        {
            uiHost.BindWorkspaceHotkey(
                workspace,
                key,
                modifiers,
                description ?? $"切换到 {workspace.Title}");
        }

        public void SetPanel(LiveDisplayWorkspace workspace, string key, string title, IRenderable content, bool fullBleed = false)
            => SetPanel(workspace, key, title, content, fullBleed, switchToWorkspace: true);

        public void SetPanel(LiveDisplayWorkspace workspace, string key, string title, IRenderable content, bool fullBleed, bool switchToWorkspace)
            => uiHost.SetPanel(
                new LiveDisplayPanel(workspace, pluginId, key, title, content, DateTimeOffset.Now, fullBleed),
                switchToWorkspace);

        public void Log(LiveDisplayWorkspace workspace, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
            => uiHost.Log(new LiveDisplayLogLine(workspace, pluginId, text, severity, IsMarkup: false));

        public void MarkupLog(LiveDisplayWorkspace workspace, string markup, LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
            => uiHost.Log(new LiveDisplayLogLine(workspace, pluginId, markup, severity, IsMarkup: true));

        public void Notify(LiveDisplayWorkspace workspace, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info, TimeSpan? ttl = null)
            => uiHost.Notify(new LiveDisplayNotification(workspace, pluginId, text, severity, LiveDisplayNotification.ExpiresAtFromNow(severity, ttl)));

        static string NormalizeComponent(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Plugin id 不能为空。", parameterName);

            return value.Trim();
        }
    }
}
