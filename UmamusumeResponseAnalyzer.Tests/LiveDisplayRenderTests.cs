using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
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
    UiHost host;
    Task? run;
    ShutdownCommandTarget? shutdownTarget;

    public LiveDisplayRenderTests()
    {
        terminal = new(width: 80, height: 18);
        host = new UiHost(terminal.Application);
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
            () => terminal.Application.TopRunnableView!.SubViews.First().SubViews.Single().Viewport.Y);

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
            () => terminal.Application.TopRunnableView!.SubViews.First().SubViews.Single().Viewport.Y);

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
    public async Task CommandInput_IsARealOverlayAndDoesNotReplaceWorkspaceLayout()
    {
        var output = host.ForPlugin("Command");
        var workspace = output.CreateWorkspace("Command");
        output.SetPanel(workspace, "main", "main", LiveDisplayContent.Text("UnderlyingBody"), fullBleed: true);

        await StartAsync();
        await terminal.InjectAsync(Key.Enter);
        await terminal.InjectAsync(Key.A);
        await terminal.WaitForScreenAsync("Command Mode");
        var screen = await terminal.CaptureScreenAsync();

        Assert.Contains("UnderlyingBody", screen);
        Assert.Contains("Command Mode", screen);
        Assert.Contains("a", screen, StringComparison.OrdinalIgnoreCase);

        await terminal.InjectAsync(Key.Esc);
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains("Command Mode", StringComparison.Ordinal));
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

        await KeyboardManager.HandleKeyAsync(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        await terminal.WaitForScreenAsync("Command Mode");
        await terminal.ClickAsync(click);
        Assert.Equal(4, accepted);
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
        await KeyboardManager.HandleKeyAsync(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false));
        await terminal.WaitForScreenAsync("Command Mode");
        await terminal.ClickAsync(bottomPoint);
        Assert.Equal(2, bottomHits);
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

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(terminal.Application.SessionStack!);

        var second = new UiHost(terminal.Application);
        host = second;
        BindHost(second);
        _ = new BootstrapWorkspace(second);
        run = await terminal.StartAsync(second);
        await terminal.WaitForScreenAsync("初始化结果");
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
            KeyboardManager.SetCommandHandler(value.HandleCommandAsync, value.CompleteCommand);
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
        KeyboardManager.SetCommandHandler(null);
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
