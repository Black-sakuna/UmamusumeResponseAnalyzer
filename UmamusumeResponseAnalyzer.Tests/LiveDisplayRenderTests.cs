using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.LiveDisplay;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("KeyboardManager")]
public sealed class LiveDisplayRenderTests : IDisposable
{
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
    public void WorkspaceIdentity_IsGlobalCaseInsensitiveAndCapacityIsFixed()
    {
        var first = host.CreateWorkspace(" Telemetry ", historyCapacity: 2);
        var same = host.CreateWorkspace(" telemetry ", historyCapacity: 2);

        Assert.Same(first, same);
        Assert.Equal(" Telemetry ", first.Title);
        Assert.Throws<InvalidOperationException>(() =>
            host.CreateWorkspace(" TELEMETRY ", historyCapacity: 3));
        Assert.Throws<ArgumentException>(() => host.CreateWorkspace("   "));
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
    public async Task History_CapturesWholePanelSetsAndReturnsToLive()
    {
        var output = host.ForPlugin("History");
        var workspace = output.CreateWorkspace("历史", historyCapacity: 2);
        output.SetPanel(workspace, "a", "A", LiveDisplayContent.Text("A-v1"));
        output.CaptureWorkspaceSnapshot(workspace);
        output.SetPanel(workspace, "b", "B", LiveDisplayContent.Text("B-v1"));
        output.CaptureWorkspaceSnapshot(workspace);
        output.SetPanel(workspace, "a", "A", LiveDisplayContent.Text("A-v2"));
        output.SetPanel(workspace, "c", "C", LiveDisplayContent.Text("C-v1"));
        output.CaptureWorkspaceSnapshot(workspace);
        output.SetPanel(workspace, "a", "A", LiveDisplayContent.Text("A-live"));

        await StartAsync();
        await terminal.WaitForScreenAsync("A-live");
        Assert.Equal([["a", "b"], ["a", "b", "c"]], host.GetWorkspaceSnapshotPanelKeysForTests(workspace));

        await terminal.InjectAsync(Key.CursorLeft);
        await terminal.WaitForScreenAsync("A-v2");
        var latestHistory = await terminal.CaptureScreenAsync();
        Assert.Contains("B-v1", latestHistory);
        Assert.Contains("C-v1", latestHistory);
        Assert.DoesNotContain("A-live", latestHistory);

        await terminal.InjectAsync(Key.CursorLeft);
        await terminal.WaitForScreenAsync("A-v1");
        Assert.DoesNotContain("C-v1", await terminal.CaptureScreenAsync());

        await terminal.InjectAsync(Key.CursorRight);
        await terminal.WaitForScreenAsync("A-v2");
        await terminal.InjectAsync(Key.CursorRight);
        await terminal.WaitForScreenAsync("A-live");
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
    public async Task CancellationCleansSessionAndSameApplicationCanRunAgain()
    {
        using var cancellation = new CancellationTokenSource();
        host.SetPanel(new LiveDisplayPanel(
            host.CreateWorkspace("First"),
            "Host",
            "main",
            "First",
            LiveDisplayContent.Text("FirstRun"),
            DateTimeOffset.Now,
            FullBleed: true));
        run = await terminal.StartAsync(host, cancellation.Token);
        await terminal.WaitForScreenAsync("FirstRun");

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(terminal.Application.SessionStack!);

        var second = new UiHost(terminal.Application);
        host = second;
        BindHost(second);
        second.SetPanel(new LiveDisplayPanel(
            second.CreateWorkspace("Second"),
            "Host",
            "main",
            "Second",
            LiveDisplayContent.Text("SecondRun"),
            DateTimeOffset.Now,
            FullBleed: true));
        run = await terminal.StartAsync(second);
        await terminal.WaitForScreenAsync("SecondRun");
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
        await terminal.WaitForScreenAsync("UmamusumeResponseAnalyzer");
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
}
