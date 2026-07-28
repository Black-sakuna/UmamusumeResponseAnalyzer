using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.LiveDisplay;
using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("KeyboardManager")]
public sealed class LiveDisplayRenderTests : IDisposable
{
    static readonly string[] BootstrapFrameTitles =
        ["运行环境", "初始化结果", "插件摘要", "最近日志"];

    readonly TerminalGuiTestApp terminal;
    readonly List<string> workspaceTaskbarOrder = [];
    UiHost host;
    Task? run;
    ShutdownCommandTarget? shutdownTarget;
    int workspaceTaskbarSaveCount;

    public LiveDisplayRenderTests()
    {
        terminal = new(width: 80, height: 18);
        host = CreateHost();
        BindHost(host);
    }

    public void Dispose()
    {
        try
        {
            if (run is { IsCompleted: false })
            {
                host.RequestShutdown();
                if (!run.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("UiHost test session did not stop.");
            }
        }
        finally
        {
            try
            {
                terminal.RunOnOwnerThread(() =>
                {
                    RemoveShutdownBinding();
                    LiveDisplayConsole.Unbind(host);
                    ResetUi();
                });
            }
            finally
            {
                terminal.Dispose();
            }
        }
    }

    [Fact]
    public void LiveDisplayContent_CreatesFreshViewsAndRejectsNull()
    {
        var created = 0;
        var content = new LiveDisplayContent(() =>
        {
            created++;
            return new Label { Text = $"view-{created}" };
        });

        using var first = content.CreateView();
        using var second = content.CreateView();

        Assert.IsType<Label>(first);
        Assert.IsType<Label>(second);
        Assert.NotSame(first, second);
        Assert.Equal(2, created);
        Assert.Throws<InvalidOperationException>(
            new LiveDisplayContent(() => null!).CreateView);
    }

    [Fact]
    public void WorkspaceIdentity_IsGlobalCaseInsensitiveAndPreservesTitle()
    {
        var first = host.CreateWorkspace(" Telemetry ");
        var same = host.CreateWorkspace(" telemetry ");

        Assert.Same(first, same);
        Assert.Equal(" Telemetry ", first.Title);
        Assert.Throws<ArgumentException>(() => host.CreateWorkspace("   "));
    }

    [Fact]
    public async Task BackgroundWorkspaceCreation_ReleasesIdentityGateBeforeTimedEventsDrain()
    {
        await StartAsync();

        const string title = "Concurrent plugin workspace";
        LiveDisplayWorkspace? competingWorkspace = null;
        var armed = 0;
        EventHandler<Terminal.Gui.App.TimeoutEventArgs> added = (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
                return;

            var competing = Task.Factory.StartNew(
                () => host.CreateWorkspace(title.ToUpperInvariant()),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!competing.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("workspaceIdentityGate was held while scheduling the UI drain.");
            competingWorkspace = competing.GetAwaiter().GetResult();
        };
        var timedEvents = terminal.Application.TimedEvents
            ?? throw new InvalidOperationException("Terminal.Gui timed events are unavailable.");
        timedEvents.Added += added;
        LiveDisplayWorkspace workspace;
        try
        {
            Volatile.Write(ref armed, 1);
            workspace = await Task.Factory.StartNew(
                    () => host.CreateWorkspace(title),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            timedEvents.Added -= added;
        }

        Assert.Same(workspace, competingWorkspace);
        Assert.Same(workspace, host.CurrentWorkspace);

        host.ForPlugin("ConcurrentCreate").SetPanel(
            workspace,
            "main",
            "main",
            LiveDisplayContent.Text("Concurrent workspace ready"));
        await terminal.WaitForScreenAsync("Concurrent workspace ready");
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                parts.Popup.SubViews
                    .OfType<Shortcut>()
                    .Count(item => item.Title.Equals(title, StringComparison.OrdinalIgnoreCase)) == 1));

        Assert.False(await terminal.InvokeAsync(() => parts.Popup.Visible));
    }

    [Fact]
    public async Task TombstonedWorkspace_DelayedRegistrationCannotResurrectCanonicalIdentity()
    {
        await StartAsync();

        const string title = "Delayed tombstone workspace";
        LiveDisplayWorkspace? tombstonedWorkspace = null;
        var armed = 0;
        EventHandler<Terminal.Gui.App.TimeoutEventArgs> added = (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
                return;

            tombstonedWorkspace = host.CreateWorkspace(title.ToUpperInvariant());
            host.RemoveWorkspace(tombstonedWorkspace);
        };
        var timedEvents = terminal.Application.TimedEvents
            ?? throw new InvalidOperationException("Terminal.Gui timed events are unavailable.");
        timedEvents.Added += added;
        LiveDisplayWorkspace first;
        try
        {
            Volatile.Write(ref armed, 1);
            first = await Task.Factory.StartNew(
                    () => host.CreateWorkspace(title),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            timedEvents.Added -= added;
        }

        Assert.Same(first, tombstonedWorkspace);
        var replacement = host.CreateWorkspace(title);
        Assert.NotSame(first, replacement);
        Assert.Same(replacement, host.CurrentWorkspace);

        host.ForPlugin("DelayedTombstone").SetPanel(
            replacement,
            "main",
            "main",
            LiveDisplayContent.Text("Replacement workspace registered"));
        await terminal.WaitForScreenAsync("Replacement workspace registered");

        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        var titles = await terminal.InvokeAsync(() =>
            parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray());
        Assert.Equal(1, titles.Count(candidate =>
            candidate.Equals(title, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task MainWindow_FillsScreenWithoutBorder()
    {
        await StartAsync();

        Assert.Equal(
            new Rectangle(0, 0, 80, 18),
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.Frame));
        Assert.Null(await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.BorderStyle));
        Assert.Equal(
            Thickness.Empty,
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.Border.Thickness));
        Assert.Equal(
            new Rectangle(0, 0, 80, 18),
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.SubViews.First().Frame));
        Assert.DoesNotContain("UmamusumeResponseAnalyzer", await terminal.CaptureScreenAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShortPanels_FitWithoutBorderOrViewportFalseScroll(int panelCount)
    {
        var output = host.ForPlugin("Short");
        var workspace = output.CreateWorkspace("短内容");
        for (var i = 1; i <= panelCount; i++)
            output.SetPanel(workspace, $"panel-{i}", $"Panel {i}", LiveDisplayContent.Text($"Body {i}"));

        await StartAsync();
        await terminal.WaitForScreenAsync($"Body {panelCount}");
        var screen = await terminal.CaptureScreenAsync();
        var viewportY = await terminal.InvokeAsync(
            () => terminal.Application.TopRunnableView!.SubViews.First().SubViews.Last().Viewport.Y);

        Assert.Equal(0, viewportY);
        for (var i = 1; i <= panelCount; i++)
        {
            Assert.Contains($"Short - Panel {i}", screen);
            Assert.Contains($"Body {i}", screen);
        }
    }

    [Fact]
    public void DeclarativeFixedHeight_ContributesToPanelContentHeight()
    {
        var workspace = LiveDisplayWorkspace.Create("固定高度");
        var panels = new[]
        {
            new LiveDisplayPanel(
                workspace,
                "Plugin",
                "a",
                "Tall",
                LiveDisplayContent.Text("unused"),
                DateTimeOffset.Now),
            new LiveDisplayPanel(
                workspace,
                "Plugin",
                "b",
                "Short",
                LiveDisplayContent.Text("unused"),
                DateTimeOffset.Now)
        };
        var tallView = new View { Height = 7 };
        var shortView = new View { Height = 1 };
        var surface = WorkspaceLayoutBuilder.BuildWorkspaceLayout(
            workspace,
            panels,
            [],
            value => value.Title,
            width: 80,
            height: 8,
            scrollOffset: 0,
            panel => panel.Key == "a" ? tallView : shortView);
        using var root = surface.View;

        Assert.Equal(7, Assert.IsType<DimAbsolute>(tallView.Height).Size);
        Assert.True(surface.MaxScroll > 0);
    }

    [Fact]
    public void FullBleedFillHeight_IsOwnedByTheViewport()
    {
        var workspace = LiveDisplayWorkspace.Create("viewport-owned");
        var panel = new LiveDisplayPanel(
            workspace,
            "Host",
            "dashboard",
            "dashboard",
            LiveDisplayContent.Text("unused"),
            DateTimeOffset.Now,
            FullBleed: true);
        var dashboard = new View
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        dashboard.Add(new View
        {
            Width = Dim.Fill(),
            Height = Dim.Percent(200)
        });
        var surface = WorkspaceLayoutBuilder.BuildWorkspaceLayout(
            workspace,
            [panel],
            [],
            value => value.Title,
            width: 80,
            height: 18,
            scrollOffset: 0,
            _ => dashboard);
        using var root = surface.View;

        Assert.Equal(0, surface.MaxScroll);
        Assert.Equal(new Size(80, 18), surface.View.GetContentSize());
        Assert.Equal(new Rectangle(0, 0, 80, 18), surface.View.Viewport);
        Assert.IsType<DimFill>(dashboard.Height);
    }

    [Fact]
    public async Task FullBleed_OwnsFramebufferButKeepsOverlays()
    {
        var output = host.ForPlugin("Plugin");
        var workspace = output.CreateWorkspace("全屏");
        output.SetPanel(workspace, "normal", "普通", LiveDisplayContent.Text("NormalBody"));
        output.Log(workspace, "HiddenLog");
        output.SetPanel(
            workspace,
            "full",
            "全屏",
            LiveDisplayContent.Text("FullBody"),
            fullBleed: true);

        await StartAsync();
        await terminal.WaitForScreenAsync("FullBody");
        KeyboardManager.ShowPopup(new KeyboardHandlerContext().WriteLine("PopupOverFullBleed"));
        await terminal.WaitForScreenAsync("PopupOverFullBleed");
        var screen = await terminal.CaptureScreenAsync();

        Assert.Contains("FullBody", screen);
        Assert.Contains("PopupOverFullBleed", screen);
        Assert.DoesNotContain("NormalBody", screen);
        Assert.DoesNotContain("HiddenLog", screen);
    }

    [Fact]
    public async Task WorkspaceTaskbar_OverlaysWorkspaceAndUsesNativeShortcutInteraction()
    {
        var output = host.ForPlugin("Taskbar");
        var first = output.CreateWorkspace("First Workspace");
        var second = output.CreateWorkspace("Second Workspace");
        Button? focusTarget = null;
        output.SetPanel(
            first,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                var root = new View { Width = Dim.Fill(), Height = Dim.Fill() };
                var background = new Label
                {
                    Text = string.Join(Environment.NewLine, Enumerable.Repeat(new string('W', 80), 18)),
                    Width = Dim.Fill(),
                    Height = Dim.Fill()
                };
                focusTarget = new Button { X = 1, Y = 1, Text = "Workspace focus" };
                root.Add(background, focusTarget);
                return root;
            }),
            fullBleed: true);
        output.SetPanel(
            second,
            "main",
            "main",
            LiveDisplayContent.Text("Second workspace body"),
            fullBleed: true);
        host.SwitchWorkspace(first);

        await StartAsync();
        await terminal.WaitForScreenAsync("Workspace focus");
        await terminal.InvokeAsync(() => focusTarget!.SetFocus());
        var before = await terminal.InvokeAsync(() =>
        {
            var top = terminal.Application.TopRunnableView!;
            var taskbar = Descendants(top).OfType<WorkspaceTaskbarView>().Single();
            var parts = TaskbarParts(taskbar);
            var surface = top.SubViews.First().SubViews.Single(view =>
                !ReferenceEquals(view, parts.Trigger));
            return (
                Surface: surface,
                surface.Frame,
                surface.Viewport,
                ContentSize: surface.GetContentSize(),
                Taskbar: taskbar,
                parts.Popup,
                parts.Trigger,
                Top: top);
        });
        Assert.False(before.Popup.Visible);
        Assert.Equal(new Rectangle(0, 0, 80, 18), before.Frame);
        Assert.Equal(new Rectangle(0, 0, 80, 18), before.Viewport);
        Assert.Equal(new Rectangle(0, 0, 80, 18), before.Taskbar.Frame);

        await terminal.MoveMouseAsync(new Point(0, 17));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => before.Popup.Visible));
        await terminal.RedrawAsync();

        var after = await terminal.InvokeAsync(() => (
            before.Surface.Frame,
            before.Surface.Viewport,
            ContentSize: before.Surface.GetContentSize(),
            PopupFrame: before.Popup.FrameToScreen(),
            Items: before.Popup.SubViews.OfType<Shortcut>().ToArray(),
            Focused: before.Top.MostFocused,
            ZOrder: before.Top.SubViews.ToArray()));
        Assert.Equal(before.Frame, after.Frame);
        Assert.Equal(before.Viewport, after.Viewport);
        Assert.Equal(before.ContentSize, after.ContentSize);
        Assert.Same(focusTarget, after.Focused);
        Assert.Same(before.Taskbar, after.ZOrder[1]);
        Assert.IsType<Label>(after.ZOrder[2]);
        Assert.IsType<Label>(after.ZOrder[3]);
        Assert.IsType<CommandModeView>(after.ZOrder[4]);
        Assert.Equal(["First Workspace", "Second Workspace"], after.Items.Select(x => x.Title).ToArray());
        Assert.Equal(
            ["First Workspace", "Second Workspace"],
            after.Items.Select(x => x.CommandView!.Text).ToArray());
        Assert.All(after.Items, item =>
        {
            Assert.Equal(Key.Empty, item.Key);
            Assert.Equal(string.Empty, item.HelpText);
        });

        var bottomCells = await terminal.InvokeAsync(() =>
        {
            var contents = terminal.Application.Driver!.Contents!;
            return (
                Left: contents[17, 0].Grapheme.ToString(),
                Right: contents[17, 79].Grapheme.ToString());
        });
        Assert.Equal("W", bottomCells.Left);
        Assert.Equal("W", bottomCells.Right);
        Assert.True(after.PopupFrame.X > 0);
        Assert.True(after.PopupFrame.Right < 80);
        Assert.Equal(18, after.PopupFrame.Bottom);
        Assert.InRange(Math.Abs(after.PopupFrame.X - (80 - after.PopupFrame.Right)), 0, 1);

        var firstItem = after.Items[0];
        var secondItem = after.Items[1];
        var firstCell = await terminal.InvokeAsync(
            () => firstItem.CommandView!.ViewportToScreen(Point.Empty));
        var secondCell = await terminal.InvokeAsync(
            () => secondItem.CommandView!.ViewportToScreen(Point.Empty));
        var activeAttribute = await terminal.CaptureAttributeAsync(firstCell);
        var inactiveAttribute = await terminal.CaptureAttributeAsync(secondCell);
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan).Background,
            activeAttribute!.Value.Background);
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.RaisinBlack).Background,
            inactiveAttribute!.Value.Background);

        var firstFrame = await terminal.InvokeAsync(firstItem.FrameToScreen);
        var secondFrame = await terminal.InvokeAsync(secondItem.FrameToScreen);
        Assert.Equal(firstFrame.Right, secondFrame.X);
        Assert.Equal(after.PopupFrame.Width - 2, firstFrame.Width + secondFrame.Width);
        var firstActivations = 0;
        await terminal.InvokeAsync(() => firstItem.Activated += (_, _) => firstActivations++);
        await terminal.ClickAsync(new Point(firstFrame.X + firstFrame.Width / 2, firstFrame.Y));
        await terminal.WaitForAsync(() => firstActivations == 1);
        Assert.Same(first, host.CurrentWorkspace);
        Assert.Same(
            focusTarget,
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.MostFocused));

        await terminal.MoveMouseAsync(new Point(secondFrame.X + secondFrame.Width / 2, secondFrame.Y));
        await terminal.WaitForAsync(async () =>
            (await terminal.CaptureAttributeAsync(secondCell))?.Background ==
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkSlateGray).Background);
        Assert.Same(
            focusTarget,
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.MostFocused));

        var activations = 0;
        await terminal.InvokeAsync(() => secondItem.Activated += (_, _) => activations++);
        await terminal.ClickAsync(new Point(secondFrame.X + secondFrame.Width / 2, secondFrame.Y));
        await terminal.WaitForAsync(() => ReferenceEquals(host.CurrentWorkspace, second));
        await terminal.WaitForAsync(() => activations == 1);
        await terminal.WaitForAsync(async () =>
            (await terminal.CaptureAttributeAsync(secondCell))?.Background ==
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan).Background);
        Assert.Equal(1, activations);

        var third = output.CreateWorkspace("Third Workspace");
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => before.Popup.SubViews.OfType<Shortcut>().Count()) == 3);
        host.RemoveWorkspace(third);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => before.Popup.SubViews.OfType<Shortcut>().Count()) == 2);

        await terminal.ResizeAsync(24, 10);
        await terminal.WaitForAsync(async () =>
        {
            var frame = await terminal.InvokeAsync(before.Popup.FrameToScreen);
            return frame.X >= 0 && frame.Right <= 24 && frame.Width <= 24;
        });
        Assert.Equal(24, await terminal.InvokeAsync(() => before.Popup.Frame.Width));

        host.RemoveWorkspace(second);
        await terminal.WaitForAsync(() => ReferenceEquals(host.CurrentWorkspace, first));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => before.Popup.SubViews.OfType<Shortcut>().Count()) == 1);
        var remaining = await terminal.InvokeAsync(() => before.Popup.SubViews.OfType<Shortcut>().Single());
        var remainingCell = await terminal.InvokeAsync(
            () => remaining.CommandView!.ViewportToScreen(Point.Empty));
        await terminal.WaitForAsync(async () =>
            (await terminal.CaptureAttributeAsync(remainingCell))?.Background ==
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan).Background);

        await terminal.MoveMouseAsync(Point.Empty);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => !before.Popup.Visible));
        Assert.True(await terminal.InvokeAsync(() => before.Trigger.Visible));
    }

    [Fact]
    public async Task WorkspaceTaskbar_BottomTriggerDoesNotBlockWorkspaceControlOrStickOutsidePopup()
    {
        var output = host.ForPlugin("TaskbarClickThrough");
        var workspace = output.CreateWorkspace("Centered taskbar item");
        Button? bottomButton = null;
        var accepted = 0;
        output.SetPanel(
            workspace,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                var root = new View { Width = Dim.Fill(), Height = Dim.Fill() };
                bottomButton = new Button
                {
                    X = 0,
                    Y = Pos.AnchorEnd() + 1,
                    Text = "BottomTarget",
                    ShadowStyle = ShadowStyles.None
                };
                bottomButton.Accepting += (_, _) => accepted++;
                root.Add(bottomButton);
                return root;
            }),
            fullBleed: true);

        await StartAsync();
        await terminal.WaitForScreenAsync("BottomTarget");
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        var buttonFrame = await terminal.InvokeAsync(bottomButton!.FrameToScreen);
        var point = new Point(buttonFrame.X + buttonFrame.Width / 2, buttonFrame.Y);
        Assert.Equal(
            new Rectangle(0, 17, 80, 1),
            await terminal.InvokeAsync(parts.Trigger.FrameToScreen));

        await terminal.MoveMouseAsync(point);
        var triggerState = await terminal.InvokeAsync(() => parts.Trigger.MouseState);
        Assert.True(
            triggerState.HasFlag(MouseState.In),
            $"Trigger mouse state: {triggerState}");
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        await terminal.ClickAsync(point);
        await terminal.WaitForAsync(() => accepted == 1);
        Assert.Equal(1, accepted);
        Assert.Same(
            bottomButton,
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.MostFocused));

        await terminal.MoveMouseAsync(Point.Empty);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => !parts.Popup.Visible));
    }

    [Fact]
    public async Task WorkspaceTaskbar_PositionReportShowsAfterWorkspaceRegistration()
    {
        await StartAsync();
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        var bottom = new Point(0, 17);

        await terminal.MoveMouseAsync(bottom);
        Assert.False(await terminal.InvokeAsync(() => parts.Popup.Visible));

        var output = host.ForPlugin("LateTaskbar");
        var workspace = output.CreateWorkspace("Late workspace");
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().Count()) == 1);
        output.SetPanel(
            workspace,
            "main",
            "main",
            LiveDisplayContent.Text("Late workspace body"),
            switchToWorkspace: false);
        Assert.False(await terminal.InvokeAsync(() => parts.Popup.Visible));

        await terminal.MoveMouseAsync(bottom);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
    }

    [Fact]
    public async Task WorkspaceTaskbar_HoverTransitionsUseOneNativeFramebufferUpdate()
    {
        var output = host.ForPlugin("TaskbarHoverPerformance");
        var active = output.CreateWorkspace("主");
        var chineseTitle = "超长的workspace标题";
        var emojiTitle = "👩‍💻e\u0301 telemetry";
        var chinese = output.CreateWorkspace(chineseTitle);
        var emoji = output.CreateWorkspace(emojiTitle);
        host.SwitchWorkspace(active);

        await StartAsync();
        await terminal.ResizeAsync(30, 12);
        await terminal.MoveMouseAsync(new Point(0, 11));
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        var items = await terminal.InvokeAsync(
            () => parts.Popup.SubViews.OfType<Shortcut>().ToArray());

        var iteration = 0;
        var popupLayouts = 0;
        var popupDraws = 0;
        var itemLayouts = 0;
        var itemDraws = 0;
        var titleChanges = 0;
        var textChanges = 0;
        var frameChanges = 0;
        var layoutAndDraws = 0;
        var frameStates = new List<TaskbarFramebufferState>();
        var captureEnabled = false;
        var injectedAtIteration = 0;
        var renderedAfterIterations = 0;
        TaskCompletionSource? expectedFrame = null;
        Func<bool>? frameCondition = null;

        await terminal.InvokeAsync(() =>
        {
            terminal.Application.Iteration += (_, _) => iteration++;
            terminal.Application.LayoutAndDrawComplete += (_, _) =>
            {
                if (!captureEnabled)
                    return;

                layoutAndDraws++;
                var contents = terminal.Application.Driver!.Contents!;
                var popupFrame = parts.Popup.FrameToScreen();
                var popupCells = new TaskbarCellState[popupFrame.Width * popupFrame.Height];
                var cellIndex = 0;
                for (var y = popupFrame.Top; y < popupFrame.Bottom; y++)
                {
                    for (var x = popupFrame.Left; x < popupFrame.Right; x++)
                    {
                        var cell = contents[y, x];
                        popupCells[cellIndex++] = new(
                            cell.Grapheme.ToString(),
                            cell.Attribute);
                    }
                }

                frameStates.Add(new(
                    parts.Popup.Visible,
                    popupFrame,
                    items.Select(item => item.Title).ToArray(),
                    items.Select(item =>
                    {
                        var commandView = item.CommandView!;
                        var origin = commandView.ViewportToScreen(Point.Empty);
                        var graphemes = new List<string>();
                        for (var x = origin.X; x < origin.X + commandView.Viewport.Width;)
                        {
                            var grapheme = contents[origin.Y, x].Grapheme.ToString();
                            graphemes.Add(grapheme);
                            x += Math.Max(1, grapheme.GetColumns());
                        }

                        return string.Concat(graphemes).TrimEnd();
                    }).ToArray(),
                    items.Select(item => item.FrameToScreen()).ToArray(),
                    items.Select(item => item.MouseState).ToArray(),
                    items.Select(item =>
                    {
                        var point = item.CommandView!.ViewportToScreen(Point.Empty);
                        return contents[point.Y, point.X].Attribute;
                    }).ToArray(),
                    popupCells));
                if (frameCondition?.Invoke() == true)
                {
                    renderedAfterIterations = iteration - injectedAtIteration;
                    captureEnabled = false;
                    frameCondition = null;
                    var completion = expectedFrame;
                    expectedFrame = null;
                    completion?.TrySetResult();
                }
            };
            parts.Popup.SubViewsLaidOut += (_, _) => popupLayouts++;
            parts.Popup.DrawComplete += (_, _) => popupDraws++;
            foreach (var item in items)
            {
                item.SubViewsLaidOut += (_, _) => itemLayouts++;
                item.DrawComplete += (_, _) => itemDraws++;
                item.TitleChanged += (_, _) => titleChanges++;
                item.FrameChanged += (_, _) => frameChanges++;
                item.CommandView!.TextChanged += (_, _) => textChanges++;
            }
        });

        async Task<TaskbarTransitionMetrics> ProbeAsync(
            Point point,
            Func<bool> condition)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await terminal.InvokeAsync(() =>
            {
                popupLayouts = 0;
                popupDraws = 0;
                itemLayouts = 0;
                itemDraws = 0;
                titleChanges = 0;
                textChanges = 0;
                frameChanges = 0;
                layoutAndDraws = 0;
                frameStates.Clear();
                expectedFrame = completion;
                frameCondition = condition;
                captureEnabled = true;
            });

            await terminal.InvokeAsync(() =>
            {
                injectedAtIteration = iteration;
                terminal.Application.GetInputInjector().InjectMouse(
                    MouseAt(point, MouseFlags.PositionReport),
                    new InputInjectionOptions
                    {
                        Mode = InputInjectionMode.Pipeline,
                        AutoProcess = false,
                        TimeProvider = terminal.Time
                    });
            });
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

            return await terminal.InvokeAsync(() =>
            {
                return new TaskbarTransitionMetrics(
                    titleChanges,
                    textChanges,
                    popupLayouts,
                    itemLayouts,
                    popupDraws,
                    itemDraws,
                    frameChanges,
                    layoutAndDraws,
                    renderedAfterIterations,
                    [.. frameStates]);
            });
        }

        static void AssertPopupCells(
            TaskbarFramebufferState frame,
            params Terminal.Gui.Drawing.Attribute[] itemAttributes)
        {
            var popupAttribute =
                new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.RaisinBlack);
            Assert.Equal(
                frame.PopupFrame.Width * frame.PopupFrame.Height,
                frame.PopupCells.Length);
            for (var y = 0; y < frame.PopupFrame.Height; y++)
            {
                for (var x = 0; x < frame.PopupFrame.Width; x++)
                {
                    var cell = frame.PopupCells[y * frame.PopupFrame.Width + x];
                    if (y == 0 || y == frame.PopupFrame.Height - 1 ||
                        x == 0 || x == frame.PopupFrame.Width - 1)
                    {
                        Assert.Equal(popupAttribute, cell.Attribute);
                        var expectedBorder = y switch
                        {
                            0 when x == 0 => "┌",
                            0 when x == frame.PopupFrame.Width - 1 => "┐",
                            0 => "─",
                            _ when y == frame.PopupFrame.Height - 1 && x == 0 => "└",
                            _ when y == frame.PopupFrame.Height - 1 &&
                                   x == frame.PopupFrame.Width - 1 => "┘",
                            _ when y == frame.PopupFrame.Height - 1 => "─",
                            _ => "│"
                        };
                        Assert.Equal(expectedBorder, cell.Grapheme);
                        continue;
                    }

                    var screenX = frame.PopupFrame.X + x;
                    var itemIndex = Array.FindIndex(
                        frame.ItemFrames,
                        itemFrame => screenX >= itemFrame.Left && screenX < itemFrame.Right);
                    Assert.InRange(itemIndex, 0, itemAttributes.Length - 1);
                    Assert.Equal(itemAttributes[itemIndex], cell.Attribute);
                }
            }
        }

        var chineseFrame = await terminal.InvokeAsync(items[1].FrameToScreen);
        var chinesePoint = new Point(
            chineseFrame.X + chineseFrame.Width / 2,
            chineseFrame.Y);
        var enter = await ProbeAsync(
            chinesePoint,
            () => items[1].Title == chineseTitle);

        Assert.Equal(2, enter.TitleChanges);
        Assert.Equal(2, enter.TextChanges);
        Assert.Equal(1, enter.PopupLayouts);
        Assert.Equal(2, enter.ItemLayouts);
        Assert.Equal(1, enter.PopupDraws);
        Assert.Equal(3, enter.ItemDraws);
        Assert.Equal(3, enter.FrameChanges);
        Assert.Equal(1, enter.LayoutAndDraws);
        Assert.Equal(1, enter.Iterations);
        var enterFrame = Assert.Single(enter.Frames);
        Assert.True(enterFrame.PopupVisible);
        Assert.Equal(new Rectangle(0, 9, 30, 3), enterFrame.PopupFrame);
        Assert.Equal(["主", chineseTitle, "…"], enterFrame.Titles);
        Assert.Equal(
            ["主", chineseTitle, "…"],
            enterFrame.VisibleTexts.Select(text => text.Normalize()).ToArray());
        Assert.Equal(
            [
                new Rectangle(1, 10, 4, 1),
                new Rectangle(5, 10, 21, 1),
                new Rectangle(26, 10, 3, 1)
            ],
            enterFrame.ItemFrames);
        Assert.False(enterFrame.MouseStates[0].HasFlag(MouseState.In));
        Assert.True(enterFrame.MouseStates[1].HasFlag(MouseState.In));
        Assert.False(enterFrame.MouseStates[2].HasFlag(MouseState.In));
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan),
            enterFrame.Attributes[0]);
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkSlateGray),
            enterFrame.Attributes[1]);
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.RaisinBlack),
            enterFrame.Attributes[2]);
        AssertPopupCells(
            enterFrame,
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan),
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkSlateGray),
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.RaisinBlack));

        var emojiFrame = await terminal.InvokeAsync(items[2].FrameToScreen);
        var emojiPoint = new Point(
            emojiFrame.X + emojiFrame.Width / 2,
            emojiFrame.Y);
        var adjacent = await ProbeAsync(
            emojiPoint,
            () => items[2].Title == emojiTitle);

        Assert.Equal(2, adjacent.TitleChanges);
        Assert.Equal(2, adjacent.TextChanges);
        Assert.Equal(1, adjacent.PopupLayouts);
        Assert.Equal(2, adjacent.ItemLayouts);
        Assert.Equal(1, adjacent.PopupDraws);
        Assert.Equal(3, adjacent.ItemDraws);
        Assert.Equal(3, adjacent.FrameChanges);
        Assert.Equal(1, adjacent.LayoutAndDraws);
        Assert.Equal(1, adjacent.Iterations);
        var adjacentFrame = Assert.Single(adjacent.Frames);
        Assert.True(adjacentFrame.PopupVisible);
        Assert.Equal(new Rectangle(0, 9, 30, 3), adjacentFrame.PopupFrame);
        Assert.Equal(["主", "超长的…", emojiTitle], adjacentFrame.Titles);
        Assert.Equal(
            new[] { "主", "超长的…", emojiTitle }.Select(text => text.Normalize()).ToArray(),
            adjacentFrame.VisibleTexts.Select(text => text.Normalize()).ToArray());
        Assert.Equal(
            [
                new Rectangle(1, 10, 4, 1),
                new Rectangle(5, 10, 9, 1),
                new Rectangle(14, 10, 15, 1)
            ],
            adjacentFrame.ItemFrames);
        Assert.False(adjacentFrame.MouseStates[0].HasFlag(MouseState.In));
        Assert.False(adjacentFrame.MouseStates[1].HasFlag(MouseState.In));
        Assert.True(adjacentFrame.MouseStates[2].HasFlag(MouseState.In));
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan),
            adjacentFrame.Attributes[0]);
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.RaisinBlack),
            adjacentFrame.Attributes[1]);
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkSlateGray),
            adjacentFrame.Attributes[2]);
        AssertPopupCells(
            adjacentFrame,
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan),
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.RaisinBlack),
            new Terminal.Gui.Drawing.Attribute(StandardColor.White, StandardColor.DarkSlateGray));

        var nextIteration = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Terminal.Gui.App.EventArgs<Terminal.Gui.App.IApplication?>>?
            nextIterationHandler = null;
        await terminal.InvokeAsync(() =>
        {
            titleChanges = 0;
            textChanges = 0;
            popupLayouts = 0;
            popupDraws = 0;
            itemLayouts = 0;
            itemDraws = 0;
            frameChanges = 0;
            layoutAndDraws = 0;
            frameStates.Clear();
            captureEnabled = true;
            nextIterationHandler = (_, _) =>
            {
                terminal.Application.Iteration -= nextIterationHandler;
                nextIteration.TrySetResult();
            };
            terminal.Application.Iteration += nextIterationHandler;
            taskbar.Refresh([active, chinese, emoji], active);
        });
        await nextIteration.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.InvokeAsync(() => captureEnabled = false);
        Assert.Equal(0, titleChanges);
        Assert.Equal(0, textChanges);
        Assert.Equal(0, popupLayouts);
        Assert.Equal(0, itemLayouts);
        Assert.Equal(0, popupDraws);
        Assert.Equal(0, itemDraws);
        Assert.Equal(0, frameChanges);
        Assert.Equal(0, layoutAndDraws);

        var leave = await ProbeAsync(
            Point.Empty,
            () => !parts.Popup.Visible);

        Assert.Equal(2, leave.TitleChanges);
        Assert.Equal(2, leave.TextChanges);
        Assert.Equal(1, leave.PopupLayouts);
        Assert.Equal(3, leave.ItemLayouts);
        Assert.Equal(0, leave.PopupDraws);
        Assert.Equal(0, leave.ItemDraws);
        Assert.Equal(3, leave.FrameChanges);
        Assert.Equal(1, leave.LayoutAndDraws);
        Assert.Equal(1, leave.Iterations);
        var leaveFrame = Assert.Single(leave.Frames);
        Assert.False(leaveFrame.PopupVisible);
        Assert.Equal(["主", "超长的wor…", "👩‍💻e\u0301 telem…"], leaveFrame.Titles);
        Assert.Equal(
            [
                new Rectangle(1, 10, 4, 1),
                new Rectangle(5, 10, 12, 1),
                new Rectangle(17, 10, 12, 1)
            ],
            leaveFrame.ItemFrames);
    }

    [Fact]
    public async Task WorkspaceTaskbar_AppliesDeferredActiveLayoutWhenMousePressEnds()
    {
        var output = host.ForPlugin("TaskbarDeferredLayout");
        var first = output.CreateWorkspace("主");
        var secondTitle = "切换后必须完整显示的workspace";
        var second = output.CreateWorkspace(secondTitle);
        var third = output.CreateWorkspace("另一个很长的workspace标题");
        host.SwitchWorkspace(first);

        await StartAsync();
        await terminal.ResizeAsync(30, 12);
        await terminal.MoveMouseAsync(new Point(0, 11));
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        var items = await terminal.InvokeAsync(
            () => parts.Popup.SubViews.OfType<Shortcut>().ToArray());
        var firstFrame = await terminal.InvokeAsync(items[0].FrameToScreen);
        var point = new Point(firstFrame.X + firstFrame.Width / 2, firstFrame.Y);
        await terminal.MoveMouseAsync(point);
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => items[0].MouseState)).HasFlag(MouseState.In));

        var action = await terminal.InvokeAsync(() => items[0].Action);
        var pressed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<Mouse>? pressedHandler = null;
        await terminal.InvokeAsync(() =>
        {
            items[0].Action = null;
            pressedHandler = (_, mouse) =>
            {
                if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
                    pressed.TrySetResult();
            };
            items[0].MouseEvent += pressedHandler;
            items[0].CommandView!.MouseEvent += pressedHandler;
            terminal.Application.GetInputInjector().InjectMouse(
                MouseAt(point, MouseFlags.LeftButtonPressed),
                new InputInjectionOptions
                {
                    Mode = InputInjectionMode.Pipeline,
                    AutoProcess = false,
                    TimeProvider = terminal.Time
                });
        });
        await pressed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.InvokeAsync(() =>
        {
            items[0].MouseEvent -= pressedHandler;
            items[0].CommandView!.MouseEvent -= pressedHandler;
        });
        await terminal.InvokeAsync(() => taskbar.Refresh([first, second, third], second));
        Assert.NotEqual(secondTitle, await terminal.InvokeAsync(() => items[1].Title));

        await terminal.InvokeAsync(() =>
            terminal.Application.GetInputInjector().InjectMouse(
                MouseAt(point, MouseFlags.LeftButtonReleased),
                new InputInjectionOptions
                {
                    Mode = InputInjectionMode.Pipeline,
                    AutoProcess = false,
                    TimeProvider = terminal.Time
                }));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => items[1].Title) == secondTitle);

        await terminal.InvokeAsync(() =>
        {
            items[0].Action = action;
            taskbar.Refresh([first, second, third], first);
        });
    }

    [Fact]
    public async Task WorkspaceTaskbar_TruncatesGraphemeTitlesAndExpandsActiveAndHovered()
    {
        var output = host.ForPlugin("TaskbarTitles");
        var active = output.CreateWorkspace("主");
        var chineseTitle = "超长的workspace标题";
        var emojiTitle = "👩‍💻e\u0301 telemetry";
        _ = output.CreateWorkspace(chineseTitle);
        _ = output.CreateWorkspace(emojiTitle);
        ListView? content = null;
        output.SetPanel(
            active,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                content = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
                content.SetSource(new ObservableCollection<string>(["zero", "one", "two"]));
                content.SelectedItem = 1;
                return content;
            }),
            fullBleed: true);
        host.SwitchWorkspace(active);

        await StartAsync();
        await terminal.WaitForScreenAsync("one");
        await terminal.InvokeAsync(() => content!.SetFocus());
        await terminal.MoveMouseAsync(new Point(0, 17));
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        var items = await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().ToArray());
        Assert.Equal(["主", chineseTitle, emojiTitle], items.Select(item => item.Title).ToArray());
        Assert.Equal(0, workspaceTaskbarSaveCount);

        await terminal.ResizeAsync(30, 12);
        await terminal.WaitForAsync(async () =>
        {
            var titles = await terminal.InvokeAsync(() => items.Select(item => item.Title).ToArray());
            return titles[0] == "主" &&
                   titles[1].EndsWith('…') &&
                   titles[2].EndsWith('…');
        });
        var shortened = await terminal.InvokeAsync(() => items.Select(item => item.Title).ToArray());
        Assert.Equal("主", shortened[0]);
        AssertValidGraphemePrefix(chineseTitle, shortened[1]);
        AssertValidGraphemePrefix(emojiTitle, shortened[2]);
        Assert.Equal(
            shortened[1].Normalize(),
            (await CaptureViewTextAsync(items[1].CommandView!)).Normalize());
        Assert.Equal(
            shortened[2].Normalize(),
            (await CaptureViewTextAsync(items[2].CommandView!)).Normalize());

        var chineseFrame = await terminal.InvokeAsync(items[1].FrameToScreen);
        await terminal.MoveMouseAsync(new Point(chineseFrame.X + chineseFrame.Width / 2, chineseFrame.Y));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => items[1].Title) == chineseTitle);
        Assert.Equal("主", await terminal.InvokeAsync(() => items[0].Title));
        Assert.EndsWith("…", await terminal.InvokeAsync(() => items[2].Title));
        Assert.Equal(
            chineseTitle.Normalize(),
            (await CaptureViewTextAsync(items[1].CommandView!)).Normalize());
        Assert.True(await terminal.InvokeAsync(() => content!.HasFocus));
        Assert.Equal(1, await terminal.InvokeAsync(() => content!.SelectedItem));

        var activeFrame = await terminal.InvokeAsync(items[0].FrameToScreen);
        await terminal.MoveMouseAsync(new Point(activeFrame.X + activeFrame.Width / 2, activeFrame.Y));
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => items[1].Title)).EndsWith('…'));
        Assert.Equal("主", await terminal.InvokeAsync(() => items[0].Title));

        await terminal.ResizeAsync(80, 18);
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => items.Select(item => item.Title).ToArray()))
            .SequenceEqual(new[] { "主", chineseTitle, emojiTitle }));
        var popupFrame = await terminal.InvokeAsync(parts.Popup.FrameToScreen);
        Assert.True(popupFrame.Left >= 0);
        Assert.True(popupFrame.Right <= 80);
        Assert.Equal(0, workspaceTaskbarSaveCount);
    }

    [Fact]
    public async Task WorkspaceTaskbar_ClickThresholdAndDragUseNativeCaptureWithoutDoubleActivation()
    {
        var output = host.ForPlugin("TaskbarDrag");
        var workspaces = new[]
        {
            output.CreateWorkspace("A"),
            output.CreateWorkspace("B"),
            output.CreateWorkspace("C"),
            output.CreateWorkspace("D")
        };
        host.SwitchWorkspace(workspaces[0]);

        await StartAsync();
        await terminal.MoveMouseAsync(new Point(0, 17));
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        var items = await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().ToArray());
        var activations = new int[items.Length];
        await terminal.InvokeAsync(() =>
        {
            for (var index = 0; index < items.Length; index++)
            {
                var captured = index;
                items[index].Activated += (_, _) => activations[captured]++;
            }
        });

        var smallMoveFrame = await terminal.InvokeAsync(items[1].FrameToScreen);
        var smallMoveStart = new Point(
            smallMoveFrame.X + smallMoveFrame.Width / 2,
            smallMoveFrame.Y);
        await terminal.InjectAsync(MouseAt(smallMoveStart, MouseFlags.LeftButtonPressed));
        await terminal.InjectAsync(MouseAt(
            smallMoveStart with { X = smallMoveStart.X + 1 },
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        await terminal.InjectAsync(MouseAt(
            smallMoveStart with { X = smallMoveStart.X + 1 },
            MouseFlags.LeftButtonReleased));
        await terminal.WaitForAsync(() => ReferenceEquals(host.CurrentWorkspace, workspaces[1]));
        await terminal.WaitForAsync(() => activations[1] == 1);
        await terminal.WaitForAsync(async () =>
            !await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed()));
        Assert.Equal(1, activations.Sum());
        Assert.Equal(0, workspaceTaskbarSaveCount);

        host.SwitchWorkspace(workspaces[0]);
        await terminal.WaitForAsync(() => ReferenceEquals(host.CurrentWorkspace, workspaces[0]));
        await terminal.RedrawAsync();
        items = await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().ToArray());
        var draggedActivations = 0;
        await terminal.InvokeAsync(() => items[3].Activated += (_, _) => draggedActivations++);
        var frozenFrames = await terminal.InvokeAsync(() =>
            items.Select(item => item.FrameToScreen()).ToArray());
        var dragStart = new Point(
            frozenFrames[3].X + frozenFrames[3].Width / 2,
            frozenFrames[3].Y);
        var dragLeft = new Point(frozenFrames[0].X, frozenFrames[0].Y);
        await terminal.MoveMouseAsync(dragStart);
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => items[3].MouseState)).HasFlag(MouseState.In));
        Assert.False(await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed()));
        frozenFrames = await terminal.InvokeAsync(() =>
            items.Select(item => item.FrameToScreen()).ToArray());
        dragStart = new Point(
            frozenFrames[3].X + frozenFrames[3].Width / 2,
            frozenFrames[3].Y);
        dragLeft = new Point(frozenFrames[0].X, frozenFrames[0].Y);
        Assert.Equal(
            items,
            await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().ToArray()));
        await terminal.InjectAsync(MouseAt(dragStart, MouseFlags.LeftButtonPressed));
        await terminal.InjectAsync(MouseAt(
            dragLeft,
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        Assert.True(await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed(items[3])));
        Assert.True(await terminal.InvokeAsync(() => parts.Popup.Visible));
        var popupFrame = await terminal.InvokeAsync(parts.Popup.FrameToScreen);
        var outsidePopup = new Point(popupFrame.Left - 1, dragStart.Y);
        await terminal.InjectAsync(MouseAt(
            outsidePopup,
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        Assert.True(await terminal.InvokeAsync(() => parts.Popup.Visible));
        Assert.True(await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed(items[3])));
        Assert.Equal(
            frozenFrames,
            await terminal.InvokeAsync(() => items.Select(item => item.FrameToScreen()).ToArray()));
        Assert.Same(workspaces[0], host.CurrentWorkspace);
        Assert.Equal(0, workspaceTaskbarSaveCount);

        await terminal.InjectAsync(MouseAt(dragLeft, MouseFlags.LeftButtonReleased));
        await terminal.WaitForAsync(() => workspaceTaskbarSaveCount == 1);
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray()))
            .SequenceEqual(new[] { "D", "A", "B", "C" }));
        Assert.False(await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed()));
        Assert.Same(workspaces[0], host.CurrentWorkspace);
        Assert.Equal(0, draggedActivations);
        Assert.Equal(["D", "A", "B", "C"], workspaceTaskbarOrder);

        items = await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().ToArray());
        var rightFrames = await terminal.InvokeAsync(() =>
            items.Select(item => item.FrameToScreen()).ToArray());
        var dragRightStart = new Point(
            rightFrames[0].X + rightFrames[0].Width / 2,
            rightFrames[0].Y);
        var dragRight = new Point(rightFrames[^1].Right - 1, rightFrames[^1].Y);
        await terminal.InjectAsync(MouseAt(dragRightStart, MouseFlags.LeftButtonPressed));
        await terminal.InjectAsync(MouseAt(
            dragRight,
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        await terminal.InjectAsync(MouseAt(dragRight, MouseFlags.LeftButtonReleased));
        await terminal.WaitForAsync(() => workspaceTaskbarSaveCount == 2);
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray()))
            .SequenceEqual(new[] { "A", "B", "C", "D" }));
        Assert.Same(workspaces[0], host.CurrentWorkspace);

        items = await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().ToArray());
        var noChangeFrame = await terminal.InvokeAsync(items[1].FrameToScreen);
        var noChangeStart = new Point(
            noChangeFrame.X + noChangeFrame.Width / 2,
            noChangeFrame.Y);
        var noChangeMove = noChangeStart with { X = noChangeStart.X + 2 };
        await terminal.InjectAsync(MouseAt(noChangeStart, MouseFlags.LeftButtonPressed));
        await terminal.InjectAsync(MouseAt(
            noChangeMove,
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        await terminal.InjectAsync(MouseAt(noChangeMove, MouseFlags.LeftButtonReleased));
        await terminal.WaitForAsync(async () =>
            !await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed()));
        Assert.Equal(2, workspaceTaskbarSaveCount);
        Assert.Same(workspaces[0], host.CurrentWorkspace);
    }

    [Fact]
    public async Task WorkspaceTaskbar_PersistentTitleOrderRestoresAbsentAndLeavesHostOrderUntouched()
    {
        workspaceTaskbarOrder.AddRange(["Third", "Missing", "First"]);
        var output = host.ForPlugin("TaskbarOrder");
        var first = output.CreateWorkspace("First");
        var second = output.CreateWorkspace("Second");
        _ = output.CreateWorkspace("Third");
        var missing = output.CreateWorkspace("Missing");
        _ = output.CreateWorkspace("Fourth");
        output.BindWorkspaceHotkey(second, ConsoleKey.F8);

        await StartAsync();
        await terminal.MoveMouseAsync(new Point(0, 17));
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray()))
            .SequenceEqual(new[] { "Third", "Missing", "First", "Second", "Fourth" }));

        var items = await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().ToArray());
        var frames = await terminal.InvokeAsync(() => items.Select(item => item.FrameToScreen()).ToArray());
        var source = new Point(frames[^1].X + frames[^1].Width / 2, frames[^1].Y);
        var target = new Point(frames[0].X, frames[0].Y);
        await terminal.InjectAsync(MouseAt(source, MouseFlags.LeftButtonPressed));
        await terminal.InjectAsync(MouseAt(
            target,
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        await terminal.InjectAsync(MouseAt(target, MouseFlags.LeftButtonReleased));
        await terminal.WaitForAsync(() => workspaceTaskbarSaveCount == 1);
        Assert.Equal(["Fourth", "Third", "Missing", "First", "Second"], workspaceTaskbarOrder);

        await terminal.StopAsync(host, run!);
        run = null;
        var secondHost = CreateHost();
        host = secondHost;
        BindHost(secondHost);
        var secondOutput = secondHost.ForPlugin("TaskbarOrderReloaded");
        var reloadedFirst = secondOutput.CreateWorkspace("First");
        var reloadedSecond = secondOutput.CreateWorkspace("Second");
        _ = secondOutput.CreateWorkspace("Third");
        _ = secondOutput.CreateWorkspace("Fourth");
        secondOutput.BindWorkspaceHotkey(reloadedSecond, ConsoleKey.F8);
        secondHost.SwitchWorkspace(reloadedFirst);

        await StartAsync();
        await terminal.MoveMouseAsync(new Point(0, 17));
        taskbar = await GetWorkspaceTaskbarAsync();
        parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray()))
            .SequenceEqual(new[] { "Fourth", "Third", "First", "Second" }));

        _ = secondOutput.CreateWorkspace(missing.Title);
        await terminal.WaitForAsync(async () =>
            (await terminal.InvokeAsync(() => parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray()))
            .SequenceEqual(new[] { "Fourth", "Third", "Missing", "First", "Second" }));
        Assert.Equal(1, workspaceTaskbarSaveCount);

        await terminal.InjectAsync(Key.F8);
        await terminal.WaitForAsync(() => ReferenceEquals(secondHost.CurrentWorkspace, reloadedSecond));
        await terminal.MoveMouseAsync(Point.Empty);
        await terminal.WaitForAsync(async () =>
            !await terminal.InvokeAsync(() => parts.Popup.Visible));
        await secondHost.HandleCommandAsync("/workspace list");
        await terminal.WaitForScreenAsync("Workspaces");
        var screen = await terminal.CaptureScreenAsync();
        var list = screen[screen.IndexOf("Workspaces", StringComparison.Ordinal)..];
        Assert.True(list.IndexOf("First", StringComparison.Ordinal) <
                    list.IndexOf("Second", StringComparison.Ordinal));
        Assert.True(list.IndexOf("Second", StringComparison.Ordinal) <
                    list.IndexOf("Third", StringComparison.Ordinal));
        Assert.True(list.IndexOf("Third", StringComparison.Ordinal) <
                    list.IndexOf("Fourth", StringComparison.Ordinal));
        Assert.True(list.IndexOf("Fourth", StringComparison.Ordinal) <
                    list.IndexOf("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Panels_AreSortedAndLatestLogsFollowTail()
    {
        var outputB = host.ForPlugin("Plugin-B");
        var outputA = host.ForPlugin("Plugin-A");
        var workspace = outputB.CreateWorkspace("普通");
        outputB.SetPanel(workspace, "b", "B", LiveDisplayContent.Text("Body-B"));
        outputA.SetPanel(workspace, "a", "A", LiveDisplayContent.Text("Body-A 中文🐎"));
        for (var i = 0; i < 19; i++)
            outputA.Log(workspace, $"log-{i:00}");
        outputA.Log(
            workspace,
            $"latest-prefix-{new string('x', 400)}{Environment.NewLine}physical-tail");

        await StartAsync();
        await terminal.WaitForScreenAsync("physical-tail");
        var bottom = await terminal.CaptureScreenAsync();

        Assert.Contains("physical-tail", bottom);
        Assert.DoesNotContain("latest-prefix", bottom);
        Assert.DoesNotContain("log-00", bottom);

        await terminal.InjectAsync(Key.Home);
        await terminal.WaitForScreenAsync("中文🐎");
        var top = await terminal.CaptureScreenAsync();
        Assert.True(
            top.IndexOf("Plugin-A - A", StringComparison.Ordinal) >= 0 &&
            top.IndexOf("Plugin-A - A", StringComparison.Ordinal) <
            top.IndexOf("Plugin-B - B", StringComparison.Ordinal));
        Assert.Contains("中文🐎", top);

        await terminal.InjectAsync(Key.End);
        await terminal.WaitForScreenAsync("physical-tail");
    }

    [Fact]
    public async Task NoWorkspace_ShowsOnlyLatest18GlobalLogEntries()
    {
        for (var i = 0; i < 20; i++)
            host.Log(new LiveDisplayLogLine(null, "Host", $"global-{i:00}", LiveDisplaySeverity.Info));

        await StartAsync();
        await terminal.ResizeAsync(80, 30);
        await terminal.WaitForScreenAsync("global-19");
        var screen = await terminal.CaptureScreenAsync();

        Assert.Contains("global-02", screen);
        Assert.Contains("global-19", screen);
        Assert.DoesNotContain("global-01", screen);
        Assert.DoesNotContain("还没有插件输出", screen);
    }

    [Fact]
    public async Task Viewport_IsBottomAnchoredAndUsesRealKeyMouseAndResizeInput()
    {
        var output = host.ForPlugin("Viewport");
        var workspace = output.CreateWorkspace("长页面");
        Label? content = null;
        output.SetPanel(
            workspace,
            "main",
            "长页面",
            new LiveDisplayContent(() =>
                content = new Label { Text = Lines(1, 50) }),
            fullBleed: true);

        await StartAsync();
        await terminal.WaitForScreenAsync("line-50");
        Assert.DoesNotContain("line-01", await terminal.CaptureScreenAsync());

        await terminal.InjectAsync(Key.Home);
        await terminal.WaitForScreenAsync("line-01");
        Assert.DoesNotContain("line-50", await terminal.CaptureScreenAsync());

        await terminal.InjectAsync(Key.End);
        await terminal.WaitForScreenAsync("line-50");

        await terminal.InjectAsync(Key.CursorUp);
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains("line-50", StringComparison.Ordinal));
        await terminal.InjectAsync(Key.CursorDown);
        await terminal.WaitForScreenAsync("line-50");

        await terminal.InjectAsync(Key.PageUp);
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains("line-50", StringComparison.Ordinal));
        await terminal.InjectAsync(Key.PageDown);
        await terminal.WaitForScreenAsync("line-50");

        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = new Point(2, 2),
            Flags = MouseFlags.WheeledUp,
            Timestamp = terminal.Time.Now
        });
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains("line-50", StringComparison.Ordinal));
        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = new Point(2, 2),
            Flags = MouseFlags.WheeledDown,
            Timestamp = terminal.Time.Now
        });
        await terminal.WaitForScreenAsync("line-50");

        await terminal.ResizeAsync(50, 10);
        await terminal.InjectAsync(Key.Home);
        await terminal.WaitForScreenAsync("line-01");
        await terminal.InjectAsync(Key.End);
        await terminal.WaitForScreenAsync("line-50");

        await terminal.ResizeAsync(100, 30);
        await terminal.WaitForScreenAsync("line-50");
        await terminal.ResizeAsync(50, 10);
        await terminal.WaitForScreenAsync("line-50");
        await terminal.ResizeAsync(100, 30);
        await terminal.WaitForScreenAsync("line-50");

        await terminal.InvokeAsync(() => content!.Text = "ShortContent");
        output.Log(workspace, "refresh retained content height");
        await terminal.WaitForScreenAsync("ShortContent");
        var shortened = await terminal.CaptureScreenAsync();
        var viewportY = await terminal.InvokeAsync(
            () => terminal.Application.TopRunnableView!.SubViews.First().SubViews.Last().Viewport.Y);

        Assert.Equal(0, viewportY);
        Assert.DoesNotContain("line-50", shortened);
    }

    [Fact]
    public async Task LeftAndRight_ReachPersistentHotkeysWhenFocusedViewDoesNotHandleThem()
    {
        var output = host.ForPlugin("Routing");
        var workspace = output.CreateWorkspace("Routing");
        output.SetPanel(workspace, "main", "Live", LiveDisplayContent.Text("LiveState"));
        var leftHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rightHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leftCalls = 0;
        var rightCalls = 0;
        KeyboardManager.Register(ConsoleKey.LeftArrow, "left", () =>
        {
            Interlocked.Increment(ref leftCalls);
            leftHandled.TrySetResult();
            return Task.CompletedTask;
        });
        KeyboardManager.Register(ConsoleKey.RightArrow, "right", () =>
        {
            Interlocked.Increment(ref rightCalls);
            rightHandled.TrySetResult();
            return Task.CompletedTask;
        });

        await StartAsync();
        await terminal.WaitForScreenAsync("LiveState");

        await terminal.InjectAsync(Key.CursorLeft);
        await leftHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.InjectAsync(Key.CursorRight);
        await rightHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal((1, 1), (Volatile.Read(ref leftCalls), Volatile.Read(ref rightCalls)));
        Assert.Contains("LiveState", await terminal.CaptureScreenAsync());
    }

    [Fact]
    public async Task LiveViews_AreRetainedAndDisposedExactlyOnReplacementAndRemoval()
    {
        var created = 0;
        var disposed = 0;
        var output = host.ForPlugin("Lifecycle");
        var workspace = output.CreateWorkspace("生命周期");
        output.SetPanel(workspace, "main", "v1", Content("v1"));

        await StartAsync();
        await terminal.WaitForScreenAsync("v1");
        output.Notify(workspace, "does not rebuild", ttl: TimeSpan.FromMinutes(1));
        await terminal.WaitForScreenAsync("does not rebuild");
        Assert.Equal((1, 0), (created, disposed));

        output.SetPanel(workspace, "main", "v2", Content("v2"));
        await terminal.WaitForScreenAsync("v2");
        Assert.Equal((2, 1), (created, disposed));

        output.RemoveWorkspace(workspace);
        await terminal.WaitForAsync(() => disposed == 2);

        LiveDisplayContent Content(string text)
            => new(() =>
            {
                created++;
                var view = new Label { Text = text };
                view.Disposing += (_, _) => disposed++;
                return view;
            });
    }

    [Fact]
    public async Task HiddenLiveViews_AreDisposedExactlyOnceWhenReplaced()
    {
        var output = host.ForPlugin("HiddenLifecycle");
        var first = output.CreateWorkspace("First");
        var firstCreated = 0;
        var firstDisposed = 0;
        output.SetPanel(first, "main", "first", Tracked("First-v1", () => firstCreated++, () => firstDisposed++));

        await StartAsync();
        await terminal.WaitForScreenAsync("First-v1");

        var second = output.CreateWorkspace("Second");
        var hiddenCreated = 0;
        var hiddenDisposed = 0;
        output.SetPanel(second, "normal", "normal", Tracked("Hidden-v1", () => hiddenCreated++, () => hiddenDisposed++));
        await terminal.WaitForScreenAsync("Hidden-v1");
        output.SetPanel(second, "cover", "cover", LiveDisplayContent.Text("FullBleedCover"), fullBleed: true);
        await terminal.WaitForScreenAsync("FullBleedCover");

        output.SetPanel(
            second,
            "normal",
            "normal-v2",
            Tracked("Hidden-v2", () => hiddenCreated++, () => hiddenDisposed++),
            switchToWorkspace: false);
        await terminal.WaitForAsync(() => hiddenDisposed == 1);
        output.SetPanel(
            second,
            "normal",
            "normal-v3",
            Tracked("Hidden-v3", () => hiddenCreated++, () => hiddenDisposed++),
            switchToWorkspace: false);
        Assert.Equal((1, 1), (hiddenCreated, hiddenDisposed));

        output.SwitchWorkspace(first);
        await terminal.WaitForScreenAsync("First-v1");
        output.SwitchWorkspace(second);
        await terminal.WaitForScreenAsync("FullBleedCover");
        output.SetPanel(
            first,
            "main",
            "first-v2",
            Tracked("First-v2", () => firstCreated++, () => firstDisposed++),
            switchToWorkspace: false);
        await terminal.WaitForAsync(() => firstDisposed == 1);
        output.SetPanel(
            first,
            "main",
            "first-v3",
            Tracked("First-v3", () => firstCreated++, () => firstDisposed++),
            switchToWorkspace: false);

        Assert.Equal((1, 1), (firstCreated, firstDisposed));

        static LiveDisplayContent Tracked(string text, Action created, Action disposed)
            => new(() =>
            {
                created();
                var view = new Label { Text = text };
                view.Disposing += (_, _) => disposed();
                return view;
            });
    }

    [Fact]
    public async Task LogsAndNotifications_AreScopedWithoutSwitchingWorkspace()
    {
        var output = host.ForPlugin("Scopes");
        var first = output.CreateWorkspace("第一");
        var second = output.CreateWorkspace("第二");
        output.SetPanel(first, "main", "第一", LiveDisplayContent.Text("FirstBody"));
        output.SetPanel(second, "main", "第二", LiveDisplayContent.Text("SecondBody"));
        output.SwitchWorkspace(first);
        output.Log(second, "SecondLog");
        output.Notify(second, "SecondNotify", ttl: TimeSpan.FromMinutes(1));
        host.Log(new LiveDisplayLogLine(null, "Host", "GlobalLog", LiveDisplaySeverity.Info));
        host.Notify(new LiveDisplayNotification(
            null,
            "Host",
            "GlobalNotify",
            LiveDisplaySeverity.Info,
            DateTimeOffset.Now.AddMinutes(1),
            []));

        await StartAsync();
        await terminal.WaitForScreenAsync("GlobalNotify");
        var screen = await terminal.CaptureScreenAsync();

        Assert.Equal(first, host.CurrentWorkspace);
        Assert.Contains("FirstBody", screen);
        Assert.Contains("GlobalLog", screen);
        Assert.Contains("GlobalNotify", screen);
        Assert.DoesNotContain("SecondLog", screen);
        Assert.DoesNotContain("SecondNotify", screen);
        Assert.Single(host.GetNotificationsForTests(second));
    }

    [Fact]
    public async Task Notification_DisappearsFromFramebufferAfterTtl()
    {
        var output = host.ForPlugin("Ttl");
        var workspace = output.CreateWorkspace("TTL");
        output.SetPanel(workspace, "main", "main", LiveDisplayContent.Text("TtlBody"), fullBleed: true);
        output.Notify(workspace, "ExpiringNotification", ttl: TimeSpan.FromMilliseconds(20));

        await StartAsync();
        await terminal.WaitForScreenAsync("ExpiringNotification");
        await Task.Delay(50, TestContext.Current.CancellationToken);
        terminal.Time.Advance(TimeSpan.FromSeconds(1));
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains("ExpiringNotification", StringComparison.Ordinal));

        Assert.Contains("TtlBody", await terminal.CaptureScreenAsync());
    }

    [Fact]
    public async Task RemovingWorkspace_IsSynchronousIdempotentAndRejectsLateOutput()
    {
        var output = host.ForPlugin("Removal");
        var first = output.CreateWorkspace("第一");
        var second = output.CreateWorkspace("第二");
        output.SetPanel(first, "main", "第一", LiveDisplayContent.Text("FirstBody"));
        output.SetPanel(second, "main", "第二", LiveDisplayContent.Text("SecondBody"));

        output.RemoveWorkspace(first);
        output.RemoveWorkspace(first);
        output.SetPanel(first, "late", "late", LiveDisplayContent.Text("LateBody"));
        var recreated = output.CreateWorkspace("第一");
        output.SetPanel(recreated, "main", "recreated", LiveDisplayContent.Text("RecreatedBody"));

        await StartAsync();
        await terminal.WaitForScreenAsync("RecreatedBody");
        var screen = await terminal.CaptureScreenAsync();

        Assert.NotSame(first, recreated);
        Assert.DoesNotContain("LateBody", screen);
        Assert.Empty(host.GetLogsForTests(first));
        Assert.Empty(host.GetNotificationsForTests(first));
    }

    [Fact]
    public async Task BackgroundWorkspaceRemoval_ReleasesIdentityGateAndCleansUpOnce()
    {
        var output = host.ForPlugin("ConcurrentRemoval");
        var removed = output.CreateWorkspace("Removed concurrently");
        var replacement = output.CreateWorkspace("Replacement workspace");
        var disposed = 0;
        var shortcutCalls = 0;
        output.SetPanel(
            removed,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                var view = new Label { Text = "Concurrent removal target" };
                view.Disposing += (_, _) => disposed++;
                return view;
            }));
        output.SetPanel(
            replacement,
            "main",
            "main",
            LiveDisplayContent.Text("Replacement remains"),
            switchToWorkspace: false);
        output.Notify(
            removed,
            "Removal shortcut",
            ttl: TimeSpan.FromMinutes(1),
            shortcuts: new LiveDisplayShortcut(ConsoleKey.F8, () =>
            {
                Interlocked.Increment(ref shortcutCalls);
                return Task.CompletedTask;
            }));

        await StartAsync();
        await terminal.WaitForScreenAsync("Concurrent removal target");
        await terminal.WaitForScreenAsync("Removal shortcut");
        Assert.Equal(1, KeyboardManager.TransientShortcutCountForTests);

        LiveDisplayWorkspace? competingWorkspace = null;
        var armed = 0;
        EventHandler<Terminal.Gui.App.TimeoutEventArgs> added = (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
                return;

            var competing = Task.Factory.StartNew(
                () => host.CreateWorkspace("replacement WORKSPACE"),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!competing.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("workspaceIdentityGate was held while scheduling workspace removal.");
            competingWorkspace = competing.GetAwaiter().GetResult();
        };
        var timedEvents = terminal.Application.TimedEvents
            ?? throw new InvalidOperationException("Terminal.Gui timed events are unavailable.");
        timedEvents.Added += added;
        try
        {
            Volatile.Write(ref armed, 1);
            await Task.Factory.StartNew(
                    () => output.RemoveWorkspace(removed),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            timedEvents.Added -= added;
        }

        output.RemoveWorkspace(removed);
        await terminal.WaitForScreenAsync("Replacement remains");
        await terminal.WaitForAsync(() => Volatile.Read(ref disposed) == 1);
        await terminal.InjectAsync(Key.F8);

        Assert.Same(replacement, competingWorkspace);
        Assert.Same(replacement, host.CurrentWorkspace);
        Assert.Equal(1, disposed);
        Assert.Equal(0, shortcutCalls);
        Assert.Equal(0, KeyboardManager.TransientShortcutCountForTests);

        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        var titles = await terminal.InvokeAsync(() =>
            parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray());
        Assert.DoesNotContain(removed.Title, titles);
        Assert.Equal(1, titles.Count(title =>
            title.Equals(replacement.Title, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task EqualWorkspaceAlias_UsesCanonicalIdentityForOutputRemovalAndDisposal()
    {
        var output = host.ForPlugin("Canonical");
        var canonical = output.CreateWorkspace("Case Sensitive Title");
        var alias = LiveDisplayWorkspace.Create("case sensitive title");
        var secondAlias = LiveDisplayWorkspace.Create("CASE SENSITIVE TITLE");
        var disposed = 0;
        output.SetPanel(
            alias,
            "main",
            "alias",
            new LiveDisplayContent(() =>
            {
                var view = new Label { Text = "AliasBody" };
                view.Disposing += (_, _) => disposed++;
                return view;
            }));
        output.Log(secondAlias, "Canonicalized before removal");

        await StartAsync();
        await terminal.WaitForScreenAsync("AliasBody");
        Assert.Same(canonical, host.CurrentWorkspace);

        output.RemoveWorkspace(alias);
        await terminal.WaitForAsync(() => disposed == 1);
        output.RemoveWorkspace(canonical);
        output.SetPanel(canonical, "late", "late", LiveDisplayContent.Text("LateCanonicalBody"));
        output.Log(alias, "LateAliasLog");
        output.SetPanel(secondAlias, "late-second", "late", LiveDisplayContent.Text("LateSecondAliasBody"));
        output.Log(secondAlias, "LateSecondAliasLog");
        var screen = await terminal.CaptureScreenAsync();

        Assert.Equal(1, disposed);
        Assert.Null(host.CurrentWorkspace);
        Assert.DoesNotContain("LateCanonicalBody", screen);
        Assert.DoesNotContain("LateAliasLog", screen);
        Assert.DoesNotContain("LateSecondAliasBody", screen);
        Assert.DoesNotContain("LateSecondAliasLog", screen);
    }

    [Fact]
    public async Task CommandParserAndCompletion_PreserveQuotedWorkspaceTitles()
    {
        var first = host.CreateWorkspace("First");
        var second = host.CreateWorkspace("Second \"Workspace\"");
        host.SwitchWorkspace(first);

        await StartAsync();
        await host.HandleCommandAsync("/workspace switch \"Second \\\"Workspace\\\"\"");
        await terminal.WaitForAsync(() => ReferenceEquals(host.CurrentWorkspace, second));

        Assert.Contains(
            "/workspace switch \"Second \\\"Workspace\\\"\"",
            host.CompleteCommand("/workspace switch Sec"));

        await host.HandleCommandAsync("/workspace switch Missing");
        await terminal.WaitForAsync(() =>
            host.GetLogsForTests(null).Any(line =>
                line.Text.Contains("workspace 不存在: Missing", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CommandInput_UsesUnhandledTerminalGuiKeysAndRestoresFocus()
    {
        var output = host.ForPlugin("Command");
        var workspace = output.CreateWorkspace("Command");
        View? control = null;
        output.SetPanel(
            workspace,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                control = new View
                {
                    Text = "CommandFocusTarget",
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    CanFocus = true
                };
                return control;
            }),
            fullBleed: true);

        await StartAsync();
        await terminal.WaitForScreenAsync("CommandFocusTarget");
        await terminal.InvokeAsync(() => control!.SetFocus());

        await terminal.InjectAsync(new Key('/'));
        var commandMode = await GetCommandModeAsync();
        var commandInput = await GetCommandInputAsync(commandMode);
        await terminal.WaitForAsync(() => commandMode.IsOpen);

        Assert.Equal("/", commandInput.Text);
        Assert.IsType<TextField>(commandInput);
        Assert.True(await terminal.InvokeAsync(() => commandInput.HasFocus));
        await terminal.WaitForScreenAsync("Command Mode");
        var normalFrame = await terminal.InvokeAsync(commandMode.FrameToScreen);
        var normalScreen = await terminal.CaptureScreenAsync();
        Assert.Equal(0, normalFrame.X);
        Assert.Equal(80, normalFrame.Width);
        Assert.Equal(18, normalFrame.Bottom);
        Assert.Contains("CommandFocusTarget", normalScreen);

        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);
        Assert.True(await terminal.InvokeAsync(() => control!.HasFocus));

        await terminal.InjectAsync(new Key('/').WithShift);
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        Assert.Equal("/", commandInput.Text);
        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);

        var enterHotkeyInvocations = 0;
        KeyboardManager.Register(ConsoleKey.Enter, "Enter hotkey", () =>
        {
            enterHotkeyInvocations++;
            return Task.CompletedTask;
        });
        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => enterHotkeyInvocations == 1);
        Assert.False(commandMode.IsOpen);
        Assert.True(KeyboardManager.Unregister(ConsoleKey.Enter));

        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        Assert.Equal(string.Empty, commandInput.Text);
    }

    [Fact]
    public async Task WorkspaceTaskbar_CommandModeSuppressesTriggerAndPreservesInputFocus()
    {
        var output = host.ForPlugin("TaskbarCommand");
        var first = output.CreateWorkspace("Taskbar command");
        var second = output.CreateWorkspace("Other workspace");
        View? focusTarget = null;
        output.SetPanel(
            first,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                focusTarget = new View
                {
                    Text = "TaskbarCommandFocus",
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    CanFocus = true
                };
                return focusTarget;
            }),
            fullBleed: true);
        output.SetPanel(second, "main", "main", LiveDisplayContent.Text("Other"), fullBleed: true);
        host.SwitchWorkspace(first);

        await StartAsync();
        await terminal.WaitForScreenAsync("TaskbarCommandFocus");
        await terminal.InvokeAsync(() => focusTarget!.SetFocus());
        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        await terminal.MoveMouseAsync(new Point(0, 17));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
        var taskbarCell = await terminal.InvokeAsync(() =>
            parts.Popup.SubViews
                .OfType<Shortcut>()
                .First()
                .CommandView!
                .ViewportToScreen(Point.Empty));
        var taskbarAttribute = await terminal.CaptureAttributeAsync(taskbarCell);
        Assert.Equal(
            new Terminal.Gui.Drawing.Attribute(StandardColor.Black, StandardColor.Cyan).Background,
            taskbarAttribute!.Value.Background);

        var dragItem = await terminal.InvokeAsync(() =>
            parts.Popup.SubViews.OfType<Shortcut>().Last());
        var dragFrame = await terminal.InvokeAsync(dragItem.FrameToScreen);
        var dragStart = new Point(dragFrame.X + dragFrame.Width / 2, dragFrame.Y);
        var dragMove = dragStart with { X = dragStart.X - 2 };
        var orderBeforeCommand = await terminal.InvokeAsync(() =>
            parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray());
        await terminal.InjectAsync(MouseAt(dragStart, MouseFlags.LeftButtonPressed));
        await terminal.InjectAsync(MouseAt(
            dragMove,
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        Assert.True(await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed(dragItem)));

        await terminal.InjectAsync(new Key('/'));
        var commandMode = await GetCommandModeAsync();
        var commandInput = await GetCommandInputAsync(commandMode);
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        Assert.False(await terminal.InvokeAsync(() => parts.Popup.Visible));
        Assert.False(await terminal.InvokeAsync(() => parts.Trigger.Visible));
        Assert.False(await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed()));
        Assert.Equal(
            orderBeforeCommand,
            await terminal.InvokeAsync(() =>
                parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray()));
        Assert.Equal(0, workspaceTaskbarSaveCount);
        Assert.True(await terminal.InvokeAsync(() => commandInput.HasFocus));
        Assert.Equal("/", commandInput.Text);

        foreach (var point in new[] { new Point(0, 17), new Point(40, 17), new Point(79, 17) })
            await terminal.MoveMouseAsync(point);
        await terminal.InjectAsync(new Key('马'));
        await terminal.RedrawAsync();
        var commandScreen = await terminal.CaptureScreenAsync();
        var commandAttribute = await terminal.CaptureAttributeAsync(taskbarCell);

        Assert.False(await terminal.InvokeAsync(() => parts.Popup.Visible));
        Assert.False(await terminal.InvokeAsync(() => parts.Trigger.Visible));
        Assert.True(commandMode.IsOpen);
        Assert.True(await terminal.InvokeAsync(() => commandInput.HasFocus));
        Assert.Equal("/马", commandInput.Text);
        Assert.NotEqual(taskbarAttribute, commandAttribute);
        Assert.Contains("Command Mode", commandScreen);
        Assert.DoesNotContain("Taskbar command", commandScreen);
        Assert.DoesNotContain("Other workspace", commandScreen);
        Assert.Same(
            commandMode,
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.SubViews.Last()));

        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);
        await terminal.RedrawAsync();
        Assert.False(await terminal.InvokeAsync(() => parts.Popup.Visible));
        Assert.True(await terminal.InvokeAsync(() => parts.Trigger.Visible));
        Assert.Same(
            focusTarget,
            await terminal.InvokeAsync(() => terminal.Application.TopRunnableView!.MostFocused));

        await terminal.MoveMouseAsync(Point.Empty);
        await terminal.MoveMouseAsync(new Point(0, 17));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => parts.Popup.Visible));
    }

    [Fact]
    public async Task BootstrapControls_UnhandledSlashOpensCommandMode()
    {
        var bootstrap = new BootstrapWorkspace(host);
        bootstrap.SetSettings([("Test", "Value")]);
        bootstrap.Log("Test", "bootstrap-slash-log");

        await StartAsync();
        await terminal.WaitForScreenAsync("bootstrap-slash-log");
        var controls = await terminal.InvokeAsync(() =>
        {
            var dashboard = Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single();
            var table = Descendants(dashboard).OfType<TableView>().First();
            var logList = Descendants(dashboard).OfType<ListView>().Single();
            return new (View Control, Func<(int Primary, int Secondary)> Selection)[]
            {
                (table, () => (table.Value!.SelectedCell.Y, table.Value!.SelectedCell.X)),
                (logList, () => (logList.SelectedItem ?? -1, -1))
            };
        });
        var commandMode = await GetCommandModeAsync();
        var commandInput = await GetCommandInputAsync(commandMode);

        foreach (var (control, selection) in controls)
        {
            await terminal.InvokeAsync(() => control.SetFocus());
            var before = await terminal.InvokeAsync(() => (
                Focused: terminal.Application.TopRunnableView!.MostFocused,
                Selection: selection()));
            Assert.Same(control, before.Focused);
            Assert.True(before.Selection.Primary >= 0);

            await terminal.InjectAsync(new Key('/'));
            await terminal.WaitForAsync(() => commandMode.IsOpen);

            Assert.Equal("/", commandInput.Text);
            await terminal.InjectAsync(Key.Esc);
            await terminal.WaitForAsync(() => !commandMode.IsOpen);

            var after = await terminal.InvokeAsync(() => (
                Focused: terminal.Application.TopRunnableView!.MostFocused,
                Selection: selection()));
            Assert.Same(before.Focused, after.Focused);
            Assert.Equal(before.Selection, after.Selection);
        }
    }

    [Fact]
    public async Task FocusedControl_HandledSlashWinsOverCommandInput()
    {
        var output = host.ForPlugin("CommandPriority");
        var workspace = output.CreateWorkspace("Command priority");
        View? control = null;
        var handledSlash = 0;
        var handledEnter = 0;
        output.SetPanel(
            workspace,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                control = new View
                {
                    Text = "SlashConsumer",
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    CanFocus = true
                };
                control.KeyDown += (_, key) =>
                {
                    if (key.AsGrapheme == "/")
                    {
                        handledSlash++;
                        key.Handled = true;
                    }
                    else if (key.KeyCode == Key.Enter.KeyCode)
                    {
                        handledEnter++;
                        key.Handled = true;
                    }
                };
                return control;
            }),
            fullBleed: true);

        await StartAsync();
        await terminal.WaitForScreenAsync("SlashConsumer");
        await terminal.InvokeAsync(() => control!.SetFocus());
        await terminal.InjectAsync(new Key('/'));
        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => handledSlash == 1 && handledEnter == 1);

        var commandMode = await GetCommandModeAsync();
        Assert.False(commandMode.IsOpen);
        Assert.Equal(1, handledSlash);
        Assert.Equal(1, handledEnter);
        Assert.DoesNotContain("Command Mode", await terminal.CaptureScreenAsync());
    }

    [Fact]
    public async Task CommandInput_UsesNativeEditingCompletionHistoryAndExactlyOnceExecution()
    {
        var output = host.ForPlugin("CommandEditing");
        var workspace = output.CreateWorkspace("Command editing");
        output.SetPanel(workspace, "main", "main", LiveDisplayContent.Text("CommandEditingBody"), fullBleed: true);

        await StartAsync();
        var commandMode = await GetCommandModeAsync();
        var commandInput = await GetCommandInputAsync(commandMode);

        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        await InjectTextAsync("/ab");
        await terminal.InjectAsync(Key.CursorLeft);
        await terminal.InjectAsync(new Key('你'));
        Assert.Equal("/a你b", commandInput.Text);
        await terminal.InjectAsync(Key.Backspace);
        Assert.Equal("/ab", commandInput.Text);
        output.SetPanel(
            workspace,
            "main",
            "main",
            LiveDisplayContent.Text("BackgroundUpdatedBody"),
            fullBleed: true,
            switchToWorkspace: false);
        await terminal.WaitForScreenAsync("BackgroundUpdatedBody");
        Assert.Same(commandInput, await GetCommandInputAsync(commandMode));
        Assert.Equal("/ab", commandInput.Text);
        Assert.True(await terminal.InvokeAsync(() => commandInput.HasFocus));
        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);
        Assert.DoesNotContain(
            host.GetLogsForTests(null),
            line => line.Text.Contains("未知命令", StringComparison.Ordinal));

        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        await terminal.InjectAsync(Key.Z.WithCtrl);
        Assert.Equal(string.Empty, commandInput.Text);
        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);

        await terminal.InjectAsync(new Key('/'));
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        Assert.Equal("/", commandInput.Text);
        await InjectTextAsync("workspace s");
        await terminal.InjectAsync(Key.Tab);
        Assert.Equal("/workspace switch", commandInput.Text);
        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);

        await terminal.InjectAsync(new Key('/'));
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        Assert.Equal("/", commandInput.Text);
        await InjectTextAsync("alpha");
        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);
        await terminal.WaitForAsync(() =>
            host.GetLogsForTests(null).Count(line => line.Text == "未知命令: /alpha") == 1);

        await terminal.InjectAsync(new Key('/'));
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        Assert.Equal("/", commandInput.Text);
        await InjectTextAsync("beta");
        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => !commandMode.IsOpen);
        await terminal.WaitForAsync(() =>
            host.GetLogsForTests(null).Count(line => line.Text == "未知命令: /beta") == 1);

        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        await terminal.InjectAsync(Key.CursorUp);
        Assert.Equal("/beta", commandInput.Text);
        await terminal.InjectAsync(Key.CursorUp);
        Assert.Equal("/alpha", commandInput.Text);
        await terminal.InjectAsync(Key.CursorDown);
        Assert.Equal("/beta", commandInput.Text);
        await terminal.InjectAsync(Key.CursorDown);
        Assert.Equal(string.Empty, commandInput.Text);
        await terminal.InjectAsync(Key.Esc);

        Assert.Equal(1, host.GetLogsForTests(null).Count(line => line.Text == "未知命令: /alpha"));
        Assert.Equal(1, host.GetLogsForTests(null).Count(line => line.Text == "未知命令: /beta"));
    }

    [Fact]
    public async Task CommandInput_RendersBottomFrameCandidatesAttributesAndCompactLayout()
    {
        for (var i = 0; i < 20; i++)
            host.CreateWorkspace($"Workspace-{i:00}");

        await StartAsync();
        await terminal.InjectAsync(new Key('/'));
        var commandMode = await GetCommandModeAsync();
        var commandInput = await GetCommandInputAsync(commandMode);
        await terminal.WaitForAsync(() => commandMode.IsOpen);
        Assert.Equal("/", commandInput.Text);
        await InjectTextAsync("workspace switch ");
        await terminal.InjectAsync(Key.Tab);
        await terminal.RedrawAsync();

        var matches = host.CompleteCommand("/workspace switch ");
        var layout = await terminal.InvokeAsync(() =>
        {
            var labels = Descendants(commandMode).OfType<Label>().ToArray();
            var title = labels.Single(x => x.Text?.ToString() == " Command Mode ");
            var candidates = labels.Single(x =>
                x.Text?.ToString()?.Contains("/workspace switch ", StringComparison.Ordinal) == true);
            var prompt = labels.Single(x => x.Text?.ToString() == "❯ ");
            var footer = labels.Single(x =>
                x.Text?.ToString() == "Tab 补全 · ↑↓ 历史 · Esc 取消");
            return (
                TitleView: title,
                CandidatesView: candidates,
                FooterView: footer,
                Frame: commandMode.FrameToScreen(),
                Title: title.FrameToScreen(),
                Candidates: candidates.FrameToScreen(),
                CandidateText: candidates.Text?.ToString() ?? string.Empty,
                Prompt: prompt.FrameToScreen(),
                Input: commandInput.FrameToScreen(),
                Footer: footer.FrameToScreen());
        });
        var screen = await terminal.CaptureScreenAsync();
        var rows = screen.Split(Environment.NewLine);
        var titleText = " Command Mode ";
        var titleColumn = rows[layout.Title.Y].IndexOf(titleText, StringComparison.Ordinal);
        var footerColumn = rows[layout.Footer.Y].IndexOf("Tab", StringComparison.Ordinal);
        var visibleCandidateSlots = layout.Candidates.Height;
        var expectedOverflow = matches.Count - Math.Max(0, visibleCandidateSlots - 1);

        Assert.Equal(new Rectangle(0, 0, 80, 18), layout.Frame);
        Assert.Equal('┌', rows[layout.Frame.Y][layout.Frame.X]);
        Assert.Equal('┐', rows[layout.Frame.Y][layout.Frame.Right - 1]);
        Assert.Equal('└', rows[layout.Frame.Bottom - 1][layout.Frame.X]);
        Assert.Equal('┘', rows[layout.Frame.Bottom - 1][layout.Frame.Right - 1]);
        Assert.InRange(titleColumn, (80 - titleText.Length) / 2 - 1, (80 - titleText.Length) / 2 + 1);
        Assert.True(layout.Candidates.Y < layout.Input.Y);
        Assert.Equal(layout.Input.Y + 1, layout.Footer.Y);
        Assert.All(
            layout.CandidateText.Split(Environment.NewLine),
            line => Assert.StartsWith("  ", line));
        Assert.Contains($"  +{expectedOverflow} more", layout.CandidateText);

        var candidateAttribute = await terminal.CaptureAttributeAsync(
            new Point(layout.Candidates.X + 2, layout.Candidates.Y));
        var promptAttribute = await terminal.CaptureAttributeAsync(layout.Prompt.Location);
        var inputAttribute = await terminal.CaptureAttributeAsync(layout.Input.Location);
        var footerAttribute = await terminal.CaptureAttributeAsync(
            new Point(footerColumn, layout.Footer.Y));
        Assert.Equal(candidateAttribute, footerAttribute);
        Assert.NotEqual(candidateAttribute, promptAttribute);
        Assert.NotEqual(promptAttribute, inputAttribute);

        await terminal.ResizeAsync(20, 4);
        await terminal.RedrawAsync();
        var compact = await terminal.InvokeAsync(() => (
            Frame: commandMode.FrameToScreen(),
            TitleVisible: layout.TitleView.Visible,
            CandidatesVisible: layout.CandidatesView.Visible,
            FooterVisible: layout.FooterView.Visible));
        var compactScreen = await terminal.CaptureScreenAsync();

        Assert.Equal(new Rectangle(0, 1, 20, 3), compact.Frame);
        Assert.False(compact.TitleVisible);
        Assert.False(compact.CandidatesVisible);
        Assert.False(compact.FooterVisible);
        Assert.Contains("❯", compactScreen);
        Assert.DoesNotContain("Command Mode", compactScreen);
    }

    [Fact]
    public async Task CommandInput_CtrlCStopsApplicationLifetime()
    {
        var output = host.ForPlugin("CommandShutdown");
        var workspace = output.CreateWorkspace("Command shutdown");
        output.SetPanel(
            workspace,
            "main",
            "main",
            LiveDisplayContent.Text("CommandShutdownBody"),
            fullBleed: true);

        await StartAsync();
        await terminal.InjectAsync(Key.Enter);
        await terminal.WaitForScreenAsync("Command Mode");
        await terminal.InjectAsync(Key.C.WithCtrl);

        await run!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(terminal.Application.SessionStack!);
    }

    [Fact]
    public async Task PluginControls_KeepFocusKeyboardAndMouseBehavior()
    {
        var output = host.ForPlugin("Controls");
        var workspace = output.CreateWorkspace("Controls");
        TextField? input = null;
        ListView? list = null;
        Button? button = null;
        var accepted = 0;
        output.SetPanel(
            workspace,
            "controls",
            "Controls",
            new LiveDisplayContent(() =>
            {
                var root = new View { Width = Dim.Fill(), Height = 7 };
                input = new TextField { X = 1, Y = 0, Width = 12, Text = string.Empty };
                list = new ListView { X = 1, Y = 2, Width = 12, Height = 2 };
                list.SetSource(new ObservableCollection<string>(["one", "two"]));
                list.SelectedItem = 0;
                button = new Button { X = 1, Y = 5, Text = "Accept" };
                button.Accepting += (_, _) => accepted++;
                root.Add(input, list, button);
                return root;
            }));

        await StartAsync();
        await terminal.WaitForScreenAsync("Accept");
        var inputFrame = await terminal.InvokeAsync(() => input!.FrameToScreen());
        await terminal.ClickAsync(new Point(inputFrame.X + 1, inputFrame.Y));
        await terminal.InjectAsync(Key.A);
        output.Log(workspace, "RetainFocusLog");
        await terminal.WaitForScreenAsync("RetainFocusLog");
        Assert.True(await terminal.InvokeAsync(() => input!.HasFocus));
        await terminal.InjectAsync(Key.B);
        await terminal.InjectAsync(Key.Tab);
        await terminal.InjectAsync(Key.CursorDown);
        await terminal.InjectAsync(Key.Tab);
        await terminal.InjectAsync(Key.Enter);

        Assert.Equal("ab", input?.Text?.ToString());
        Assert.Equal(1, list?.SelectedItem);
        Assert.Equal(1, accepted);

        var buttonFrame = await terminal.InvokeAsync(() => button!.FrameToScreen());
        var click = new Point(buttonFrame.X + Math.Max(0, buttonFrame.Width / 2), buttonFrame.Y);
        await terminal.ClickAsync(click);
        Assert.Equal(2, accepted);

        output.Notify(workspace, "Visible notification", ttl: TimeSpan.FromMinutes(1));
        await terminal.WaitForScreenAsync("Visible notification");
        await terminal.ClickAsync(click);
        Assert.Equal(3, accepted);
    }

    [Fact]
    public async Task FocusedPluginControl_CanConsumeCtrlCWithoutGlobalShutdown()
    {
        var output = host.ForPlugin("ControlPriority");
        var workspace = output.CreateWorkspace("Control priority");
        View? control = null;
        var handled = 0;
        output.SetPanel(
            workspace,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                control = new View
                {
                    Text = "CtrlCControl",
                    Width = 20,
                    Height = 1,
                    CanFocus = true
                };
                control.KeyDown += (_, key) =>
                {
                    if (key == Key.C.WithCtrl)
                    {
                        handled++;
                        key.Handled = true;
                    }
                };
                return control;
            }),
            fullBleed: true);

        await StartAsync();
        await terminal.WaitForScreenAsync("CtrlCControl");
        await terminal.InvokeAsync(() => control!.SetFocus());
        await terminal.InjectAsync(Key.C.WithCtrl);
        await terminal.WaitForAsync(() => handled == 1);

        Assert.False(run!.IsCompleted);
        await terminal.StopAsync(host, run);
    }

    [Fact]
    public async Task OverlayLabelRegions_DrawAboveButPassMouseClicksThrough()
    {
        var output = host.ForPlugin("OverlayHitTest");
        var workspace = output.CreateWorkspace("Overlay hit test");
        Button? top = null;
        Button? bottom = null;
        var topHits = 0;
        var bottomHits = 0;
        output.SetPanel(
            workspace,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                var root = new View { Width = Dim.Fill(), Height = Dim.Fill() };
                top = new Button { Text = "TopTarget", X = Pos.AnchorEnd(16), Y = 2 };
                bottom = new Button { Text = "BottomTarget", X = 1, Y = Pos.AnchorEnd(1) };
                top.Accepting += (_, _) => topHits++;
                bottom.Accepting += (_, _) => bottomHits++;
                root.Add(top, bottom);
                return root;
            }),
            fullBleed: true);

        await StartAsync();
        await terminal.WaitForScreenAsync("TopTarget");

        output.Notify(
            workspace,
            "NotificationOverlay",
            ttl: TimeSpan.FromMinutes(1));
        await terminal.WaitForScreenAsync("NotificationOverlay");
        var topFrame = await terminal.InvokeAsync(() => top!.FrameToScreen());
        await terminal.ClickAsync(new Point(topFrame.X + topFrame.Width / 2, topFrame.Y));
        Assert.Equal(1, topHits);

        KeyboardManager.ShowPopup(new KeyboardHandlerContext().WriteLine("KeyboardOverlay"));
        await terminal.WaitForScreenAsync("KeyboardOverlay");
        var bottomFrame = await terminal.InvokeAsync(() => bottom!.FrameToScreen());
        var bottomPoint = new Point(bottomFrame.X + bottomFrame.Width / 2, bottomFrame.Y);
        await terminal.ClickAsync(bottomPoint);
        Assert.Equal(1, bottomHits);

        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains("KeyboardOverlay", StringComparison.Ordinal));
        Assert.False(run!.IsCompleted);
    }

    [Fact]
    public async Task NestedDialog_RestoresFocusAndCtrlCStopsTheWholeApplication()
    {
        var output = host.ForPlugin("Nested");
        var workspace = output.CreateWorkspace("Nested");
        Button? button = null;
        var accepted = 0;
        output.SetPanel(
            workspace,
            "main",
            "main",
            new LiveDisplayContent(() =>
            {
                button = new Button { Text = "Underlying" };
                button.Accepting += (_, _) => accepted++;
                return button;
            }),
            fullBleed: true);
        await StartAsync();
        await terminal.WaitForScreenAsync("Underlying");
        await terminal.InvokeAsync(() => button!.SetFocus());

        var confirm = Task.Run(
            () => TerminalGuiDialogs.Confirm(terminal.Application, "Nested confirm"),
            TestContext.Current.CancellationToken);
        await terminal.WaitForScreenAsync("Nested confirm");
        await terminal.InjectAsync(Key.Esc);
        Assert.False(await confirm);

        await terminal.InjectAsync(Key.Enter);
        Assert.Equal(1, accepted);

        var nestedAtShutdown = Task.Run(
            () => TerminalGuiDialogs.Confirm(terminal.Application, "Stop nested"),
            TestContext.Current.CancellationToken);
        await terminal.WaitForScreenAsync("Stop nested");
        await terminal.InjectAsync(Key.C.WithCtrl);

        await nestedAtShutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await run!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(terminal.Application.SessionStack!);
    }

    [Fact]
    public async Task BackgroundRequestShutdown_StopsNestedDialogBeforeMainSession()
    {
        var output = host.ForPlugin("NestedShutdown");
        var workspace = output.CreateWorkspace("Nested shutdown");
        output.SetPanel(workspace, "main", "main", LiveDisplayContent.Text("NestedShutdownBody"), fullBleed: true);
        await StartAsync();

        var confirm = Task.Run(
            () => TerminalGuiDialogs.Confirm(terminal.Application, "Background shutdown modal"),
            TestContext.Current.CancellationToken);
        await terminal.WaitForScreenAsync("Background shutdown modal");
        await Task.Run(host.RequestShutdown, TestContext.Current.CancellationToken);

        Assert.False(await confirm.WaitAsync(TimeSpan.FromSeconds(5)));
        await run!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(terminal.Application.SessionStack!);
    }

    [Fact]
    public async Task LifetimeCancellation_StopsPluginModalAndMainSession()
    {
        using var cancellation = new CancellationTokenSource();
        BindHost(host, cancellation.Token, unbindFirst: true);
        var output = host.ForPlugin("ModalCancellation");
        var workspace = output.CreateWorkspace("Modal cancellation");
        output.SetPanel(
            workspace,
            "main",
            "main",
            LiveDisplayContent.Text("ModalCancellationBody"),
            fullBleed: true);
        run = await terminal.StartAsync(host, cancellation.Token);
        await terminal.WaitForScreenAsync("ModalCancellationBody");

        var confirm = Task.Run(
            () => LiveDisplayConsole.Confirm("Cancelled plugin modal"),
            TestContext.Current.CancellationToken);
        await terminal.WaitForScreenAsync("Cancelled plugin modal");
        await Task.Run(cancellation.Cancel, TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => confirm.WaitAsync(TimeSpan.FromSeconds(5)));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(terminal.Application.SessionStack!);
    }

    [Fact]
    public async Task BackgroundOutput_IsMarshalledIntoTheMainLoop()
    {
        var output = host.ForPlugin("Background");
        var workspace = output.CreateWorkspace("Background");
        output.SetPanel(workspace, "main", "main", LiveDisplayContent.Text("Before"), fullBleed: true);

        var starting = terminal.StartAsync(host);
        await Task.Run(
            () => output.SetPanel(
                workspace,
                "main",
                "main",
                LiveDisplayContent.Text("After"),
                fullBleed: true),
            TestContext.Current.CancellationToken);
        run = await starting;

        await terminal.WaitForScreenAsync("After");
        Assert.DoesNotContain("Before", await terminal.CaptureScreenAsync());
    }

    [Fact]
    public async Task BootstrapDashboard_FirstVisibleAndReplacementFramesAreAlwaysValid()
    {
        var frames = new ConcurrentQueue<BootstrapDrawFrame>();
        EventHandler<EventArgs> capture = (_, _) =>
        {
            var top = terminal.Application.TopRunnableView!;
            var dashboard = Descendants(top).OfType<BootstrapDashboardView>().SingleOrDefault();
            if (dashboard?.SuperView is not { } surface)
                return;

            var screen = terminal.Application.Driver?.ToString() ?? string.Empty;
            frames.Enqueue(new(
                screen,
                top.Viewport,
                surface.Frame,
                surface.Viewport,
                surface.GetContentSize(),
                dashboard.Frame,
                dashboard.Viewport,
                dashboard.SubViews
                    .OfType<FrameView>()
                    .ToDictionary(x => x.Title.ToString()!, x => x.Frame)));
        };
        terminal.RunOnOwnerThread(() => terminal.Application.LayoutAndDrawComplete += capture);
        try
        {
            var bootstrap = new BootstrapWorkspace(host);
            await StartAsync();
            await terminal.WaitForScreenAsync("运行环境");

            var drawCount = frames.Count;
            await Task.Run(
                () => bootstrap.SetPhase(
                    "config",
                    "替换项",
                    LiveDisplaySeverity.Success,
                    "R1"),
                TestContext.Current.CancellationToken);
            await terminal.WaitForAsync(() => frames.Count > drawCount);

            drawCount = frames.Count;
            await Task.Run(
                () => bootstrap.SetPluginSummary(
                [
                    new("FirstFramePlugin", "1.0.0", "OK", "replacement")
                ]),
                TestContext.Current.CancellationToken);
            await terminal.WaitForAsync(() => frames.Count > drawCount);

            drawCount = frames.Count;
            await Task.Run(
                () => bootstrap.Log("Worker", "replacement log"),
                TestContext.Current.CancellationToken);
            await terminal.WaitForAsync(() => frames.Count > drawCount);

            await terminal.ResizeAsync(120, 36);
            await terminal.WaitForAsync(() => frames.Any(frame => frame.RootViewport.Width == 120));
        }
        finally
        {
            await terminal.InvokeAsync(() => terminal.Application.LayoutAndDrawComplete -= capture);
        }

        var captured = frames.ToArray();
        Assert.NotEmpty(captured);
        Assert.All(captured, AssertValidBootstrapFrame);
        Assert.Contains(captured, frame => frame.RootViewport.Width == 80);
        Assert.Contains(captured, frame => frame.RootViewport.Width == 120);

        var layout = await terminal.InvokeAsync(() =>
        {
            var top = terminal.Application.TopRunnableView!;
            var dashboard = Descendants(top).OfType<BootstrapDashboardView>().Single();
            var dashboardFrames = dashboard.SubViews
                .OfType<FrameView>()
                .ToDictionary(x => x.Title.ToString()!);
            return (
                Dashboard: dashboard.Frame,
                DashboardViewport: dashboard.Viewport,
                Environment: dashboardFrames["运行环境"].Frame,
                Phase: dashboardFrames["初始化结果"].Frame,
                Logs: dashboardFrames["最近日志"].Frame,
                FrameCount: dashboardFrames.Count,
                TableCount: dashboard.SubViews
                    .OfType<FrameView>()
                    .SelectMany(x => x.SubViews)
                    .OfType<TableView>()
                    .Count(),
                ListCount: dashboard.SubViews
                    .OfType<FrameView>()
                    .SelectMany(x => x.SubViews)
                    .OfType<ListView>()
                    .Count(),
                HasOuterFrame: Descendants(top)
                    .OfType<FrameView>()
                    .Any(x => x.Title.ToString() == "URA - 启动状态"),
                HasStatusBar: Descendants(top).OfType<StatusBar>().Any());
        });
        var screen = await terminal.CaptureScreenAsync();

        Assert.Equal(new Rectangle(0, 0, 120, 36), layout.Dashboard);
        Assert.Equal(0, layout.Environment.Y);
        Assert.Equal(0, layout.Phase.Y);
        Assert.Equal(layout.DashboardViewport.Height, layout.Logs.Bottom);
        Assert.Equal(4, layout.FrameCount);
        Assert.Equal(3, layout.TableCount);
        Assert.Equal(1, layout.ListCount);
        Assert.False(layout.HasOuterFrame);
        Assert.False(layout.HasStatusBar);
        Assert.DoesNotContain("URA - 启动状态", screen);
        Assert.DoesNotContain("启动信息", screen);
        Assert.DoesNotContain("Ctrl+B", screen);
    }

    [Fact]
    public async Task BootstrapDashboard_ShowsPluginLogsScopedToBootstrapWorkspace()
    {
        var bootstrap = new BootstrapWorkspace(host);
        var dmm = host.ForPlugin("DMM插件");
        var otherOutput = host.ForPlugin("Other");
        var otherWorkspace = otherOutput.CreateWorkspace("Other");

        dmm.Log(bootstrap.Workspace, "DMM bootstrap status", LiveDisplaySeverity.Success);
        otherOutput.Log(otherWorkspace, "Other workspace status", LiveDisplaySeverity.Error);

        await StartAsync();
        await terminal.WaitForScreenAsync("DMM bootstrap status");

        var layout = await terminal.InvokeAsync(() =>
        {
            var dashboard = Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single();
            return (
                DashboardFrame: dashboard.Frame,
                FrameCount: dashboard.SubViews.OfType<FrameView>().Count(),
                ListCount: dashboard.SubViews
                    .OfType<FrameView>()
                    .SelectMany(x => x.SubViews)
                    .OfType<ListView>()
                    .Count());
        });
        var screen = await terminal.CaptureScreenAsync();

        Assert.Equal(new Rectangle(0, 0, 80, 18), layout.DashboardFrame);
        Assert.Equal(4, layout.FrameCount);
        Assert.Equal(1, layout.ListCount);
        Assert.Contains("OK [DMM插件] DMM bootstrap status", screen);
        Assert.DoesNotContain("Other workspace status", screen);
        Assert.Contains(
            host.GetLogsForTests(bootstrap.Workspace),
            x => x.PluginId == "DMM插件" && x.Text == "DMM bootstrap status");
    }

    [Fact]
    public async Task BootstrapDashboard_ReplacesRowsAndShowsBackgroundUpdates()
    {
        var bootstrap = new BootstrapWorkspace(host);
        bootstrap.SetSettings(
        [
            ("版本", "1.14.4"),
            ("工作目录", Directory.GetCurrentDirectory()),
            ("配置文件", "config.yaml"),
            ("监听地址", "http://127.0.0.1:4693"),
            ("服务器目标", "jp"),
            ("数据语言", "zh-TW"),
            ("训练员性别", "男"),
            ("更新源", "https://example.invalid/assets")
        ]);
        bootstrap.SetPhase("database", "数据文件", LiveDisplaySeverity.Info, "旧初始化结果");
        bootstrap.SetPluginSummary(
        [
            new(
                new("PluginBefore", "PluginBefore", string.Empty, new Version(1, 0), true, true, false),
                initialized: false,
                failed: false),
            new(
                new("TargetSkipped", "TargetSkipped", string.Empty, null, false, true, false),
                initialized: false,
                failed: false),
            new(
                new("BrokenPlugin", "BrokenPlugin", string.Empty, null, false, true, false),
                initialized: false,
                failed: true)
        ]);
        for (var i = 0; i < 140; i++)
            bootstrap.Log("Test", $"bootstrap-log-{i:000}");

        await StartAsync();
        await terminal.ResizeAsync(120, 36);
        await terminal.RedrawAsync();
        await terminal.WaitForScreenAsync("旧初始化结果");

        var disposed = 0;
        await terminal.InvokeAsync(() =>
            Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single()
                .Disposing += (_, _) => disposed++);

        await Task.Run(() =>
        {
            bootstrap.SetPhase("database", "数据文件", LiveDisplaySeverity.Success, "后台更新完成");
            bootstrap.SetPluginSummary(
            [
                new(
                    new("PluginAfter", "PluginAfter", string.Empty, new Version(1, 0, 1), true, true, false),
                    initialized: true,
                    failed: false),
                new(
                    new("TargetSkipped", "TargetSkipped", string.Empty, null, false, true, false),
                    initialized: true,
                    failed: false),
                new(
                    new("BrokenPlugin", "BrokenPlugin", string.Empty, null, false, true, false),
                    initialized: true,
                    failed: true)
            ]);
            bootstrap.Log("Worker", "后台关键日志", LiveDisplaySeverity.Warning);
        }, TestContext.Current.CancellationToken);
        await terminal.WaitForScreenAsync("后台更新完成");
        await terminal.WaitForScreenAsync("后台关键日志");

        var content = await terminal.InvokeAsync(() =>
        {
            var dashboard = Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single();
            var frames = dashboard.SubViews
                .OfType<FrameView>()
                .ToDictionary(x => x.Title.ToString()!);
            var phase = (DataTableSource)frames["初始化结果"].SubViews.OfType<TableView>().Single().Table!;
            var plugins = (DataTableSource)frames["插件摘要"].SubViews.OfType<TableView>().Single().Table!;
            var logs = frames["最近日志"].SubViews.OfType<ListView>().Single();
            return (
                PhaseRows: phase.DataTable.Rows.Count,
                PluginRows: plugins.DataTable.Rows.Count,
                LogRows: logs.Source?.Count ?? 0);
        });
        var screen = await terminal.CaptureScreenAsync();

        Assert.Equal(1, disposed);
        Assert.Equal(6, content.PhaseRows);
        Assert.Equal(3, content.PluginRows);
        Assert.Equal(128, content.LogRows);
        Assert.Contains("1.14.4", screen);
        Assert.Contains("config.yaml", screen);
        Assert.Contains("PluginAfter", screen);
        Assert.Contains("TargetSkipped", screen);
        Assert.Contains("未加载", screen);
        Assert.Contains("BrokenPlugin", screen);
        Assert.Contains("加载或初始化失败", screen);
        Assert.DoesNotContain("PluginBefore", screen);
        Assert.DoesNotContain("旧初始化结果", screen);
        Assert.DoesNotContain("bootstrap-log-000", screen);
        Assert.Contains(
            host.GetLogsForTests(bootstrap.Workspace),
            x => x.PluginId == "Worker" && x.Text == "后台关键日志");
    }

    [Fact]
    public async Task BootstrapUpdates_ReleaseStateGateBeforeTimedEventsDrain()
    {
        var bootstrap = new BootstrapWorkspace(host);
        bootstrap.SetPhase("database", "数据文件", LiveDisplaySeverity.Info, "旧数据文件状态");
        await StartAsync();
        await terminal.ResizeAsync(120, 36);
        await terminal.WaitForScreenAsync("旧数据文件状态");

        var pluginOutput = host.ForPlugin("ConcurrentInit");
        LiveDisplayWorkspace? pluginWorkspace = null;
        var armed = 0;
        EventHandler<Terminal.Gui.App.TimeoutEventArgs> added = (_, _) =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0)
                return;

            var initialization = Task.Factory.StartNew(
                () =>
                {
                    var workspace = pluginOutput.CreateWorkspace("Concurrent initialized plugin");
                    pluginOutput.SetPanel(
                        workspace,
                        "main",
                        "main",
                        LiveDisplayContent.Text("Concurrent plugin ready"),
                        switchToWorkspace: false);
                    bootstrap.SetPhase(
                        "database",
                        "数据文件",
                        LiveDisplaySeverity.Success,
                        "后台初始化完成");
                    return workspace;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            if (!initialization.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Bootstrap state lock was held while scheduling its UI refresh.");
            pluginWorkspace = initialization.GetAwaiter().GetResult();
        };
        var timedEvents = terminal.Application.TimedEvents
            ?? throw new InvalidOperationException("Terminal.Gui timed events are unavailable.");
        timedEvents.Added += added;
        try
        {
            Volatile.Write(ref armed, 1);
            await Task.Factory.StartNew(
                    () => bootstrap.SetSettings([("状态", "并发初始化")]),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            timedEvents.Added -= added;
        }

        await terminal.WaitForScreenAsync("后台初始化完成");
        Assert.DoesNotContain("旧数据文件状态", await terminal.CaptureScreenAsync());
        Assert.NotNull(pluginWorkspace);

        var taskbar = await GetWorkspaceTaskbarAsync();
        var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
        var titles = await terminal.InvokeAsync(() =>
            parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray());
        Assert.Equal(1, titles.Count(title =>
            title.Equals(pluginWorkspace!.Title, StringComparison.OrdinalIgnoreCase)));
        Assert.Same(bootstrap.Workspace, host.CurrentWorkspace);
    }

    [Fact]
    public async Task PluginInitializationAndTaskbarInput_CompleteConcurrently()
    {
        var bootstrap = new BootstrapWorkspace(host);
        bootstrap.SetPhase("database", "数据文件", LiveDisplaySeverity.Info, "等待后台初始化");
        await StartAsync();
        await terminal.ResizeAsync(120, 36);
        await terminal.WaitForScreenAsync("等待后台初始化");

        var pluginOutput = host.ForPlugin("ConcurrentTaskbarInit");
        var initializationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialization = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialization = Task.Factory.StartNew(
                () =>
                {
                    initializationStarted.TrySetResult();
                    releaseInitialization.Task.GetAwaiter().GetResult();
                    var workspace = pluginOutput.CreateWorkspace("Taskbar concurrent plugin");
                    pluginOutput.SetPanel(
                        workspace,
                        "main",
                        "main",
                        LiveDisplayContent.Text("Taskbar concurrent plugin ready"),
                        switchToWorkspace: false);
                    bootstrap.SetPhase(
                        "database",
                        "数据文件",
                        LiveDisplaySeverity.Success,
                        "taskbar 操作期间初始化完成");
                    return workspace;
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        try
        {
            await initializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initialization.IsCompleted);

            await terminal.MoveMouseAsync(new Point(0, 35));
            var taskbar = await GetWorkspaceTaskbarAsync();
            var parts = await terminal.InvokeAsync(() => TaskbarParts(taskbar));
            await terminal.WaitForAsync(async () =>
                await terminal.InvokeAsync(() => parts.Popup.Visible));
            Assert.False(initialization.IsCompleted);

            releaseInitialization.TrySetResult();
            var pluginWorkspace = await initialization.WaitAsync(TimeSpan.FromSeconds(5));
            await terminal.WaitForScreenAsync("taskbar 操作期间初始化完成");

            var titles = await terminal.InvokeAsync(() =>
                parts.Popup.SubViews.OfType<Shortcut>().Select(item => item.Title).ToArray());
            Assert.Equal(1, titles.Count(title =>
                title.Equals(pluginWorkspace.Title, StringComparison.OrdinalIgnoreCase)));
            Assert.Same(bootstrap.Workspace, host.CurrentWorkspace);
        }
        finally
        {
            releaseInitialization.TrySetResult();
            await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task BootstrapDashboard_UsesNativeScrollingAndResponsiveLayout()
    {
        var bootstrap = new BootstrapWorkspace(host);
        var longSettings = new (string Label, string Value)[]
        {
            ("版本", "1.14.4"),
            ("工作目录", $@"K:\{new string('w', 180)}"),
            ("配置文件", $@"K:\{new string('c', 180)}\config.yaml"),
            ("监听地址", "http://127.0.0.1:4693"),
            ("服务器目标", "jp, tw"),
            ("数据语言", "zh-TW"),
            ("训练员性别", "男"),
            ("更新源", $"https://example.invalid/{new string('u', 180)}")
        };
        var manyPlugins = Enumerable.Range(0, 100)
            .Select(i => new BootstrapPluginRow(
                $"Plugin-{i:000}",
                "1.0.0",
                "OK",
                i == 0 ? new string('r', 220) : $"result-{i:000}"))
            .ToArray();
        bootstrap.SetSettings(longSettings);
        bootstrap.SetPluginSummary(manyPlugins);
        for (var i = 0; i < 140; i++)
            bootstrap.Log("Test", $"log-{i:000}-{new string('l', 160)}");

        await StartAsync();
        await terminal.ResizeAsync(48, 32);
        await terminal.RedrawAsync();
        await terminal.WaitForScreenAsync("插件摘要");

        var narrow = await terminal.InvokeAsync(() =>
        {
            var dashboard = Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single();
            var frames = dashboard.SubViews
                .OfType<FrameView>()
                .ToDictionary(x => x.Title.ToString()!);
            return (
                Dashboard: dashboard.Frame,
                Environment: frames["运行环境"].Frame,
                Phase: frames["初始化结果"].Frame,
                Plugins: frames["插件摘要"].Frame,
                Logs: frames["最近日志"].Frame);
        });

        Assert.Equal(new Rectangle(0, 0, 48, 32), narrow.Dashboard);
        Assert.Equal(0, narrow.Environment.Y);
        Assert.Equal(narrow.Environment.Bottom, narrow.Phase.Y);
        Assert.Equal(narrow.Phase.Bottom, narrow.Plugins.Y);
        Assert.Equal(narrow.Plugins.Bottom, narrow.Logs.Y);
        Assert.Equal(narrow.Dashboard.Height, narrow.Logs.Bottom);

        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
            {
                var environment = DashboardFrame("运行环境").SubViews.OfType<TableView>().Single();
                var phase = DashboardFrame("初始化结果").SubViews.OfType<TableView>().Single();
                var plugins = DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single();
                var logs = DashboardFrame("最近日志").SubViews.OfType<ListView>().Single();
                return environment.VerticalScrollBar.Visible &&
                    environment.HorizontalScrollBar.Visible &&
                    phase.VerticalScrollBar.Visible &&
                    plugins.VerticalScrollBar.Visible &&
                    plugins.HorizontalScrollBar.Visible &&
                    logs.VerticalScrollBar.Visible &&
                    logs.HorizontalScrollBar.Visible;
            }));

        var overflow = await terminal.InvokeAsync(() =>
        {
            var environment = DashboardFrame("运行环境").SubViews.OfType<TableView>().Single();
            var phase = DashboardFrame("初始化结果").SubViews.OfType<TableView>().Single();
            var plugins = DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single();
            var logs = DashboardFrame("最近日志").SubViews.OfType<ListView>().Single();
            var vertical = plugins.VerticalScrollBar;
            var horizontal = plugins.HorizontalScrollBar;
            var driver = terminal.Application.Driver!;
            var up = vertical.ViewportToScreen(Point.Empty);
            var down = vertical.ViewportToScreen(new Point(0, vertical.Viewport.Height - 1));
            var left = horizontal.ViewportToScreen(Point.Empty);
            var right = horizontal.ViewportToScreen(new Point(horizontal.Viewport.Width - 1, 0));
            var surface = Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single()
                .SuperView!;
            return (
                EnvironmentContent: environment.GetContentSize(),
                EnvironmentViewport: environment.Viewport,
                EnvironmentPadding: environment.Padding.Thickness,
                PhaseContent: phase.GetContentSize(),
                PhaseViewport: phase.Viewport,
                PluginContent: plugins.GetContentSize(),
                PluginViewport: plugins.Viewport,
                PluginPadding: plugins.Padding.Thickness,
                LogContent: logs.GetContentSize(),
                LogViewport: logs.Viewport,
                LogPadding: logs.Padding.Thickness,
                LogSelectedItem: logs.SelectedItem,
                Up: driver.Contents![up.Y, up.X],
                Down: driver.Contents[down.Y, down.X],
                Left: driver.Contents[left.Y, left.X],
                Right: driver.Contents[right.Y, right.X],
                SurfaceViewport: surface.Viewport,
                SurfaceContent: surface.GetContentSize());
        });
        Assert.True(overflow.EnvironmentContent.Height > overflow.EnvironmentViewport.Height);
        Assert.True(overflow.EnvironmentContent.Width > overflow.EnvironmentViewport.Width);
        Assert.True(overflow.PhaseContent.Height > overflow.PhaseViewport.Height);
        Assert.True(overflow.PluginContent.Height > overflow.PluginViewport.Height);
        Assert.True(overflow.PluginContent.Width > overflow.PluginViewport.Width);
        Assert.True(overflow.LogContent.Height > overflow.LogViewport.Height);
        Assert.True(overflow.LogContent.Width > overflow.LogViewport.Width);
        Assert.Equal(1, overflow.EnvironmentPadding.Right);
        Assert.Equal(1, overflow.EnvironmentPadding.Bottom);
        Assert.Equal(1, overflow.PluginPadding.Right);
        Assert.Equal(1, overflow.PluginPadding.Bottom);
        Assert.Equal(1, overflow.LogPadding.Right);
        Assert.Equal(1, overflow.LogPadding.Bottom);
        Assert.Equal(127, overflow.LogSelectedItem);
        Assert.True(overflow.LogViewport.Y > 0);
        Assert.Equal(Glyphs.UpArrow.ToString(), overflow.Up.Grapheme);
        Assert.Equal(Glyphs.DownArrow.ToString(), overflow.Down.Grapheme);
        Assert.Equal(Glyphs.LeftArrow.ToString(), overflow.Left.Grapheme);
        Assert.Equal(Glyphs.RightArrow.ToString(), overflow.Right.Grapheme);
        Assert.NotNull(overflow.Up.Attribute);
        Assert.NotNull(overflow.Down.Attribute);
        Assert.NotNull(overflow.Left.Attribute);
        Assert.NotNull(overflow.Right.Attribute);
        Assert.Equal(0, overflow.SurfaceViewport.Y);
        Assert.Equal(overflow.SurfaceViewport.Size, overflow.SurfaceContent);
        Assert.Contains("log-139", await terminal.CaptureScreenAsync());

        var firstPluginCell = await terminal.InvokeAsync(() =>
            DashboardFrame("插件摘要")
                .SubViews
                .OfType<TableView>()
                .Single()
                .ViewportToScreen(new Point(1, 2)));
        var unfocusedAttribute = await terminal.CaptureAttributeAsync(firstPluginCell);
        var initialPluginRow = await terminal.InvokeAsync(() =>
        {
            var table = DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single();
            table.SetFocus();
            return table.Value!.SelectedCell.Y;
        });
        await terminal.RedrawAsync();
        Assert.NotEqual(unfocusedAttribute, await terminal.CaptureAttributeAsync(firstPluginCell));
        await terminal.InjectAsync(Key.CursorDown);
        await terminal.InjectAsync(Key.PageDown);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                DashboardFrame("插件摘要")
                    .SubViews
                    .OfType<TableView>()
                    .Single()
                    .Value!
                    .SelectedCell.Y > initialPluginRow));
        Assert.True(await terminal.InvokeAsync(() =>
            DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single().HasFocus));
        Assert.True(await terminal.InvokeAsync(() =>
            DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single().Viewport.Y > 0));

        await terminal.InjectAsync(Key.Home.WithCtrl);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single().Viewport.Y == 0));
        var pluginBody = await terminal.InvokeAsync(() =>
            DashboardFrame("插件摘要")
                .SubViews
                .OfType<TableView>()
                .Single()
                .ViewportToScreen(new Point(1, 2)));
        for (var i = 0; i < 8; i++)
        {
            await terminal.InjectAsync(new Mouse
            {
                ScreenPosition = pluginBody,
                Flags = MouseFlags.WheeledDown,
                Timestamp = terminal.Time.Now
            });
        }
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single().Viewport.Y > 0));
        Assert.Equal(0, await terminal.InvokeAsync(() =>
            DashboardFrame("插件摘要").SuperView!.Viewport.Y));

        await terminal.InjectAsync(Key.Home.WithCtrl);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single().Viewport.Y == 0));
        var sliderDrag = await terminal.InvokeAsync(() =>
        {
            var table = DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single();
            var vertical = table.VerticalScrollBar;
            var slider = table.VerticalScrollBar.Slider;
            var start = slider.ViewportToScreen(Point.Empty);
            var bottom = vertical.ViewportToScreen(new Point(0, vertical.Viewport.Height - 2)).Y;
            return (
                Start: start,
                End: start with { Y = Math.Min(start.Y + 2, bottom) });
        });
        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = sliderDrag.Start,
            Flags = MouseFlags.LeftButtonPressed,
            Timestamp = terminal.Time.Now
        });
        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = sliderDrag.End,
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Timestamp = terminal.Time.Now
        });
        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = sliderDrag.End,
            Flags = MouseFlags.LeftButtonReleased,
            Timestamp = terminal.Time.Now
        });
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single().Viewport.Y > 0));

        var horizontalScroll = await terminal.InvokeAsync(() =>
        {
            var table = DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single();
            return (
                Before: table.Viewport.X,
                Right: table.HorizontalScrollBar.ViewportToScreen(
                    new Point(table.HorizontalScrollBar.Viewport.Width - 1, 0)));
        });
        await terminal.ClickAsync(horizontalScroll.Right);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single().Viewport.X >
                horizontalScroll.Before));

        await terminal.InvokeAsync(() =>
            DashboardFrame("最近日志").SubViews.OfType<ListView>().Single().SetFocus());
        await terminal.InjectAsync(Key.Home);
        Assert.Equal(
            0,
            await terminal.InvokeAsync(() =>
                DashboardFrame("最近日志").SubViews.OfType<ListView>().Single().SelectedItem));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                DashboardFrame("最近日志").SubViews.OfType<ListView>().Single().Viewport.Y == 0));
        Assert.Contains("log-012", await terminal.CaptureScreenAsync());

        bootstrap.SetSettings([("版本", "1.14.4")]);
        bootstrap.SetPluginSummary([new("OnlyPlugin", "1.0.0", "OK", "done")]);
        await terminal.WaitForScreenAsync("OnlyPlugin");
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
            {
                var environment = DashboardFrame("运行环境").SubViews.OfType<TableView>().Single();
                var plugins = DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single();
                return !environment.VerticalScrollBar.Visible &&
                    !environment.HorizontalScrollBar.Visible &&
                    !plugins.VerticalScrollBar.Visible &&
                    !plugins.HorizontalScrollBar.Visible &&
                    environment.Viewport is { X: 0, Y: 0 } &&
                    plugins.Viewport is { X: 0, Y: 0 };
            }));
        var compact = await terminal.InvokeAsync(() =>
        {
            var dashboard = Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single();
            var frames = dashboard.SubViews
                .OfType<FrameView>()
                .ToDictionary(x => x.Title.ToString()!);
            var surface = dashboard.SuperView!;
            return (
                Dashboard: dashboard.Frame,
                Environment: frames["运行环境"].Frame,
                Phase: frames["初始化结果"].Frame,
                Plugins: frames["插件摘要"].Frame,
                Logs: frames["最近日志"].Frame,
                SurfaceViewport: surface.Viewport,
                SurfaceContent: surface.GetContentSize());
        });
        Assert.Equal(narrow.Dashboard, compact.Dashboard);
        Assert.Equal(narrow.Environment, compact.Environment);
        Assert.Equal(narrow.Phase, compact.Phase);
        Assert.Equal(narrow.Plugins, compact.Plugins);
        Assert.Equal(narrow.Logs, compact.Logs);
        Assert.Equal(0, compact.SurfaceViewport.Y);
        Assert.Equal(compact.SurfaceViewport.Size, compact.SurfaceContent);

        bootstrap.SetSettings(longSettings);
        bootstrap.SetPluginSummary(manyPlugins);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
            {
                var environment = DashboardFrame("运行环境").SubViews.OfType<TableView>().Single();
                var plugins = DashboardFrame("插件摘要").SubViews.OfType<TableView>().Single();
                return environment.VerticalScrollBar.Visible &&
                    environment.HorizontalScrollBar.Visible &&
                    plugins.VerticalScrollBar.Visible &&
                    plugins.HorizontalScrollBar.Visible;
            }));

        await terminal.ResizeAsync(120, 36);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                Descendants(terminal.Application.TopRunnableView!)
                    .OfType<BootstrapDashboardView>()
                    .Single()
                    .Frame == new Rectangle(0, 0, 120, 36)));
        var firstWide = await terminal.InvokeAsync(() =>
        {
            var environment = DashboardFrame("运行环境").Frame;
            var phase = DashboardFrame("初始化结果").Frame;
            var plugins = DashboardFrame("插件摘要").Frame;
            var logs = DashboardFrame("最近日志").Frame;
            return (environment, phase, plugins, logs);
        });
        Assert.Equal(0, firstWide.environment.Y);
        Assert.Equal(0, firstWide.phase.Y);
        Assert.Equal(firstWide.environment.Right, firstWide.phase.X);
        Assert.Equal(36, firstWide.logs.Bottom);

        await terminal.ResizeAsync(48, 32);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                Descendants(terminal.Application.TopRunnableView!)
                    .OfType<BootstrapDashboardView>()
                    .Single()
                    .Frame == narrow.Dashboard));
        await terminal.ResizeAsync(120, 36);
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                Descendants(terminal.Application.TopRunnableView!)
                    .OfType<BootstrapDashboardView>()
                    .Single()
                    .Frame == new Rectangle(0, 0, 120, 36)));
        var secondWide = await terminal.InvokeAsync(() => (
            DashboardFrame("运行环境").Frame,
            DashboardFrame("初始化结果").Frame,
            DashboardFrame("插件摘要").Frame,
            DashboardFrame("最近日志").Frame));
        Assert.Equal(firstWide, secondWide);

        FrameView DashboardFrame(string title)
            => Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single()
                .SubViews
                .OfType<FrameView>()
                .Single(x => x.Title.ToString() == title);
    }

    [Fact]
    public async Task BootstrapWorkspace_PersistsUntilAnotherPanelActivatesAndCtrlBReturnsToIt()
    {
        var bootstrap = new BootstrapWorkspace(host);
        var output = host.ForPlugin("Other");
        var other = output.CreateWorkspace("Other");
        output.SetPanel(
            other,
            "main",
            "main",
            LiveDisplayContent.Text("QuietOther"),
            switchToWorkspace: false);

        await StartAsync();
        await terminal.WaitForScreenAsync("运行环境");
        host.SwitchWorkspace(other);
        await terminal.WaitForScreenAsync("QuietOther");
        await terminal.InjectAsync(Key.B.WithCtrl);
        await terminal.WaitForScreenAsync("运行环境");
        Assert.Same(bootstrap.Workspace, host.CurrentWorkspace);

        var disposed = 0;
        await terminal.InvokeAsync(() =>
            Descendants(terminal.Application.TopRunnableView!)
                .OfType<BootstrapDashboardView>()
                .Single()
                .Disposing += (_, _) => disposed++);
        output.SetPanel(other, "main", "main", LiveDisplayContent.Text("ActivatedOther"));
        await terminal.WaitForScreenAsync("ActivatedOther");
        await terminal.WaitForAsync(() => disposed == 1);

        Assert.Same(other, host.CurrentWorkspace);
        Assert.Empty(await terminal.InvokeAsync(() =>
            Descendants(terminal.Application.TopRunnableView!).OfType<BootstrapDashboardView>().ToArray()));
    }

    [Fact]
    public async Task CancellationCleansSessionAndSameApplicationCanRunAgain()
    {
        using var cancellation = new CancellationTokenSource();
        _ = new BootstrapWorkspace(host);
        run = await terminal.StartAsync(host, cancellation.Token);
        await terminal.WaitForScreenAsync("运行环境");
        await terminal.InvokeAsync(() =>
            Descendants(terminal.Application.TopRunnableView!)
                .OfType<ListView>()
                .Single()
                .SetFocus());
        await terminal.InjectAsync(new Key('/'));
        var firstCommandMode = await GetCommandModeAsync();
        await terminal.WaitForAsync(() => firstCommandMode.IsOpen);
        var disposedCommandModes = 0;
        var firstTaskbar = await GetWorkspaceTaskbarAsync();
        var disposedTaskbars = 0;
        var disposedTaskbarTriggers = 0;
        firstCommandMode.Disposing += (_, _) => disposedCommandModes++;
        firstTaskbar.Disposing += (_, _) => disposedTaskbars++;
        firstTaskbar.BottomEdgeTrigger.Disposing += (_, _) => disposedTaskbarTriggers++;

        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(() => !firstCommandMode.IsOpen);
        var firstTaskbarParts = await terminal.InvokeAsync(() => TaskbarParts(firstTaskbar));
        await terminal.MoveMouseAsync(new Point(0, 17));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() => firstTaskbarParts.Popup.Visible));
        var item = await terminal.InvokeAsync(() =>
            firstTaskbarParts.Popup.SubViews.OfType<Shortcut>().Single());
        var itemFrame = await terminal.InvokeAsync(item.FrameToScreen);
        var dragStart = new Point(itemFrame.X + itemFrame.Width / 2, itemFrame.Y);
        await terminal.InjectAsync(MouseAt(dragStart, MouseFlags.LeftButtonPressed));
        await terminal.InjectAsync(MouseAt(
            dragStart with { X = dragStart.X + 2 },
            MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
        Assert.True(await terminal.InvokeAsync(() => terminal.Application.Mouse.IsGrabbed()));

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(terminal.Application.SessionStack!);
        Assert.False(terminal.Application.Mouse.IsGrabbed());
        Assert.Equal(0, workspaceTaskbarSaveCount);
        Assert.Equal(1, disposedCommandModes);
        Assert.Equal(1, disposedTaskbars);
        Assert.Equal(1, disposedTaskbarTriggers);

        var second = CreateHost();
        host = second;
        BindHost(second);
        _ = new BootstrapWorkspace(second);
        run = await terminal.StartAsync(second);
        await terminal.WaitForScreenAsync("初始化结果");
        await terminal.InvokeAsync(() =>
            Descendants(terminal.Application.TopRunnableView!)
                .OfType<ListView>()
                .Single()
                .SetFocus());
        await terminal.InjectAsync(new Key('/'));
        await terminal.WaitForAsync(() =>
            Descendants(terminal.Application.TopRunnableView!)
                .OfType<CommandModeView>()
                .Single()
                .IsOpen);

        var secondSession = await terminal.InvokeAsync(() => (
            CommandModes: Descendants(terminal.Application.TopRunnableView!)
                .OfType<CommandModeView>()
                .Count(),
            Taskbars: Descendants(terminal.Application.TopRunnableView!)
                .OfType<WorkspaceTaskbarView>()
                .Count(),
            TextFields: Descendants(terminal.Application.TopRunnableView!)
                .OfType<CommandModeView>()
                .SelectMany(Descendants)
                .OfType<TextField>()
                .Count()));
        Assert.Equal(1, secondSession.CommandModes);
        Assert.Equal(1, secondSession.Taskbars);
        Assert.Equal(1, secondSession.TextFields);

        await terminal.InjectAsync(Key.Esc);
        Assert.False((await GetCommandModeAsync()).IsOpen);
    }

    [Fact]
    public void NotificationPopup_SummarizesOnlyVisibleOverflow()
    {
        var now = DateTimeOffset.Now;
        var notifications = Enumerable.Range(1, 6)
            .Select(i => new LiveDisplayNotification(
                null,
                $"Plugin-{i}",
                $"Notification-{i}",
                LiveDisplaySeverity.Info,
                now.AddSeconds(10),
                []))
            .ToArray();

        var lines = new NotificationPopupRenderer().BuildLines(
            notifications,
            popupWidth: 40,
            maxHeight: 23,
            now,
            workspace => workspace.Title);

        Assert.Contains(lines, line => line.Contains("Notification-1", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("还有 2 条通知", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(40, line.GetColumns()));
    }

    [Fact]
    public void NotificationPopup_MultilineTextUsesPhysicalRowsWithoutCroppingNextCard()
    {
        var now = DateTimeOffset.Now;
        var notifications = new[]
        {
            new LiveDisplayNotification(
                null,
                "First",
                $"first-line{Environment.NewLine}second-line",
                LiveDisplaySeverity.Info,
                now.AddMinutes(1),
                []),
            new LiveDisplayNotification(
                null,
                "Second",
                "SecondCard",
                LiveDisplaySeverity.Info,
                now.AddMinutes(1),
                [])
        };

        var lines = new NotificationPopupRenderer().BuildLines(
            notifications,
            popupWidth: 40,
            maxHeight: 12,
            now,
            workspace => workspace.Title);
        var firstRow = lines.FindIndex(line => line.Contains("first-line", StringComparison.Ordinal));
        var secondRow = lines.FindIndex(line => line.Contains("second-line", StringComparison.Ordinal));
        var nextCard = lines.FindIndex(line => line.Contains("SecondCard", StringComparison.Ordinal));

        Assert.Equal(firstRow + 1, secondRow);
        Assert.True(secondRow < nextCard);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain('\r', line);
            Assert.DoesNotContain('\n', line);
            Assert.Equal(40, line.GetColumns());
        });
    }

    async Task StartAsync()
    {
        run = await terminal.StartAsync(host);
        await host.Ready.WaitAsync(TimeSpan.FromSeconds(5));
    }

    UiHost CreateHost()
        => new(
            terminal.Application,
            () => workspaceTaskbarOrder,
            titles =>
            {
                workspaceTaskbarOrder.Clear();
                workspaceTaskbarOrder.AddRange(titles);
                workspaceTaskbarSaveCount++;
            });

    void BindHost(
        UiHost value,
        CancellationToken cancellationToken = default,
        bool unbindFirst = false)
        => terminal.RunOnOwnerThread(() =>
        {
            if (unbindFirst)
                LiveDisplayConsole.Unbind(value);
            LiveDisplayConsole.Bind(value, terminal.Application, cancellationToken);
            KeyboardManager.OverlaySink = value;
            RemoveShutdownBinding();
            shutdownTarget = new ShutdownCommandTarget(() =>
            {
                value.RequestShutdown();
                if (terminal.Application.TopRunnableView is not null)
                    terminal.Application.RequestStop();
            });
            terminal.Application.Keyboard.KeyBindings.AddApp(
                Key.C.WithCtrl,
                shutdownTarget,
                Command.Quit);
        });

    void RemoveShutdownBinding()
    {
        terminal.Application.Keyboard.KeyBindings.Remove(Key.C.WithCtrl);
        shutdownTarget?.Dispose();
        shutdownTarget = null;
    }

    static string Lines(int first, int last)
        => string.Join(Environment.NewLine, Enumerable.Range(first, last - first + 1).Select(i => $"line-{i:00}"));

    async Task InjectTextAsync(string text)
    {
        foreach (var character in text)
            await terminal.InjectAsync(new Key(character));
    }

    Task<CommandModeView> GetCommandModeAsync()
        => terminal.InvokeAsync(() => Descendants(terminal.Application.TopRunnableView!)
            .OfType<CommandModeView>()
            .Single());

    Task<WorkspaceTaskbarView> GetWorkspaceTaskbarAsync()
        => terminal.InvokeAsync(() => Descendants(terminal.Application.TopRunnableView!)
            .OfType<WorkspaceTaskbarView>()
            .Single());

    Task<TextField> GetCommandInputAsync(CommandModeView commandMode)
        => terminal.InvokeAsync(() => Descendants(commandMode).OfType<TextField>().Single());

    static (View Popup, View Trigger) TaskbarParts(WorkspaceTaskbarView taskbar)
    {
        var popup = taskbar.SubViews.Single(view => view.BorderStyle == LineStyle.Single);
        return (popup, taskbar.BottomEdgeTrigger);
    }

    Task<string> CaptureViewTextAsync(View view)
        => terminal.InvokeAsync(() =>
        {
            var origin = view.ViewportToScreen(Point.Empty);
            var right = origin.X + view.Viewport.Width;
            var contents = terminal.Application.Driver!.Contents!;
            var graphemes = new List<string>();
            for (var x = origin.X; x < right;)
            {
                var grapheme = contents[origin.Y, x].Grapheme.ToString();
                graphemes.Add(grapheme);
                x += Math.Max(1, grapheme.GetColumns());
            }

            return string.Concat(graphemes).TrimEnd();
        });

    Mouse MouseAt(Point point, MouseFlags flags)
        => new()
        {
            ScreenPosition = point,
            Flags = flags,
            Timestamp = terminal.Time.Now
        };

    static void AssertValidGraphemePrefix(string fullTitle, string shortened)
    {
        var full = fullTitle.ToStringList();
        var visible = shortened.ToStringList();
        Assert.Equal("…", visible[^1]);
        Assert.Equal(full.Take(visible.Count - 1), visible.Take(visible.Count - 1));
    }

    static void AssertValidBootstrapFrame(BootstrapDrawFrame frame)
    {
        var expected = new Rectangle(0, 0, frame.RootViewport.Width, frame.RootViewport.Height);

        Assert.Equal(expected, frame.RootViewport);
        Assert.Equal(expected, frame.SurfaceFrame);
        Assert.Equal(expected, frame.SurfaceViewport);
        Assert.Equal(expected.Size, frame.SurfaceContentSize);
        Assert.Equal(expected, frame.DashboardFrame);
        Assert.Equal(expected, frame.DashboardViewport);
        Assert.Equal(
            0,
            Math.Max(0, frame.SurfaceContentSize.Height - frame.SurfaceViewport.Height));
        Assert.Equal(4, frame.Frames.Count);
        Assert.All(BootstrapFrameTitles, title => Assert.Contains(title, frame.Screen));

        foreach (var bounds in frame.Frames.Values)
        {
            Assert.True(bounds.Width > 0);
            Assert.True(bounds.Height > 0);
            Assert.True(bounds.Left >= 0);
            Assert.True(bounds.Top >= 0);
            Assert.True(bounds.Right <= frame.DashboardViewport.Width);
            Assert.True(bounds.Bottom <= frame.DashboardViewport.Height);
        }

        var values = frame.Frames.Values.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = i + 1; j < values.Length; j++)
            {
                var intersection = Rectangle.Intersect(values[i], values[j]);
                Assert.True(intersection.Width == 0 || intersection.Height == 0);
            }
        }

        var environment = frame.Frames["运行环境"];
        var phase = frame.Frames["初始化结果"];
        var plugins = frame.Frames["插件摘要"];
        var logs = frame.Frames["最近日志"];
        Assert.Equal(frame.DashboardViewport.Height, logs.Bottom);
        if (frame.DashboardViewport.Width >= 96)
        {
            Assert.Equal(0, environment.Y);
            Assert.Equal(0, phase.Y);
            Assert.Equal(environment.Right, phase.X);
            Assert.Equal(environment.Bottom, plugins.Y);
        }
        else
        {
            Assert.Equal(0, environment.Y);
            Assert.Equal(environment.Bottom, phase.Y);
            Assert.Equal(phase.Bottom, plugins.Y);
        }
        Assert.Equal(plugins.Bottom, logs.Y);
    }

    static IEnumerable<View> Descendants(View root)
    {
        foreach (var child in root.SubViews)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    static void ResetUi()
    {
        KeyboardManager.UnregisterAll();
        KeyboardManager.OverlaySink = null;
        KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
        LiveDisplayConsole.UnbindForTests();
    }

    sealed class ShutdownCommandTarget : View
    {
        public ShutdownCommandTarget(Action shutdown)
        {
            AddCommand(Command.Quit, () =>
            {
                shutdown();
                return true;
            });
        }
    }

    sealed record TaskbarFramebufferState(
        bool PopupVisible,
        Rectangle PopupFrame,
        string[] Titles,
        string[] VisibleTexts,
        Rectangle[] ItemFrames,
        MouseState[] MouseStates,
        Terminal.Gui.Drawing.Attribute?[] Attributes,
        TaskbarCellState[] PopupCells);

    sealed record TaskbarCellState(
        string Grapheme,
        Terminal.Gui.Drawing.Attribute? Attribute);

    sealed record TaskbarTransitionMetrics(
        int TitleChanges,
        int TextChanges,
        int PopupLayouts,
        int ItemLayouts,
        int PopupDraws,
        int ItemDraws,
        int FrameChanges,
        int LayoutAndDraws,
        int Iterations,
        TaskbarFramebufferState[] Frames);

    sealed record BootstrapDrawFrame(
        string Screen,
        Rectangle RootViewport,
        Rectangle SurfaceFrame,
        Rectangle SurfaceViewport,
        Size SurfaceContentSize,
        Rectangle DashboardFrame,
        Rectangle DashboardViewport,
        IReadOnlyDictionary<string, Rectangle> Frames);
}
