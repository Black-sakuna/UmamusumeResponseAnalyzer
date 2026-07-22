using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.LiveDisplay;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("KeyboardManager")]
public sealed class LiveDisplayRenderTests : IDisposable
{
    public LiveDisplayRenderTests() => ResetUi();

    public void Dispose() => ResetUi();

    [Fact]
    public void LiveDisplayContent_CreatesFreshTerminalGuiViews()
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
    }

    [Fact]
    public void LiveDisplayContent_NullFactoryResultFailsFast()
    {
        var content = new LiveDisplayContent(() => null!);

        var error = Assert.Throws<InvalidOperationException>(content.CreateView);

        Assert.Contains("null", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstPanelActivatesWorkspaceAndRendersTerminalGuiContent()
    {
        var host = new UiHost();
        var output = host.ForPlugin("Telemetry");
        var workspace = output.CreateWorkspace("动态");

        output.SetPanel(workspace, "main", "状态", LiveDisplayContent.Text("DynamicBody"), fullBleed: true);
        var snapshot = host.RenderSnapshotForTests();

        Assert.Equal(workspace, host.CurrentWorkspace);
        Assert.Contains("DynamicBody", snapshot);
        Assert.Contains("Telemetry - 状态", snapshot);
    }

    [Fact]
    public void QuietPanelUpdateDoesNotSwitchWorkspace()
    {
        var host = new UiHost();
        var output = host.ForPlugin("Telemetry");
        var first = output.CreateWorkspace("第一");
        var second = output.CreateWorkspace("第二");
        output.SetPanel(first, "main", "第一", LiveDisplayContent.Text("FirstBody"));
        output.SetPanel(second, "main", "第二", LiveDisplayContent.Text("SecondBody"));
        output.SwitchWorkspace(first);
        host.RenderSnapshotForTests();

        output.SetPanel(
            second,
            "main",
            "第二",
            LiveDisplayContent.Text("QuietUpdate"),
            switchToWorkspace: false);
        var firstSnapshot = host.RenderSnapshotForTests();

        Assert.Equal(first, host.CurrentWorkspace);
        Assert.Contains("FirstBody", firstSnapshot);
        Assert.DoesNotContain("QuietUpdate", firstSnapshot);

        output.SwitchWorkspace(second);
        Assert.Contains("QuietUpdate", host.RenderSnapshotForTests());
    }

    [Fact]
    public void LatestFullBleedPanelOwnsWorkspaceBody()
    {
        var host = new UiHost();
        var workspace = host.CreateWorkspace("全屏");
        host.SetPanel(new LiveDisplayPanel(
            workspace,
            "Plugin",
            "normal",
            "Normal",
            LiveDisplayContent.Text("NormalBody"),
            DateTimeOffset.Now));
        host.SetPanel(new LiveDisplayPanel(
            workspace,
            "Plugin",
            "full",
            "Full",
            LiveDisplayContent.Text("FullBody"),
            DateTimeOffset.Now.AddMilliseconds(1),
            FullBleed: true));

        var snapshot = host.RenderSnapshotForTests();

        Assert.Contains("FullBody", snapshot);
        Assert.DoesNotContain("NormalBody", snapshot);
    }

    [Fact]
    public void WorkspaceIdentityIsCaseInsensitiveAndCapacityIsFixed()
    {
        var host = new UiHost();

        var first = host.CreateWorkspace("Telemetry", historyCapacity: 2);
        var same = host.CreateWorkspace("telemetry", historyCapacity: 2);

        Assert.Same(first, same);
        Assert.Throws<InvalidOperationException>(() => host.CreateWorkspace("TELEMETRY", historyCapacity: 3));
        Assert.Throws<ArgumentException>(() => host.CreateWorkspace("   "));
    }

    [Fact]
    public async Task HistoryCapturesPanelSetsAndBrowsesBackToLive()
    {
        var host = new UiHost();
        var output = host.ForPlugin("History");
        var workspace = output.CreateWorkspace("历史", historyCapacity: 2);
        output.SetPanel(workspace, "a", "A", LiveDisplayContent.Text("A-v1"));
        host.RenderSnapshotForTests();
        output.CaptureWorkspaceSnapshot(workspace);
        host.RenderSnapshotForTests();
        output.SetPanel(workspace, "b", "B", LiveDisplayContent.Text("B-v1"));
        host.RenderSnapshotForTests();
        output.CaptureWorkspaceSnapshot(workspace);
        host.RenderSnapshotForTests();
        output.SetPanel(workspace, "a", "A", LiveDisplayContent.Text("A-live"));
        host.RenderSnapshotForTests();

        Assert.Equal([["a"], ["a", "b"]], host.GetWorkspaceSnapshotPanelKeysForTests(workspace));
        Assert.True(await NavigateAsync(host, ConsoleKey.LeftArrow));
        var history = host.RenderSnapshotForTests();
        Assert.Contains("A-v1", history);
        Assert.Contains("B-v1", history);
        Assert.DoesNotContain("A-live", history);

        Assert.True(await NavigateAsync(host, ConsoleKey.RightArrow));
        Assert.Contains("A-live", host.RenderSnapshotForTests());
    }

    [Fact]
    public async Task ViewportUsesWorkspaceOnlyScrollingAndClampsBoundaries()
    {
        var host = new UiHost();
        var output = host.ForPlugin("Viewport");
        var workspace = output.CreateWorkspace("长页面");
        var longText = string.Join(Environment.NewLine, Enumerable.Range(1, 80).Select(x => $"line-{x}"));
        output.SetPanel(workspace, "main", "长页面", LiveDisplayContent.Text(longText), fullBleed: true);
        host.RenderSnapshotForTests(width: 80, height: 10);

        Assert.True(await NavigateAsync(host, ConsoleKey.UpArrow));
        Assert.True(await NavigateAsync(host, ConsoleKey.Home));
        Assert.False(await NavigateAsync(host, ConsoleKey.UpArrow));
        Assert.True(await NavigateAsync(host, ConsoleKey.End));
        Assert.False(await NavigateAsync(host, ConsoleKey.DownArrow));

        var other = output.CreateWorkspace("短页面");
        output.SetPanel(other, "main", "短页面", LiveDisplayContent.Text("short"));
        host.RenderSnapshotForTests(width: 80, height: 10);
        Assert.False(await NavigateAsync(host, ConsoleKey.UpArrow));
    }

    [Fact]
    public void LogsAndNotificationsAreScopedAndNeverSwitchWorkspace()
    {
        var host = new UiHost();
        var output = host.ForPlugin("Scopes");
        var first = output.CreateWorkspace("第一");
        var second = output.CreateWorkspace("第二");
        output.SetPanel(first, "main", "第一", LiveDisplayContent.Text("FirstBody"));
        output.SetPanel(second, "main", "第二", LiveDisplayContent.Text("SecondBody"));
        output.SwitchWorkspace(first);
        host.RenderSnapshotForTests();

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
        var snapshot = host.RenderSnapshotForTests();

        Assert.Equal(first, host.CurrentWorkspace);
        Assert.Contains("GlobalLog", snapshot);
        Assert.DoesNotContain("SecondLog", snapshot);
        Assert.Single(host.GetNotificationsForTests(second));
        Assert.Single(host.GetNotificationsForTests(null));
    }

    [Fact]
    public void NotificationPopupRendersCardsAndSummarizesOverflow()
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
        Assert.Contains(lines, line => line.Contains("Notification-4", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("还有 2 条通知", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.Equal(40, line.GetColumns()));
    }

    [Fact]
    public void LogBanksKeepLatestThreeHundredPerScope()
    {
        var host = new UiHost();
        var workspace = host.CreateWorkspace("日志");
        for (var i = 0; i < 320; i++)
        {
            host.Log(new LiveDisplayLogLine(null, "Host", $"global-{i}", LiveDisplaySeverity.Info));
            host.Log(new LiveDisplayLogLine(workspace, "Plugin", $"workspace-{i}", LiveDisplaySeverity.Info));
        }
        host.RenderSnapshotForTests();

        Assert.Equal(300, host.GetLogsForTests(null).Count);
        Assert.Equal("global-20", host.GetLogsForTests(null)[0].Text);
        Assert.Equal(300, host.GetLogsForTests(workspace).Count);
        Assert.Equal("workspace-20", host.GetLogsForTests(workspace)[0].Text);
    }

    [Fact]
    public void RemovingWorkspaceClearsOnlyItsPanelsTelemetryAndNavigationState()
    {
        var host = new UiHost();
        var output = host.ForPlugin("Removal");
        var first = output.CreateWorkspace("第一");
        var second = output.CreateWorkspace("第二");
        output.SetPanel(first, "main", "第一", LiveDisplayContent.Text("FirstBody"));
        output.Log(first, "FirstLog");
        output.Notify(first, "FirstNotify", ttl: TimeSpan.FromMinutes(1));
        output.SetPanel(second, "main", "第二", LiveDisplayContent.Text("SecondBody"));
        output.Log(second, "SecondLog");
        host.RenderSnapshotForTests();

        output.RemoveWorkspace(first);
        var snapshot = host.RenderSnapshotForTests();

        Assert.Equal(second, host.CurrentWorkspace);
        Assert.Empty(host.GetLogsForTests(first));
        Assert.Empty(host.GetNotificationsForTests(first));
        Assert.Contains("SecondBody", snapshot);
        Assert.Contains("SecondLog", snapshot);
    }

    [Fact]
    public void BootstrapIsRemovedOnlyBySwitchingPanelOutput()
    {
        var host = new UiHost();
        var bootstrap = new BootstrapWorkspace(host);
        var output = host.ForPlugin("Plugin");
        var workspace = output.CreateWorkspace("插件");
        host.RenderSnapshotForTests();

        output.SetPanel(
            workspace,
            "main",
            "插件",
            LiveDisplayContent.Text("quiet"),
            switchToWorkspace: false);
        Assert.Contains("启动状态", host.RenderSnapshotForTests());

        output.SetPanel(workspace, "main", "插件", LiveDisplayContent.Text("active"));
        var snapshot = host.RenderSnapshotForTests();

        Assert.Equal(workspace, host.CurrentWorkspace);
        Assert.Contains("active", snapshot);
        Assert.Empty(host.GetLogsForTests(bootstrap.Workspace));
    }

    [Fact]
    public async Task WorkspaceCommandSwitchesCaseInsensitivelyAndReportsUnknownWorkspace()
    {
        var host = new UiHost();
        var first = host.CreateWorkspace("First");
        var second = host.CreateWorkspace("Second Workspace");
        host.SwitchWorkspace(first);
        host.RenderSnapshotForTests();

        await host.HandleCommandAsync("/workspace switch \"second workspace\"");
        Assert.Equal(second, host.CurrentWorkspace);

        await host.HandleCommandAsync("/workspace switch Missing");
        Assert.Contains(
            host.GetLogsForTests(null),
            line => line.Text.Contains("workspace 不存在: Missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HeadlessLifecycleProcessesConsoleInteractionAndStopsCleanly()
    {
        var host = new UiHost();
        UiHost.HasInteractiveConsoleOverrideForTests = false;
        LiveDisplayConsole.Bind(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = host.RunAsync(timeout.Token);
        while (!host.IsRunning)
            await Task.Delay(10, timeout.Token);

        var interactionRan = false;
        await LiveDisplayConsole.RunAsync(() =>
        {
            interactionRan = true;
            return Task.CompletedTask;
        }).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        host.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.True(interactionRan);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void PublicOutputRejectsWorkspaceLessAndNullScopedCalls()
    {
        var host = new UiHost();
        var output = host.ForPlugin("Boundary");

        Assert.Throws<InvalidOperationException>(() => output.Log("no workspace"));
        Assert.Throws<InvalidOperationException>(() => output.Notify("no workspace"));
        Assert.Throws<ArgumentNullException>(() => output.SetPanel(null!, "main", "title", LiveDisplayContent.Text("body")));
        Assert.Throws<ArgumentNullException>(() => output.Log(null!, "text"));
        Assert.Throws<ArgumentNullException>(() => output.Notify(null!, "text"));
    }

    static Task<bool> NavigateAsync(UiHost host, ConsoleKey key)
        => ((IKeyboardOverlaySink)host).TryHandleWorkspaceKeyAsync(new ConsoleKeyInfo('\0', key, false, false, false));

    static void ResetUi()
    {
        using (KeyboardManager.SuspendInput())
        {
        }

        KeyboardManager.UnregisterAll();
        KeyboardManager.SetCommandHandler(null);
        KeyboardManager.OverlaySink = null;
        KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = null;
        KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
        UiHost.HasInteractiveConsoleOverrideForTests = null;
        UiHost.ClearConsoleOverrideForTests = null;
        UiHost.RunLiveDisplayUntilConsoleInteractionOverrideForTests = null;
        LiveDisplayConsole.UnbindForTests();
    }
}
