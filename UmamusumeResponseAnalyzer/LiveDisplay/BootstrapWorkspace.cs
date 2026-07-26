using System.Collections.ObjectModel;
using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;

namespace UmamusumeResponseAnalyzer.LiveDisplay;

internal sealed class BootstrapWorkspace
{
    const string HostSource = "URA";
    const int MaxLogRows = 128;

    readonly UiHost uiHost;
    readonly object gate = new();
    readonly List<(string Label, string Value)> settings = [];
    readonly Dictionary<string, BootstrapPhase> phases = new()
    {
        ["config"] = new("配置", LiveDisplaySeverity.Info, "等待"),
        ["plugin-scan"] = new("插件扫描", LiveDisplaySeverity.Info, "等待"),
        ["database"] = new("数据文件", LiveDisplaySeverity.Info, "等待"),
        ["plugin-init"] = new("插件初始化", LiveDisplaySeverity.Info, "等待"),
        ["server"] = new("HTTP server", LiveDisplaySeverity.Info, "等待"),
        ["host"] = new("宿主", LiveDisplaySeverity.Info, "等待")
    };
    readonly List<BootstrapPluginRow> plugins = [];
    readonly List<BootstrapLogRow> logs = [];

    public BootstrapWorkspace(UiHost uiHost)
    {
        this.uiHost = uiHost;
        Workspace = uiHost.CreateWorkspace("启动");
        uiHost.BindWorkspaceHotkey(Workspace, ConsoleKey.B, ConsoleModifiers.Control, "启动信息");
        RefreshLocked();
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

    public void SetPluginSummary(IReadOnlyList<BootstrapPluginRow> values)
    {
        lock (gate)
        {
            plugins.Clear();
            plugins.AddRange(values);
            RefreshLocked();
        }
    }

    public void Log(string source, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
    {
        lock (gate)
        {
            logs.Add(new(SeverityLabel(severity), source, text));
            if (logs.Count > MaxLogRows)
                logs.RemoveRange(0, logs.Count - MaxLogRows);
            RefreshLocked();
        }

        uiHost.Log(new LiveDisplayLogLine(Workspace, source, text, severity));
    }

    void RefreshLocked()
    {
        var settingsSnapshot = settings.ToArray();
        var phaseSnapshot = phases.Values.ToArray();
        var pluginSnapshot = plugins.ToArray();
        var logSnapshot = logs.ToArray();
        uiHost.SetPanel(new LiveDisplayPanel(
            Workspace,
            HostSource,
            "status",
            "启动状态",
            new LiveDisplayContent(() => new BootstrapDashboardView(
                settingsSnapshot,
                phaseSnapshot,
                pluginSnapshot,
                logSnapshot)),
            DateTimeOffset.Now,
            FullBleed: true));
    }

    internal static string SeverityLabel(LiveDisplaySeverity severity) => severity switch
    {
        LiveDisplaySeverity.Trace => "INFO",
        LiveDisplaySeverity.Info => "INFO",
        LiveDisplaySeverity.Success => "OK",
        LiveDisplaySeverity.Warning => "WARN",
        LiveDisplaySeverity.Error => "ERR",
        _ => "INFO"
    };
}

internal sealed record BootstrapPhase(
    string Label,
    LiveDisplaySeverity Severity,
    string Detail);

internal sealed record BootstrapPluginRow(
    string Name,
    string Version,
    string Status,
    string Result)
{
    public BootstrapPluginRow(PluginRuntimeStatus plugin, bool initialized, bool failed)
        : this(
            plugin.DisplayName == plugin.InternalName
                ? plugin.DisplayName
                : $"{plugin.DisplayName} ({plugin.InternalName})",
            plugin.Version?.ToString() ?? string.Empty,
            plugin.IsLoaded ? initialized ? "OK" : "INFO" : failed || !plugin.IsAvailable ? "ERR" : "INFO",
            plugin.IsLoaded
                ? initialized ? "初始化完成" : "扫描完成，等待初始化"
                : failed || !plugin.IsAvailable
                    ? initialized ? "加载或初始化失败" : "加载失败"
                    : "未加载")
    {
    }
}

internal sealed record BootstrapLogRow(
    string Status,
    string Source,
    string Text);

internal sealed class BootstrapDashboardView : View
{
    const int WideLayoutMinimumWidth = 96;

    readonly FrameView environmentFrame;
    readonly FrameView phaseFrame;
    readonly FrameView pluginFrame;
    readonly FrameView logFrame;
    bool? wideLayout;

    public BootstrapDashboardView(
        IReadOnlyList<(string Label, string Value)> settings,
        IReadOnlyList<BootstrapPhase> phases,
        IReadOnlyList<BootstrapPluginRow> plugins,
        IReadOnlyList<BootstrapLogRow> logs)
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        TabStop = TabBehavior.TabGroup;

        environmentFrame = CreateFrame(
            "运行环境",
            CreateTable(
                ["项目", "值"],
                settings.Select(x => new[] { x.Label, x.Value })));
        phaseFrame = CreateFrame(
            "初始化结果",
            CreateTable(
                ["状态", "项目", "结果"],
                phases.Select(x => new[]
                {
                    BootstrapWorkspace.SeverityLabel(x.Severity),
                    x.Label,
                    x.Detail
                })));
        pluginFrame = CreateFrame(
            "插件摘要",
            CreateTable(
                ["插件名", "版本", "状态", "结果"],
                plugins.Select(x => new[] { x.Name, x.Version, x.Status, x.Result })));

        var logList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ShowMarks = false,
            MarkMultiple = false,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars
        };
        logList.SetSource(new ObservableCollection<string>(
            logs.Select(x => $"{x.Status} [{x.Source}] {x.Text}")));
        if (logs.Count > 0)
            logList.SelectedItem = logs.Count - 1;
        logFrame = CreateFrame("最近日志", logList);

        Add(environmentFrame, phaseFrame, pluginFrame, logFrame);
        ApplyLayout(useWideLayout: false);
    }

    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        var useWideLayout = Viewport.Width >= WideLayoutMinimumWidth;
        if (wideLayout != useWideLayout)
            ApplyLayout(useWideLayout);
        base.OnSubViewLayout(args);
    }

    void ApplyLayout(bool useWideLayout)
    {
        wideLayout = useWideLayout;
        if (useWideLayout)
        {
            environmentFrame.X = 0;
            environmentFrame.Y = 0;
            environmentFrame.Width = Dim.Percent(50);
            environmentFrame.Height = Dim.Percent(36);

            phaseFrame.X = Pos.Right(environmentFrame);
            phaseFrame.Y = 0;
            phaseFrame.Width = Dim.Fill();
            phaseFrame.Height = Dim.Percent(36);

            pluginFrame.X = 0;
            pluginFrame.Y = Pos.Bottom(environmentFrame);
            pluginFrame.Width = Dim.Fill();
            pluginFrame.Height = Dim.Percent(34);
        }
        else
        {
            environmentFrame.X = 0;
            environmentFrame.Y = 0;
            environmentFrame.Width = Dim.Fill();
            environmentFrame.Height = Dim.Percent(25);

            phaseFrame.X = 0;
            phaseFrame.Y = Pos.Bottom(environmentFrame);
            phaseFrame.Width = Dim.Fill();
            phaseFrame.Height = Dim.Percent(25);

            pluginFrame.X = 0;
            pluginFrame.Y = Pos.Bottom(phaseFrame);
            pluginFrame.Width = Dim.Fill();
            pluginFrame.Height = Dim.Percent(25);
        }

        logFrame.X = 0;
        logFrame.Y = Pos.Bottom(pluginFrame);
        logFrame.Width = Dim.Fill();
        logFrame.Height = Dim.Fill();
    }

    static FrameView CreateFrame(string title, View content)
    {
        var frame = new FrameView
        {
            Title = title,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        frame.Add(content);
        return frame;
    }

    static TableView CreateTable(string[] columns, IEnumerable<string[]> rows)
    {
        var data = new DataTable();
        foreach (var column in columns)
            data.Columns.Add(column);
        foreach (var row in rows)
            data.Rows.Add(row.Cast<object>().ToArray());

        var table = new TableView(new DataTableSource(data))
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
            MultiSelect = false,
            UseAllRowsForContentCalculation = true,
            MaxCellWidth = int.MaxValue,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars
        };
        table.Style.AlwaysShowHeaders = true;
        table.Style.ShowHorizontalHeaderOverline = false;
        table.RefreshContentSize();
        return table;
    }
}
