using System.Drawing;
using System.Runtime.CompilerServices;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.TerminalGui;

internal static class WorkspaceLayoutBuilder
{
    static readonly ConditionalWeakTable<View, ViewLayoutMetadata> ViewMetadata = new();

    internal sealed class WorkspaceSurface
    {
        readonly PanelView[] panels;
        readonly bool fullBleed;
        readonly View panelContainer;

        public WorkspaceSurface(
            Workspace? workspace,
            IReadOnlyList<(WorkspacePanel Panel, View View)> panelViews,
            Func<Workspace, string> workspaceLabel)
        {
            fullBleed = panelViews.Count == 1 && panelViews[0].Panel.FullBleed;
            View = new View
            {
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                CanFocus = true,
                TabStop = TabBehavior.TabGroup
            };
            panelContainer = fullBleed ? View : new View();
            if (!fullBleed)
                View.Add(panelContainer);
            panels = panelViews
                .Select(x =>
                {
                    var metadata = ViewMetadata.GetValue(
                        x.View,
                        static view => new(
                            view.Height is DimAbsolute absolute
                                ? Math.Max(0, absolute.Size)
                                : 0,
                            view.Height is DimFill));
                    EnableFocusPath(x.View);
                    var frame = fullBleed
                        ? null
                        : new FrameView
                        {
                            Title = $"{x.Panel.PluginId} - {x.Panel.Title}",
                            X = 0,
                            Width = Dim.Fill()
                        };
                    frame?.Border.GetOrCreateView();
                    return new PanelView(
                        x.Panel,
                        x.View,
                        metadata.DeclaredHeight,
                        metadata.FillsViewportHeight,
                        frame);
                })
                .ToArray();

            if (workspace is not null && panels.Length == 0)
            {
                panelContainer.Add(new Label
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
                        panelContainer.Add(panel.View);
                    }
                    else
                    {
                        panel.Frame.Add(panel.View);
                        panelContainer.Add(panel.Frame);
                    }
                }
            }
            if (!fullBleed)
                EnableFocusPath(panelContainer);
        }

        public View View { get; }
        public int MaxScroll { get; private set; }

        public void Update(int width, int height, int scrollOffset)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var panelHeights = CalculatePanelHeights(
                panels.Select(x => (x.Panel, x.View, x.DeclaredHeight, x.FillsViewportHeight)).ToArray(),
                height,
                fullBleed,
                width);
            var contentHeight = Math.Max(height, panelHeights.Sum());
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
                    panel.View.Height = panel.FillsViewportHeight
                        ? Dim.Fill()
                        : panelHeight;
                    continue;
                }

                panel.Frame.Y = y;
                panel.Frame.Height = panelHeight;
                panel.View.Height = Math.Max(1, panelHeight - 2);
                y += panelHeight;
            }

            View.SetContentSize(new Size(width, contentHeight));
            if (!fullBleed)
            {
                panelContainer.Width = width;
                panelContainer.Height = contentHeight;
            }
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
            WorkspacePanel Panel,
            View View,
            int DeclaredHeight,
            bool FillsViewportHeight,
            FrameView? Frame);
    }

    sealed record ViewLayoutMetadata(
        int DeclaredHeight,
        bool FillsViewportHeight);

    public static WorkspaceSurface BuildWorkspaceLayout(
        Workspace? workspace,
        IReadOnlyCollection<WorkspacePanel> panels,
        Func<Workspace, string> workspaceLabel,
        int width,
        int height,
        int scrollOffset,
        Func<WorkspacePanel, View> createView)
    {
        var activePanels = SelectPanels(workspace, panels);
        var activePanelViews = activePanels
            .Select(panel => (Panel: panel, View: createView(panel)))
            .ToArray();
        var surface = new WorkspaceSurface(workspace, activePanelViews, workspaceLabel);
        surface.Update(width, height, scrollOffset);
        return surface;
    }

    static WorkspacePanel[] SelectPanels(
        Workspace? workspace,
        IReadOnlyCollection<WorkspacePanel> panels)
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
        IReadOnlyList<(
            WorkspacePanel Panel,
            View View,
            int DeclaredHeight,
            bool FillsViewportHeight)> panels,
        int bodyHeight,
        bool fullBleed,
        int width)
    {
        if (panels.Count == 0)
            return [];

        if (fullBleed)
        {
            if (panels[0].FillsViewportHeight)
                return [bodyHeight];

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
        }

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

}
