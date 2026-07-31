using System.Drawing;
using System.Reflection;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("PluginReload")]
public sealed class UiHostRenderTests : IDisposable
{
    readonly TerminalGuiTestApp terminal;
    readonly UiHost host;
    readonly List<Workspace> ownedWorkspaces = [];

    public UiHostRenderTests(PluginRuntimeFixture fixture)
    {
        terminal = fixture.Terminal;
        host = fixture.Host;
        HotkeyManager.OverlaySink = host;
    }

    public void Dispose()
    {
        TerminalUi.DefaultExceptionWorkspace = null;
        foreach (var workspace in ownedWorkspaces.AsEnumerable().Reverse())
        {
            host.RemoveWorkspace(workspace);
        }
        HotkeyManager.UnregisterAll();
        host.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    [Fact]
    public void WorkspaceContent_CreatesFreshViewsAndRejectsNull()
    {
        var created = 0;
        var content = new WorkspaceContent(() =>
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
            new WorkspaceContent(() => null!).CreateView);
    }

    [Fact]
    public async Task WorkspaceIdentity_IsCanonicalCaseInsensitiveAndTombstonedByReference()
    {
        var title = $"Canonical-{Guid.NewGuid():N}";
        var first = Own(host.CreateWorkspace(title));
        var same = host.CreateWorkspace(title.ToUpperInvariant());

        Assert.Same(first, same);
        Assert.Equal(title, first.Title);

        host.RemoveWorkspace(first);
        Assert.Throws<InvalidOperationException>(() => host.SetPanel(
            first,
            "late",
            "late",
            WorkspaceContent.Text("late"),
            fullBleed: true,
            switchToWorkspace: true));

        var replacement = Own(host.CreateWorkspace(title.ToLowerInvariant()));
        Assert.NotSame(first, replacement);
        Assert.Same(replacement, host.GetCurrentWorkspace());
        await host.FlushAsync();
    }

    [Fact]
    public async Task Flush_CompletesAfterTheAcceptedPanelIsReconciled()
    {
        var workspace = CreateWorkspace("Flush workspace");
        host.SetPanel(
            workspace,
            "main",
            "main",
            WorkspaceContent.Text("FlushVisible"),
            fullBleed: true,
            switchToWorkspace: true);

        await host.FlushAsync();

        await terminal.RedrawAsync();
        var screen = await terminal.CaptureScreenAsync();
        Assert.Contains("FlushVisible", screen, StringComparison.Ordinal);
        Assert.StartsWith("FlushVisible", screen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PanelReplacementAndRemoval_PreservePublicReturnsAndFinalFramebuffer()
    {
        var workspace = CreateWorkspace("Panel workspace");
        host.SetPanel(
            workspace,
            "main",
            "main",
            WorkspaceContent.Text("FirstView"),
            fullBleed: true,
            switchToWorkspace: true);
        await host.FlushAsync();
        await terminal.WaitForScreenAsync("FirstView");

        host.SetPanel(
            workspace,
            "main",
            "main",
            WorkspaceContent.Text("SecondView"),
            fullBleed: true,
            switchToWorkspace: true);
        await host.FlushAsync();

        await terminal.WaitForScreenAsync("SecondView");
        Assert.DoesNotContain(
            "FirstView",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);
        Assert.True(host.RemovePanel(workspace, "main"));
        Assert.False(host.RemovePanel(workspace, "main"));
        await host.FlushAsync();
        await terminal.RedrawAsync();
        Assert.DoesNotContain(
            "SecondView",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Taskbar_HoverClickDragAndResizePreserveCanonicalWorkspace()
    {
        await terminal.ResizeAsync(80, 18);
        var first = CreateWorkspace("Taskbar First");
        var second = CreateWorkspace("Taskbar Second");
        host.SetPanel(
            first,
            "main",
            "main",
            WorkspaceContent.Text("FirstWorkspaceBody"),
            fullBleed: true,
            switchToWorkspace: true);
        host.SetPanel(
            second,
            "main",
            "main",
            WorkspaceContent.Text("SecondWorkspaceBody"),
            fullBleed: true,
            switchToWorkspace: false);
        await host.FlushAsync();

        await terminal.MoveMouseAsync(new Point(0, 17));
        var secondPoint = await WaitForTaskbarItemAsync(second.Title, first.Title);
        await terminal.ClickAsync(secondPoint);

        await terminal.WaitForScreenAsync("SecondWorkspaceBody");
        Assert.Same(second, host.GetCurrentWorkspace());

        await terminal.MoveMouseAsync(Point.Empty);
        await terminal.MoveMouseAsync(new Point(0, 17));
        var firstPoint = await WaitForTaskbarItemAsync(first.Title, second.Title);
        secondPoint = await WaitForTaskbarItemAsync(second.Title, first.Title);
        var dragTarget = firstPoint with
        {
            X = Math.Max(0, firstPoint.X - first.Title.Length / 2)
        };
        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = secondPoint,
            Flags = MouseFlags.LeftButtonPressed,
            Timestamp = terminal.Time.Now
        });
        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = dragTarget,
            Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport,
            Timestamp = terminal.Time.Now
        });
        await terminal.InjectAsync(new Mouse
        {
            ScreenPosition = dragTarget,
            Flags = MouseFlags.LeftButtonReleased,
            Timestamp = terminal.Time.Now
        });
        await terminal.WaitForAsync(async () =>
        {
            var line = (await terminal.CaptureScreenAsync())
                .ReplaceLineEndings("\n")
                .Split('\n')
                .FirstOrDefault(candidate =>
                    candidate.Contains(first.Title, StringComparison.Ordinal) &&
                    candidate.Contains(second.Title, StringComparison.Ordinal));
            return line is not null &&
                   line.IndexOf(second.Title, StringComparison.Ordinal) <
                   line.IndexOf(first.Title, StringComparison.Ordinal);
        });

        await terminal.ResizeAsync(100, 24);
        await terminal.RedrawAsync();
        await terminal.MoveMouseAsync(new Point(0, 23));
        await WaitForTaskbarItemAsync(second.Title, first.Title);
        Assert.Contains(
            "SecondWorkspaceBody",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Viewport_NativeHomeAndEndPreserveBottomAnchoring()
    {
        var workspace = CreateWorkspace("Scroll workspace");
        host.SetPanel(
            workspace,
            "main",
            "main",
            WorkspaceContent.Text(string.Join(
                Environment.NewLine,
                Enumerable.Range(1, 40).Select(value => $"line-{value:00}"))),
            fullBleed: true,
            switchToWorkspace: true);
        await host.FlushAsync();
        await terminal.RedrawAsync();

        Assert.Contains(
            "line-40",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);

        await terminal.InjectAsync(Key.Home);
        await terminal.WaitForScreenAsync("line-01");
        await terminal.InjectAsync(Key.End);
        await terminal.WaitForScreenAsync("line-40");
    }

    [Fact]
    public async Task Notifications_FilterByWorkspaceAndKeepShortcutLifetime()
    {
        var first = CreateWorkspace("Notification First");
        var second = CreateWorkspace("Notification Second");
        host.SetPanel(
            first,
            "main",
            "main",
            WorkspaceContent.Text("NotificationFirstBody"),
            fullBleed: true,
            switchToWorkspace: true);
        host.SetPanel(
            second,
            "main",
            "main",
            WorkspaceContent.Text("NotificationSecondBody"),
            fullBleed: true,
            switchToWorkspace: false);
        var shortcutHits = 0;
        host.Notify(
            second,
            "SecondWorkspaceNotification",
            UiSeverity.Warning,
            TimeSpan.FromMinutes(1),
            []);
        host.Notify(
            first,
            "FirstWorkspaceNotification",
            UiSeverity.Info,
            TimeSpan.FromMinutes(1),
            [new UiShortcut(ConsoleKey.F8, () =>
            {
                Interlocked.Increment(ref shortcutHits);
                return Task.CompletedTask;
            })]);
        await host.FlushAsync();

        await terminal.WaitForScreenAsync("FirstWorkspaceNotification");
        Assert.DoesNotContain(
            "SecondWorkspaceNotification",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);
        await terminal.InjectAsync(Key.F8);
        await terminal.WaitForAsync(() => Volatile.Read(ref shortcutHits) == 1);

        host.SwitchWorkspace(second);
        await host.FlushAsync();
        await terminal.WaitForScreenAsync("SecondWorkspaceNotification");
        Assert.DoesNotContain(
            "FirstWorkspaceNotification",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notification_TtlRemovesFramebufferAndShortcut()
    {
        var workspace = CreateWorkspace("TTL workspace");
        host.SetPanel(
            workspace,
            "main",
            "main",
            WorkspaceContent.Text("TtlBody"),
            fullBleed: true,
            switchToWorkspace: true);
        var hits = 0;
        host.Notify(
            workspace,
            "ExpiringNotification",
            UiSeverity.Info,
            TimeSpan.FromSeconds(2),
            [new UiShortcut(ConsoleKey.F8, () =>
            {
                Interlocked.Increment(ref hits);
                return Task.CompletedTask;
            })]);
        await host.FlushAsync();
        await terminal.WaitForScreenAsync("ExpiringNotification");

        await terminal.InjectAsync(Key.F8);
        await terminal.WaitForAsync(() => Volatile.Read(ref hits) == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(2100), TestContext.Current.CancellationToken);
        terminal.Time.Advance(TimeSpan.FromSeconds(1));
        await terminal.WaitForAsync(async () =>
            !(await terminal.CaptureScreenAsync()).Contains(
                "ExpiringNotification",
                StringComparison.Ordinal));
        await terminal.InjectAsync(Key.F8);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref hits));
        Assert.Contains(
            "TtlBody",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceCommand_CompletesAndSelectsCanonicalWorkspace()
    {
        var first = CreateWorkspace("Command First");
        var second = CreateWorkspace("Command Second");
        first.BindHotkey(ConsoleKey.D1, ConsoleModifiers.Control);
        host.SetPanel(
            first,
            "main",
            "main",
            WorkspaceContent.Text("CommandFirstBody"),
            fullBleed: true,
            switchToWorkspace: true);
        host.SetPanel(
            second,
            "main",
            "main",
            WorkspaceContent.Text("CommandSecondBody"),
            fullBleed: true,
            switchToWorkspace: false);
        await host.FlushAsync();

        Assert.Contains(
            "/workspace switch \"Command Second\"",
            host.CompleteCommand("/workspace switch Command S"));
        await host.HandleCommandAsync("/workspace");
        await host.FlushAsync();
        await terminal.WaitForScreenAsync("Workspaces");

        await terminal.InjectAsync(Key.CursorDown);
        await terminal.InjectAsync(Key.Enter);
        await host.FlushAsync();
        await terminal.WaitForScreenAsync("CommandSecondBody");
        Assert.Same(second, host.GetCurrentWorkspace());
    }

    [Fact]
    public async Task CommandMode_SubmitAppliesFinalVisibleResult()
    {
        var first = CreateWorkspace("Input First");
        var second = CreateWorkspace("Input Second");
        Button? focusTarget = null;
        host.SetPanel(
            first,
            "main",
            "main",
            new WorkspaceContent(() =>
            {
                focusTarget = new Button { Text = "CommandFocus" };
                return focusTarget;
            }),
            fullBleed: true,
            switchToWorkspace: true);
        host.SetPanel(
            second,
            "main",
            "main",
            WorkspaceContent.Text("CommandInputResult"),
            fullBleed: true,
            switchToWorkspace: false);
        await host.FlushAsync();
        await terminal.WaitForScreenAsync("CommandFocus");
        await terminal.InvokeAsync(() => focusTarget!.SetFocus());

        await terminal.InjectAsync(new Key('/'));
        await terminal.WaitForScreenAsync("Command Mode");
        await InjectTextAsync("workspace switch Input Second");
        await terminal.InjectAsync(Key.Enter);

        await terminal.WaitForScreenAsync("CommandInputResult");
        Assert.Same(second, host.GetCurrentWorkspace());
        Assert.DoesNotContain(
            "Command Mode",
            await terminal.CaptureScreenAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapGlobalAndFailureLogsRemainScopedUntilWorkspaceSwitch()
    {
        var bootstrap = new BootstrapWorkspace(host);
        Own(bootstrap.Workspace);
        try
        {
            bootstrap.Log("URA", "BootstrapLog", UiSeverity.Info);
            await host.FlushAsync();
            await terminal.WaitForScreenAsync("BootstrapLog");

            var other = CreateWorkspace("Bootstrap Other");
            host.SetPanel(
                other,
                "main",
                "main",
                WorkspaceContent.Text("OtherWorkspaceBody"),
                fullBleed: true,
                switchToWorkspace: true);
            TerminalUi.Log("ABI", "global-log-sentinel", UiSeverity.Warning);
            other.Log("other-workspace-log-sentinel", UiSeverity.Warning);
            TerminalUi.LogException(
                "URA",
                new InvalidOperationException("bootstrap-error-sentinel"));
            await host.HandleCommandAsync("/workspace switch \"unterminated");
            await host.FlushAsync();
            await terminal.WaitForScreenAsync("OtherWorkspaceBody");
            var otherScreen = await terminal.CaptureScreenAsync();
            Assert.DoesNotContain(
                "bootstrap-error-sentinel",
                otherScreen,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "global-log-sentinel",
                otherScreen,
                StringComparison.Ordinal);

            bootstrap.Workspace.SwitchTo();
            await host.FlushAsync();
            await terminal.WaitForScreenAsync("bootstrap-error-sentinel");
            await terminal.WaitForScreenAsync("global-log-sentinel");
            var bootstrapScreen = await terminal.CaptureScreenAsync();
            Assert.Contains(
                "Quoted workspace title 缺少结束双引号。",
                bootstrapScreen,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "other-workspace-log-sentinel",
                bootstrapScreen,
                StringComparison.Ordinal);
        }
        finally
        {
            bootstrap.Dispose();
        }

        Assert.Null(TerminalUi.DefaultExceptionWorkspace);
    }

    Workspace CreateWorkspace(string title)
        => Own(host.CreateWorkspace(title));

    Workspace Own(Workspace workspace)
    {
        ownedWorkspaces.Add(workspace);
        return workspace;
    }

    async Task<Point> WaitForTaskbarItemAsync(string title, string otherTitle)
    {
        Point? point = null;
        await terminal.WaitForAsync(async () =>
        {
            var lines = (await terminal.CaptureScreenAsync())
                .ReplaceLineEndings("\n")
                .Split('\n');
            for (var y = 0; y < lines.Length; y++)
            {
                if (!lines[y].Contains(otherTitle, StringComparison.Ordinal))
                    continue;

                var x = lines[y].IndexOf(title, StringComparison.Ordinal);
                if (x < 0)
                    continue;

                point = new(x + title.Length / 2, y);
                return true;
            }
            return false;
        });
        return point!.Value;
    }

    async Task InjectTextAsync(string text)
    {
        foreach (var character in text)
            await terminal.InjectAsync(new Key(character));
    }

}

public sealed class UiHostShutdownProcessTests
{
    [Fact]
    public Task PluginDisposeAndFlushCompleteBeforeHostStops()
        => RunScenarioAsync(
            "plugin-before-host-stop",
            nameof(PluginDisposeAndFlushCompleteBeforeHostStops),
            RunPluginShutdownAsync);

    [Fact]
    public Task AdmissionFencePrecedesBlockingOverlayDetach()
        => RunScenarioAsync(
            "shutdown-admission-fence",
            nameof(AdmissionFencePrecedesBlockingOverlayDetach),
            RunAdmissionFenceAsync);

    [Fact]
    public Task ShutdownHandshakeDoesNotDependOnPostOrder()
        => RunScenarioAsync(
            "shutdown-worker-before-drain",
            nameof(ShutdownHandshakeDoesNotDependOnPostOrder),
            RunWorkerBeforeDrainAsync);

    [Fact]
    public Task WindowQuitCancelsLifetimeBeforeWaitingForPluginLease()
        => RunScenarioAsync(
            "window-quit-cancels-lifetime",
            nameof(WindowQuitCancelsLifetimeBeforeWaitingForPluginLease),
            RunWindowQuitAsync);

    [Fact]
    public Task PluginDisposeObservesCancelledLifetimeWithoutActiveLease()
        => RunScenarioAsync(
            "plugin-dispose-sees-cancelled-lifetime",
            nameof(PluginDisposeObservesCancelledLifetimeWithoutActiveLease),
            RunDisposeAfterStartedAsync);

    [Fact]
    public Task CreateWindowFailureAbandonsPreRunIngress()
        => RunScenarioAsync(
            "create-window-failure",
            nameof(CreateWindowFailureAbandonsPreRunIngress),
            RunCreateWindowFailureAsync);

    [Fact]
    public Task PrimaryAndShutdownFailuresAreAggregated()
        => RunScenarioAsync(
            "primary-and-shutdown-failure",
            nameof(PrimaryAndShutdownFailuresAreAggregated),
            RunAggregatedFailureAsync);

    static async Task RunScenarioAsync(
        string scenario,
        string methodName,
        Func<Task> childAction)
    {
        if (TerminalUiLifecycleChildProcess.IsChild(scenario))
        {
            await childAction();
            Assert.Contains(
                "stopped",
                Assert.Throws<InvalidOperationException>(() =>
                    TerminalUi.Log("shutdown-test", "late")).Message,
                StringComparison.Ordinal);
            TerminalUiLifecycleChildProcess.WriteResult("ok");
            return;
        }

        Assert.Equal(
            "ok",
            await TerminalUiLifecycleProcessTests.RunChildAsync(
                scenario,
                typeof(UiHostShutdownProcessTests),
                methodName));
    }

    static async Task RunPluginShutdownAsync()
    {
        using var terminal = new TerminalGuiTestApp();
        var host = TerminalUiLifecycleChildProcess.InitializeHost(terminal, CancellationToken.None);
        HotkeyManager.OverlaySink = host;
        var run = await terminal.StartAsync(host);
        var workspace = Workspace.Create("Shutdown plugin");
        var view = new ShutdownProbeView();
        host.SetPanel(
            workspace,
            "probe",
            "probe",
            new WorkspaceContent(() => view),
            fullBleed: true,
            switchToWorkspace: true);
        await host.FlushAsync();

        var plugin = new ShutdownWorkspacePlugin(workspace);
        PluginManager.LoadedPlugins.Add(plugin);
        PluginManager.InitializePlugin(plugin);

        host.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(plugin.PanelRemoved);
        Assert.True(view.DetachedAndDisposedWhileHostAccepted);
    }

    static async Task RunAdmissionFenceAsync()
    {
        using var terminal = new TerminalGuiTestApp();
        var host = TerminalUiLifecycleChildProcess.InitializeHost(terminal, CancellationToken.None);
        HotkeyManager.OverlaySink = host;
        var workspace = Workspace.Create("Shutdown fence");
        var navigation = HotkeyManager.HandleMouseWheelAsync(1, hasModifiers: false);
        Assert.False(navigation.IsCompleted);

        host.RequestShutdown();
        await terminal.WaitForAsync(() => HotkeyManager.OverlaySink is null);
        Assert.False(navigation.IsCompleted);
        Assert.Contains(
            "stopping",
            Assert.Throws<InvalidOperationException>(() => workspace.SetPanel(
                "late",
                "late",
                WorkspaceContent.Text("late"))).Message,
            StringComparison.Ordinal);

        var run = await terminal.StartAsync(host);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await navigation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    static async Task RunWorkerBeforeDrainAsync()
    {
        using var terminal = new TerminalGuiTestApp();
        var (host, ownerContext) = InitializeHostWithObservedPosts(terminal);
        HotkeyManager.OverlaySink = host;

        host.RequestShutdown();
        await ownerContext.FirstPostCompleted.WaitAsync(TimeSpan.FromSeconds(5));

        var run = await terminal.StartAsync(host);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    static async Task RunWindowQuitAsync()
    {
        using var terminal = new TerminalGuiTestApp();
        using var lifetime = new CancellationTokenSource();
        var host = TerminalUiLifecycleChildProcess.InitializeHost(terminal, lifetime.Token);
        host.ShutdownStarting += lifetime.Cancel;
        HotkeyManager.OverlaySink = host;
        var run = await terminal.StartAsync(host);

        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = new LeaseHoldingPlugin(entered);
        PluginManager.LoadedPlugins.Add(plugin);
        PluginManager.InitializePlugin(plugin);
        var started = PluginManager.TriggerStartedForPluginsAsync([plugin], lifetime.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await terminal.InvokeAsync(() =>
            Assert.True(terminal.Application.TopRunnableView!.InvokeCommand(Command.Quit)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await started);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(lifetime.IsCancellationRequested);
    }

    static async Task RunDisposeAfterStartedAsync()
    {
        using var terminal = new TerminalGuiTestApp();
        using var lifetime = new CancellationTokenSource();
        var host = TerminalUiLifecycleChildProcess.InitializeHost(terminal, lifetime.Token);
        host.ShutdownStarting += lifetime.Cancel;
        HotkeyManager.OverlaySink = host;
        var run = await terminal.StartAsync(host);

        var plugin = new CancellationObservingPlugin();
        PluginManager.LoadedPlugins.Add(plugin);
        PluginManager.InitializePlugin(plugin);
        await PluginManager.TriggerStartedForPluginsAsync([plugin], lifetime.Token);
        Assert.False(lifetime.IsCancellationRequested);

        host.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(plugin.Disposed);
    }

    static async Task RunCreateWindowFailureAsync()
    {
        using var terminal = new TerminalGuiTestApp();
        var host = TerminalUiLifecycleChildProcess.InitializeHost(terminal, CancellationToken.None);
        var workspace = Workspace.Create("CreateWindow failure");
        host.SetPanel(
            workspace,
            "pending",
            "pending",
            WorkspaceContent.Text("pending"),
            fullBleed: true,
            switchToWorkspace: true);
        var flush = host.FlushAsync();
        Config.WorkspaceTaskbarTitleOrder = null!;

        var failure = await Record.ExceptionAsync(async () =>
        {
            var run = await terminal.StartAsync(host);
            await run;
        });
        var flushFailure = await Record.ExceptionAsync(async () => await flush);

        Assert.Contains("Config.WorkspaceTaskbarTitleOrder", Assert.IsType<InvalidOperationException>(failure).Message);
        Assert.Contains("accepted flush event", Assert.IsType<InvalidOperationException>(flushFailure).Message);
    }

    static async Task RunAggregatedFailureAsync()
    {
        using var terminal = new TerminalGuiTestApp();
        var host = TerminalUiLifecycleChildProcess.InitializeHost(terminal, CancellationToken.None);
        var plugin = new FailingShutdownPlugin();
        PluginManager.LoadedPlugins.Add(plugin);
        PluginManager.InitializePlugin(plugin);
        Config.WorkspaceTaskbarTitleOrder = null!;

        var failure = await Record.ExceptionAsync(async () =>
        {
            var run = await terminal.StartAsync(host);
            await run;
        });
        var aggregate = Assert.IsType<AggregateException>(failure).Flatten();

        Assert.Contains(
            aggregate.InnerExceptions,
            exception => exception.Message.Contains(
                "Config.WorkspaceTaskbarTitleOrder",
                StringComparison.Ordinal));
        Assert.Contains(
            aggregate.InnerExceptions,
            exception => exception.ToString().Contains(
                "shutdown-cleanup-failure",
                StringComparison.Ordinal));
    }

    static (UiHost Host, FirstPostSynchronizationContext OwnerContext)
        InitializeHostWithObservedPosts(TerminalGuiTestApp terminal)
    {
        var currentConfig = typeof(Config).GetProperty(
            "Current",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Null(currentConfig.GetValue(null));
        currentConfig.SetValue(null, new YamlConfig());

        UiHost? host = null;
        FirstPostSynchronizationContext? ownerContext = null;
        terminal.RunOnOwnerThread(() =>
        {
            ownerContext = new(SynchronizationContext.Current!);
            host = new(
                terminal.Application,
                ownerContext,
                CancellationToken.None);
            TerminalUi.Initialize(host);
        });
        return (host!, ownerContext!);
    }

    sealed class ShutdownWorkspacePlugin(Workspace workspace) : IPlugin
    {
        public string Name => nameof(ShutdownWorkspacePlugin);
        public string Author => "Test";
        public string[] Targets => [];
        public bool PanelRemoved { get; private set; }

        public void Initialize(IPluginContext context) { }

        public void Dispose()
        {
            PanelRemoved = workspace.RemovePanel("probe");
        }
    }

    sealed class ShutdownProbeView : Label
    {
        public bool DetachedAndDisposedWhileHostAccepted { get; private set; }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing || DetachedAndDisposedWhileHostAccepted)
                return;

            TerminalUi.Log("shutdown-test", "realized panel disposed");
            DetachedAndDisposedWhileHostAccepted = SuperView is null;
        }
    }

    sealed class LeaseHoldingPlugin(TaskCompletionSource entered) : IPlugin
    {
        public string Name => nameof(LeaseHoldingPlugin);
        public string Author => "Test";
        public string[] Targets => [];

        public void Initialize(IPluginContext context)
        {
            context.Events.OnStarted(async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        }
    }

    sealed class CancellationObservingPlugin : IPlugin
    {
        CancellationToken lifetimeToken;

        public string Name => nameof(CancellationObservingPlugin);
        public string Author => "Test";
        public string[] Targets => [];
        public bool Disposed { get; private set; }

        public void Initialize(IPluginContext context)
        {
            context.Events.OnStarted(cancellationToken =>
            {
                lifetimeToken = cancellationToken;
                return ValueTask.CompletedTask;
            });
        }

        public void Dispose()
        {
            Assert.True(lifetimeToken.IsCancellationRequested);
            Disposed = true;
        }
    }

    sealed class FailingShutdownPlugin : IPlugin
    {
        public string Name => nameof(FailingShutdownPlugin);
        public string Author => "Test";
        public string[] Targets => [];

        public void Initialize(IPluginContext context) { }

        public void Dispose()
            => throw new InvalidOperationException("shutdown-cleanup-failure");
    }

    sealed class FirstPostSynchronizationContext(SynchronizationContext owner)
        : SynchronizationContext
    {
        readonly TaskCompletionSource firstPostCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstPostCompleted => firstPostCompleted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            owner.Post(_ =>
            {
                try
                {
                    callback(state);
                }
                finally
                {
                    firstPostCompleted.TrySetResult();
                }
            }, null);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
