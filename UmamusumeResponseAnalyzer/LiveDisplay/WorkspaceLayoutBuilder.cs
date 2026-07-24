using System.Drawing;
using System.Runtime.CompilerServices;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.LiveDisplay;

internal static class WorkspaceLayoutBuilder
{
    static readonly ConditionalWeakTable<View, ViewLayoutMetadata> ViewMetadata = new();

    internal sealed class WorkspaceSurface
    {
        readonly LiveDisplayWorkspace? workspace;
        readonly PanelView[] panels;
        readonly bool fullBleed;
        readonly Label logLabel;

        public WorkspaceSurface(
            LiveDisplayWorkspace? workspace,
            IReadOnlyList<(LiveDisplayPanel Panel, View View)> panelViews,
            Func<LiveDisplayWorkspace, string> workspaceLabel)
        {
            this.workspace = workspace;
            fullBleed = panelViews.Count == 1 && panelViews[0].Panel.FullBleed;
            View = new View
            {
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                CanFocus = true,
                TabStop = TabBehavior.TabGroup
            };
            panels = panelViews
                .Select(x =>
                {
                    var declaredHeight = ViewMetadata.GetValue(
                        x.View,
                        static view => new(
                            view.Height is DimAbsolute absolute
                                ? Math.Max(0, absolute.Size)
                                : 0)).DeclaredHeight;
                    EnableFocusPath(x.View);
                    return new PanelView(
                        x.Panel,
                        x.View,
                        declaredHeight,
                        fullBleed
                            ? null
                            : new FrameView
                            {
                                Title = $"{x.Panel.PluginId} - {x.Panel.Title}",
                                X = 0,
                                Width = Dim.Fill()
                            });
                })
                .ToArray();

            if (workspace is not null && panels.Length == 0)
            {
                View.Add(new Label
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
                foreach (var panel in panels)
                {
                    if (panel.Frame is null)
                    {
                        View.Add(panel.View);
                    }
                    else
                    {
                        panel.Frame.Add(panel.View);
                        View.Add(panel.Frame);
                    }
                }
            }

            logLabel = new Label
            {
                X = 0,
                Width = Dim.Fill(),
                Visible = false
            };
            View.Add(logLabel);
        }

        public View View { get; }
        public int MaxScroll { get; private set; }

        public void Update(
            IReadOnlyList<LiveDisplayLogLine> logs,
            int width,
            int height,
            int scrollOffset)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var visibleLogLines = GetVisibleLogLines(workspace, fullBleed, logs, width, height);
            var logHeight = visibleLogLines.Length;
            var bodyHeight = Math.Max(1, height - logHeight);
            var panelHeights = CalculatePanelHeights(
                panels.Select(x => (x.Panel, x.View, x.DeclaredHeight)).ToArray(),
                bodyHeight,
                fullBleed,
                width);
            var contentHeight = Math.Max(height, panelHeights.Sum() + logHeight);
            MaxScroll = Math.Max(0, contentHeight - height);
            scrollOffset = Math.Clamp(scrollOffset, 0, MaxScroll);

            var y = 0;
            for (var i = 0; i < panels.Length; i++)
            {
                var panel = panels[i];
                var panelHeight = panelHeights[i];
                panel.View.X = 0;
                panel.View.Y = 0;
                panel.View.Width = Dim.Fill();
                if (panel.Frame is null)
                {
                    panel.View.Height = panelHeight;
                    continue;
                }

                panel.Frame.Y = y;
                panel.Frame.Height = panelHeight;
                panel.View.Height = Math.Max(1, panelHeight - 2);
                y += panelHeight;
            }

            logLabel.Text = string.Join(Environment.NewLine, visibleLogLines);
            logLabel.Y = contentHeight - logHeight;
            logLabel.Height = logHeight;
            logLabel.Visible = logHeight > 0;
            View.SetContentSize(new Size(width, contentHeight));
            View.Viewport = new Rectangle(0, MaxScroll - scrollOffset, width, height);
            View.SetNeedsLayout();
            View.SetNeedsDraw();
        }

        static bool EnableFocusPath(View view)
        {
            var hasFocusableView = view.CanFocus;
            foreach (var child in view.SubViews)
                hasFocusableView |= EnableFocusPath(child);
            if (hasFocusableView && view.SubViews.Count > 0)
            {
                view.CanFocus = true;
                view.TabStop = TabBehavior.TabGroup;
            }
            return hasFocusableView;
        }

        sealed record PanelView(
            LiveDisplayPanel Panel,
            View View,
            int DeclaredHeight,
            FrameView? Frame);
    }

    sealed record ViewLayoutMetadata(int DeclaredHeight);

    public static WorkspaceSurface BuildWorkspaceLayout(
        LiveDisplayWorkspace? workspace,
        IReadOnlyCollection<LiveDisplayPanel> panels,
        IReadOnlyList<LiveDisplayLogLine> logs,
        Func<LiveDisplayWorkspace, string> workspaceLabel,
        int width,
        int height,
        int scrollOffset,
        Func<LiveDisplayPanel, View> createView)
    {
        var activePanels = SelectPanels(workspace, panels);
        var activePanelViews = activePanels
            .Select(panel => (Panel: panel, View: createView(panel)))
            .ToArray();
        var surface = new WorkspaceSurface(workspace, activePanelViews, workspaceLabel);
        surface.Update(logs, width, height, scrollOffset);
        return surface;
    }

    static string[] GetVisibleLogLines(
        LiveDisplayWorkspace? workspace,
        bool fullBleed,
        IReadOnlyList<LiveDisplayLogLine> logs,
        int width,
        int height)
    {
        if (fullBleed)
            return [];

        var logEntryLimit = workspace is null ? 18 : 14;
        var logLines = logs.TakeLast(logEntryLimit)
            .SelectMany(line => WrapLines(FormatLog(line), Math.Max(1, width)))
            .ToArray();
        var maxLogHeight = workspace is null ? height : Math.Max(1, height / 3);
        return logLines.TakeLast(maxLogHeight).ToArray();
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

    static int[] CalculatePanelHeights(
        IReadOnlyList<(LiveDisplayPanel Panel, View View, int DeclaredHeight)> panels,
        int bodyHeight,
        bool fullBleed,
        int width)
    {
        if (panels.Count == 0)
            return [];

        if (fullBleed)
            return
            [
                Math.Max(
                    bodyHeight,
                    PreferredViewHeight(
                        panels[0].View,
                        panels[0].DeclaredHeight,
                        bodyHeight,
                        width))
            ];

        var result = panels
            .Select(x => Math.Max(
                3,
                PreferredViewHeight(
                    x.View,
                    x.DeclaredHeight,
                    Math.Max(1, bodyHeight / panels.Count - 2),
                    Math.Max(1, width - 2)) + 2))
            .ToArray();
        var remaining = bodyHeight - result.Sum();
        if (remaining > 0)
            result[^1] += remaining;
        return result;
    }

    static int PreferredViewHeight(
        View view,
        int declaredHeight,
        int fallback,
        int width)
    {
        var text = view.Text?.ToString();
        var textHeight = string.IsNullOrEmpty(text) ? 0 : WrapLines(text, width).Count();
        var subViewHeight = view.SubViews.Count == 0 ? 0 : view.GetHeightRequiredForSubViews();
        return Math.Max(
            fallback,
            Math.Max(declaredHeight, Math.Max(textHeight, subViewHeight)));
    }

    static IEnumerable<string> WrapLines(string text, int width)
    {
        width = Math.Max(1, width);
        foreach (var logicalLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (logicalLine.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var line = new System.Text.StringBuilder();
            var used = 0;
            foreach (var rune in logicalLine.EnumerateRunes())
            {
                var columns = Math.Max(0, rune.GetColumns());
                if (line.Length > 0 && used + columns > width)
                {
                    yield return line.ToString();
                    line.Clear();
                    used = 0;
                }
                line.Append(rune);
                used += columns;
            }
            yield return line.ToString();
        }
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
