using System.Collections.ObjectModel;
using System.Data;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;

namespace UmamusumeResponseAnalyzer.TerminalGui;

internal sealed class BootstrapWorkspace : IDisposable
{
    const int MaxLogRows = 128;

    readonly UiHost uiHost;
    readonly object gate = new();
    readonly List<(string Label, string Value)> settings = [];
    readonly Dictionary<string, BootstrapPhase> phases = new()
    {
        ["config"] = new("配置", UiSeverity.Info, "等待"),
        ["plugin-scan"] = new("插件扫描", UiSeverity.Info, "等待"),
        ["database"] = new("数据文件", UiSeverity.Info, "等待"),
        ["plugin-init"] = new("插件初始化", UiSeverity.Info, "等待"),
        ["server"] = new("HTTP server", UiSeverity.Info, "等待"),
        ["host"] = new("宿主", UiSeverity.Info, "等待")
    };
    readonly List<BootstrapPluginRow> plugins = [];
    readonly List<BootstrapLogRow> logs = [];
    bool disposed;

    public BootstrapWorkspace(UiHost uiHost)
    {
        this.uiHost = uiHost;
        Workspace = global::UmamusumeResponseAnalyzer.TerminalGui.Workspace.Create("启动");
        Workspace.BindHotkey(ConsoleKey.B, ConsoleModifiers.Control, "启动信息");
        uiHost.LogAdded += OnLogAdded;
        TerminalUi.DefaultExceptionWorkspace = Workspace;
        Refresh();
    }

    public Workspace Workspace { get; }

    public void SetSettings(IReadOnlyList<(string Label, string Value)> values)
    {
        lock (gate)
        {
            settings.Clear();
            settings.AddRange(values);
        }
        Refresh();
    }

    public void SetPhase(string key, string label, UiSeverity severity, string detail)
    {
        lock (gate)
            phases[key] = new(label, severity, detail);
        Refresh();
    }

    public void SetPluginSummary(IReadOnlyList<BootstrapPluginRow> values)
    {
        lock (gate)
        {
            plugins.Clear();
            plugins.AddRange(values);
        }
        Refresh();
    }

    public void Log(string source, string text, UiSeverity severity = UiSeverity.Info)
        => Workspace.Log($"[{source}] {text}", severity);

    void OnLogAdded(UiLogLine line)
    {
        if (line.Workspace is not null && !ReferenceEquals(line.Workspace, Workspace))
            return;

        lock (gate)
        {
            logs.Add(new(
                SeverityLabel(line.Severity),
                line.Text,
                line.ExceptionDetails));
            if (logs.Count > MaxLogRows)
                logs.RemoveRange(0, logs.Count - MaxLogRows);
        }
        Refresh();
    }

    void Refresh()
    {
        Workspace.SetPanel(
            "status",
            "启动状态",
            new WorkspaceContent(() =>
            {
                (string Label, string Value)[] settingsSnapshot;
                BootstrapPhase[] phaseSnapshot;
                BootstrapPluginRow[] pluginSnapshot;
                BootstrapLogRow[] logSnapshot;
                lock (gate)
                {
                    settingsSnapshot = settings.ToArray();
                    phaseSnapshot = phases.Values.ToArray();
                    pluginSnapshot = plugins.ToArray();
                    logSnapshot = logs.ToArray();
                }

                return new BootstrapDashboardView(
                    settingsSnapshot,
                    phaseSnapshot,
                    pluginSnapshot,
                    logSnapshot);
            }),
            fullBleed: true,
            switchToWorkspace: false);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        uiHost.LogAdded -= OnLogAdded;
        if (ReferenceEquals(TerminalUi.DefaultExceptionWorkspace, Workspace))
            TerminalUi.DefaultExceptionWorkspace = null;
    }

    internal static string SeverityLabel(UiSeverity severity) => severity switch
    {
        UiSeverity.Trace => "INFO",
        UiSeverity.Info => "INFO",
        UiSeverity.Success => "OK",
        UiSeverity.Warning => "WARN",
        UiSeverity.Error => "ERR",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null)
    };
}

internal sealed record BootstrapPhase(
    string Label,
    UiSeverity Severity,
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
    string Text,
    string? ExceptionDetails = null);

internal sealed class BootstrapDashboardView : View
{
    const int WideLayoutMinimumWidth = 96;

    readonly FrameView environmentFrame;
    readonly FrameView phaseFrame;
    readonly FrameView pluginFrame;
    readonly FrameView logFrame;
    readonly ListView logList;
    readonly IReadOnlyList<BootstrapLogRow> logs;
    PopoverMenu? logContextMenu;
    string? exceptionDetailsToCopy;
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
        this.logs = logs;

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

        logList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ShowMarks = false,
            MarkMultiple = false,
            KeystrokeNavigator = null,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars
        };
        logList.Activating += LogListActivating;
        logList.CommandNotBound += LogListCommandNotBound;
        logList.MouseBindings.ReplaceCommands(
            MouseFlags.RightButtonClicked,
            Command.Activate,
            Command.Context);
        logList.SetSource(new ObservableCollection<string>(
            logs.Select(x => $"{x.Status} {x.Text}")));
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            logList.Activating -= LogListActivating;
            logList.CommandNotBound -= LogListCommandNotBound;
            if (logContextMenu is { } contextMenu)
            {
                contextMenu.App?.Popovers?.Hide(contextMenu);
                contextMenu.App?.Popovers?.DeRegister(contextMenu);
                contextMenu.Dispose();
                logContextMenu = null;
                exceptionDetailsToCopy = null;
            }
        }
        base.Dispose(disposing);
    }

    void LogListActivating(object? sender, CommandEventArgs e)
    {
        if (e.Context is
            {
                Command: Command.Activate,
                Binding: MouseBinding { MouseEvent: { } mouse }
            } &&
            (mouse.Flags & MouseFlags.RightButtonClicked) != 0)
        {
            logList.SelectedItem = null;
        }
    }

    void LogListCommandNotBound(object? sender, CommandEventArgs e)
    {
        if (e.Context is not
            {
                Command: Command.Context,
                Binding: MouseBinding { MouseEvent: { } mouse }
            })
            return;

        e.Handled = true;
        ShowLogContextMenu(mouse.ScreenPosition);
    }

    void ShowLogContextMenu(Point screenPosition)
    {
        exceptionDetailsToCopy = null;
        if (logList.SelectedItem is not { } selected ||
            selected < 0 ||
            selected >= logs.Count ||
            logs[selected].ExceptionDetails is not { } details)
        {
            if (logContextMenu is { Visible: true } visibleMenu)
                visibleMenu.App?.Popovers?.Hide(visibleMenu);
            return;
        }

        exceptionDetailsToCopy = details;
        if (logContextMenu is null)
        {
            var app = App ?? throw new InvalidOperationException(
                "Bootstrap dashboard must be attached before showing its context menu.");
            logContextMenu = new PopoverMenu(new Menu(new MenuItem[]
            {
                new("复制完整 backtrace", action: CopyExceptionDetails)
            }))
            {
                App = app
            };
            app.Popovers?.Register(logContextMenu);
        }

        logContextMenu.Target = new WeakReference<View>(logList);
        logContextMenu.MakeVisible(screenPosition);
    }

    void CopyExceptionDetails()
    {
        if (logContextMenu is { } contextMenu)
            contextMenu.Target = null;

        var details = exceptionDetailsToCopy;
        exceptionDetailsToCopy = null;
        if (details is null)
            return;

        try
        {
            if (logContextMenu?.App?.Clipboard?.TrySetClipboardData(details) == true)
                return;
        }
        catch (Exception exception)
        {
            TerminalUi.LogException("Clipboard", exception);
        }

        TerminalUi.Notify(
            "URA",
            "复制完整 backtrace 失败：系统 clipboard 不可用。",
            UiSeverity.Error);
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
            CollectionNavigator = null,
            ViewportSettings = ViewportSettingsFlags.HasScrollBars
        };
        table.Style.AlwaysShowHeaders = true;
        table.Style.ShowHorizontalHeaderOverline = false;
        table.RefreshContentSize();
        return table;
    }
}
