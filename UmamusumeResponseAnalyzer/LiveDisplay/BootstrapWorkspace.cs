namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    internal sealed class BootstrapWorkspace
    {
        const string HostSource = "URA";

        readonly UiHost uiHost;
        readonly object gate = new();
        readonly List<(string Label, string Value)> settings = [];
        readonly Dictionary<string, Phase> phases = [];

        public BootstrapWorkspace(UiHost uiHost)
        {
            this.uiHost = uiHost;
            Workspace = uiHost.CreateWorkspace("启动");
            uiHost.BindWorkspaceHotkey(Workspace, ConsoleKey.B, ConsoleModifiers.Control, "启动信息");
            Refresh();
            uiHost.RemoveWorkspaceWhenAnotherPanelActivates(Workspace, () =>
            {
                if (LiveDisplayConsole.DefaultLogWorkspace == Workspace)
                    LiveDisplayConsole.DefaultLogWorkspace = null;
            });
        }

        public LiveDisplayWorkspace Workspace { get; }

        public void SetSettings(IReadOnlyList<(string Label, string Value)> values)
        {
            lock (gate)
            {
                settings.Clear();
                settings.AddRange(values);
                RefreshLocked();
            }
        }

        public void SetPhase(string key, string label, LiveDisplaySeverity severity, string detail)
        {
            lock (gate)
            {
                phases[key] = new(label, severity, detail);
                RefreshLocked();
            }
        }

        public void Log(string source, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
        {
            uiHost.Log(new LiveDisplayLogLine(Workspace, source, text, severity));
        }

        void Refresh()
        {
            lock (gate)
                RefreshLocked();
        }

        void RefreshLocked()
        {
            uiHost.SetPanel(new LiveDisplayPanel(
                Workspace,
                HostSource,
                "status",
                "启动状态",
                BuildContent(),
                DateTimeOffset.Now));
        }

        LiveDisplayContent BuildContent()
        {
            var rows = new List<string> { "设置" };

            if (settings.Count == 0)
            {
                rows.Add("尚未读取配置。");
            }
            else
            {
                foreach (var (label, value) in settings)
                    rows.Add($"{label} {value}");
            }

            rows.Add(string.Empty);
            rows.Add("启动阶段");

            if (phases.Count == 0)
            {
                rows.Add("等待启动。");
            }
            else
            {
                foreach (var phase in phases.Values)
                    rows.Add($"{SeverityLabel(phase.Severity)} {phase.Label} {phase.Detail}");
            }

            return LiveDisplayContent.Text(string.Join(Environment.NewLine, rows));
        }

        static string SeverityLabel(LiveDisplaySeverity severity) => severity switch
        {
            LiveDisplaySeverity.Trace => "TRACE",
            LiveDisplaySeverity.Info => "INFO",
            LiveDisplaySeverity.Success => "OK",
            LiveDisplaySeverity.Warning => "WARN",
            LiveDisplaySeverity.Error => "ERR",
            _ => "INFO"
        };

        sealed record Phase(string Label, LiveDisplaySeverity Severity, string Detail);
    }
}
