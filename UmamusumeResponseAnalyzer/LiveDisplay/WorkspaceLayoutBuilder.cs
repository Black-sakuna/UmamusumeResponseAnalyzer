using System.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.LiveDisplay;

internal static class WorkspaceLayoutBuilder
{
    internal sealed record Layout(View View, int MaxScroll);

    public static Layout BuildWorkspaceLayout(
        LiveDisplayWorkspace? workspace,
        IReadOnlyCollection<LiveDisplayPanel> panels,
        IReadOnlyList<LiveDisplayLogLine> logs,
        Func<LiveDisplayWorkspace, string> workspaceLabel,
        int width,
        int height,
        int scrollOffset)
    {
        var viewport = new View
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false
        };
        var activePanels = SelectPanels(workspace, panels);
        var activePanelViews = activePanels
            .Select(panel => (Panel: panel, View: panel.Content.CreateView()))
            .ToArray();
        var visibleLogs = logs.TakeLast(14).ToArray();
        var logHeight = visibleLogs.Length == 0 ? 0 : Math.Min(visibleLogs.Length, Math.Max(1, height / 3));
        var bodyHeight = Math.Max(1, height - logHeight);
        var contentHeight = Math.Max(height, EstimateContentHeight(activePanelViews, bodyHeight) + logHeight);
        var maxScroll = Math.Max(0, contentHeight - height);
        scrollOffset = Math.Clamp(scrollOffset, 0, maxScroll);
        viewport.SetContentSize(new Size(Math.Max(1, width), contentHeight));
        viewport.Viewport = new Rectangle(0, scrollOffset, Math.Max(1, width), Math.Max(1, height));

        if (workspace is null)
        {
            viewport.Add(new Label
            {
                Text = "等待插件创建 workspace…",
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = 1
            });
        }
        else if (activePanels.Length == 0)
        {
            viewport.Add(new Label
            {
                Text = $"{workspaceLabel(workspace)} 还没有插件输出。",
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = 1
            });
        }
        else
        {
            AddPanels(viewport, activePanelViews, bodyHeight);
        }

        if (visibleLogs.Length > 0)
        {
            viewport.Add(new TextView
            {
                Text = string.Join(Environment.NewLine, visibleLogs.Select(FormatLog)),
                ReadOnly = true,
                WordWrap = false,
                CanFocus = false,
                X = 0,
                Y = contentHeight - logHeight,
                Width = Dim.Fill(),
                Height = logHeight
            });
        }

        return new(viewport, maxScroll);
    }

    internal static string BuildTextSnapshot(
        LiveDisplayWorkspace? workspace,
        IReadOnlyCollection<LiveDisplayPanel> panels,
        IReadOnlyList<LiveDisplayLogLine> logs,
        Func<LiveDisplayWorkspace, string> workspaceLabel)
    {
        var lines = new List<string>();
        if (workspace is null)
        {
            lines.Add("等待插件创建 workspace…");
        }
        else
        {
            lines.Add($"Workspace: {workspaceLabel(workspace)}");
            var activePanels = SelectPanels(workspace, panels);
            if (activePanels.Length == 0)
                lines.Add("当前 workspace 还没有插件输出。");
            foreach (var panel in activePanels)
            {
                lines.Add($"[{panel.PluginId} - {panel.Title}]");
                using var view = panel.Content.CreateView();
                lines.Add(view is TextView textView ? textView.Text : view.Title);
            }
        }

        lines.AddRange(logs.TakeLast(14).Select(FormatLog));
        return string.Join(Environment.NewLine, lines);
    }

    static LiveDisplayPanel[] SelectPanels(
        LiveDisplayWorkspace? workspace,
        IReadOnlyCollection<LiveDisplayPanel> panels)
    {
        if (workspace is null)
            return [];

        var workspacePanels = panels
            .Where(x => ReferenceEquals(x.Workspace, workspace))
            .OrderBy(x => x.PluginId, StringComparer.Ordinal)
            .ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        var fullBleed = workspacePanels
            .Where(x => x.FullBleed)
            .MaxBy(x => x.UpdatedAt);
        return fullBleed is null ? workspacePanels : [fullBleed];
    }

    static void AddPanels(
        View viewport,
        IReadOnlyList<(LiveDisplayPanel Panel, View View)> panels,
        int bodyHeight)
    {
        var panelHeight = Math.Max(3, bodyHeight / panels.Count);
        for (var i = 0; i < panels.Count; i++)
        {
            var (panel, view) = panels[i];
            view.X = 0;
            view.Y = 0;
            view.Width = Dim.Fill();
            view.Height = Dim.Fill();
            if (panel.FullBleed && panels.Count == 1)
            {
                view.Height = bodyHeight;
                viewport.Add(view);
                continue;
            }

            var frame = new FrameView
            {
                Title = $"{panel.PluginId} - {panel.Title}",
                X = 0,
                Y = i * panelHeight,
                Width = Dim.Fill(),
                Height = i == panels.Count - 1 ? Math.Max(3, bodyHeight - i * panelHeight) : panelHeight,
                CanFocus = false
            };
            frame.Add(view);
            viewport.Add(frame);
        }
    }

    static int EstimateContentHeight(
        IReadOnlyList<(LiveDisplayPanel Panel, View View)> panels,
        int bodyHeight)
    {
        if (panels.Count == 0)
            return bodyHeight;

        var textHeight = 0;
        foreach (var (_, view) in panels)
        {
            textHeight += view is TextView textView
                ? Math.Max(3, textView.Text.Count(x => x == '\n') + 3)
                : Math.Max(3, bodyHeight / panels.Count);
        }
        return Math.Max(bodyHeight, textHeight);
    }

    static string FormatLog(LiveDisplayLogLine line)
        => $"{SeverityText(line.Severity)} [{line.PluginId}] {line.Text}";

    static string SeverityText(LiveDisplaySeverity severity) => severity switch
    {
        LiveDisplaySeverity.Trace => "TRACE",
        LiveDisplaySeverity.Info => "INFO ",
        LiveDisplaySeverity.Success => "OK   ",
        LiveDisplaySeverity.Warning => "WARN ",
        LiveDisplaySeverity.Error => "ERR  ",
        _ => "INFO "
    };
}
