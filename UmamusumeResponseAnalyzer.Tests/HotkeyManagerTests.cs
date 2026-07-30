using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using UmamusumeResponseAnalyzer.TerminalGui;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[CollectionDefinition("HotkeyManager", DisableParallelization = true)]
public sealed class HotkeyManagerCollection;

[Collection("HotkeyManager")]
public sealed class HotkeyManagerTests : IDisposable
{
    readonly IApplication application = Application.Create();

    public HotkeyManagerTests() => ResetHotkeyManager();

    public void Dispose()
    {
        ResetHotkeyManager();
        application.Dispose();
    }

    static Func<Task> NoopHandler => () => Task.CompletedTask;

    static void ResetHotkeyManager()
    {
        HotkeyManager.UnregisterAll();
        HotkeyManager.OverlaySink = null;
        HotkeyManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
        TerminalUi.UnbindForTests();
    }

    [Theory]
    [InlineData(ConsoleKey.K, ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift, "Ctrl+Alt+Shift+K")]
    [InlineData(ConsoleKey.UpArrow, ConsoleModifiers.None, "↑")]
    [InlineData(ConsoleKey.DownArrow, ConsoleModifiers.Control, "Ctrl+↓")]
    [InlineData(ConsoleKey.Oem2, ConsoleModifiers.None, "/")]
    public void FormatKeyCombo_UsesStableReadableNames(
        ConsoleKey key,
        ConsoleModifiers modifiers,
        string expected)
    {
        Assert.Equal(expected, HotkeyManager.FormatKeyCombo(key, modifiers));
    }

    [Theory]
    [InlineData(ConsoleKey.S)]
    [InlineData(ConsoleKey.Q)]
    [InlineData(ConsoleKey.Z)]
    public void Register_RejectsCtrlTerminalFlowControlKeys(ConsoleKey key)
    {
        Assert.Throws<InvalidOperationException>(() =>
            HotkeyManager.Register(key, ConsoleModifiers.Control, "reserved", NoopHandler));
        Assert.Empty(HotkeyManager.Hotkeys);
    }

    [Fact]
    public void Registrations_ReplacePreciselyAndTrackOwnerByReference()
    {
        var owner = new object();
        var otherOwner = new object();
        HotkeyManager.HotkeyEntry first;
        using (HotkeyManager.RegisterScope(owner))
            first = HotkeyManager.RegisterTracked(ConsoleKey.K, ConsoleModifiers.Control, "first", NoopHandler);
        using (HotkeyManager.RegisterScope(otherOwner))
            HotkeyManager.Register(ConsoleKey.A, "other", NoopHandler);

        var replacement = HotkeyManager.RegisterTracked(
            ConsoleKey.K,
            ConsoleModifiers.Control,
            "replacement",
            NoopHandler);

        Assert.False(HotkeyManager.Unregister(ConsoleKey.K, ConsoleModifiers.Control, first));
        Assert.Same(replacement, HotkeyManager.Hotkeys[(ConsoleKey.K, ConsoleModifiers.Control)]);
        Assert.Equal(1, HotkeyManager.UnregisterByOwner(otherOwner));
        Assert.Single(HotkeyManager.Hotkeys);
        Assert.True(HotkeyManager.Unregister(ConsoleKey.K, ConsoleModifiers.Control));
        Assert.Empty(HotkeyManager.Hotkeys);
    }

    [Fact]
    public async Task Popup_NavigatesClampsSelectsAndCloses()
    {
        var sink = new RecordingOverlaySink { PopupVisibleLineCount = 2 };
        HotkeyManager.OverlaySink = sink;
        HotkeyManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var confirmedLine = -1;
        HotkeyManager.ShowPopup(new HotkeyPopup(
            [
                new("title"),
                new("first"),
                new("second")
            ],
            Selection: new HotkeyPopupSelection([1, 2], 0, line =>
            {
                confirmedLine = line;
                return Task.CompletedTask;
            })));

        await PressAsync(KeyCode.CursorDown);
        await PressAsync(KeyCode.CursorDown);

        Assert.Equal(1, sink.Popup?.Selection?.SelectedIndex);
        Assert.Equal(1, sink.Popup?.ScrollOffset);

        await PressAsync(KeyCode.Enter);

        Assert.Equal(2, confirmedLine);
        Assert.Null(sink.Popup);
    }

    [Fact]
    public async Task InputPriority_IsPopupNotificationWorkspaceThenPersistent()
    {
        var sink = new RecordingOverlaySink
        {
            HandleWorkspaceCommand = command => command == Command.Up
        };
        HotkeyManager.OverlaySink = sink;
        HotkeyManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var calls = new List<string>();

        HotkeyManager.Register(ConsoleKey.F8, "persistent", () =>
        {
            calls.Add("persistent");
            return Task.CompletedTask;
        });
        HotkeyManager.Register(ConsoleKey.UpArrow, "persistent-up", () =>
        {
            calls.Add("persistent-up");
            return Task.CompletedTask;
        });
        var notification = HotkeyManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMinutes(1),
            [new UiShortcut(ConsoleKey.F8, () =>
            {
                calls.Add("notification");
                return Task.CompletedTask;
            })]);

        HotkeyManager.ShowPopup(new HotkeyContext()
            .AddLine("popup")
            .BindShortcut(new UiShortcut(ConsoleKey.F8, () =>
            {
                calls.Add("popup");
                return Task.CompletedTask;
            })));

        await PressAsync(KeyCode.F8);
        Assert.Equal(["popup"], calls);

        await PressAsync(KeyCode.Esc);
        await PressAsync(KeyCode.F8);
        Assert.Equal(["popup", "notification"], calls);

        HotkeyManager.UnregisterNotificationShortcuts(notification);
        await PressAsync(KeyCode.CursorUp);
        await PressAsync(KeyCode.F8);

        Assert.Equal(["popup", "notification", "persistent"], calls);
        Assert.Equal(Command.Up, Assert.Single(sink.WorkspaceCommands));
    }

    [Fact]
    public async Task PopupBuiltInKey_PrecedesNotificationAndPersistentShortcut()
    {
        var sink = new RecordingOverlaySink();
        HotkeyManager.OverlaySink = sink;
        HotkeyManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var notification = 0;
        var persistent = 0;
        HotkeyManager.Register(ConsoleKey.Enter, "persistent", () =>
        {
            persistent++;
            return Task.CompletedTask;
        });
        HotkeyManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMinutes(1),
            [new UiShortcut(ConsoleKey.Enter, () =>
            {
                notification++;
                return Task.CompletedTask;
            })]);
        HotkeyManager.ShowPopup(new HotkeyContext().AddLine("popup"));

        await PressAsync(KeyCode.Enter);

        Assert.Null(sink.Popup);
        Assert.Equal((0, 0), (notification, persistent));
    }

    [Fact]
    public async Task TransientShortcut_MatchesModifiersExactlyAndDoesNotClosePopup()
    {
        var sink = new RecordingOverlaySink();
        HotkeyManager.OverlaySink = sink;
        HotkeyManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var transient = 0;
        var persistent = 0;
        HotkeyManager.Register(ConsoleKey.K, "bare", () =>
        {
            persistent++;
            return Task.CompletedTask;
        });
        HotkeyManager.ShowPopup(new HotkeyContext()
            .AddLine("popup")
            .BindShortcut(new UiShortcut(
                ConsoleKey.K,
                () =>
                {
                    transient++;
                    return Task.CompletedTask;
                },
                ConsoleModifiers.Control)));

        await PressAsync(KeyCode.K | KeyCode.CtrlMask);
        await PressAsync(KeyCode.K | KeyCode.AltMask);

        Assert.Equal((1, 0), (transient, persistent));
        Assert.NotNull(sink.Popup);

        await PressAsync(KeyCode.K);
        Assert.Equal((1, 1), (transient, persistent));
        Assert.Null(sink.Popup);
    }

    [Fact]
    public async Task LatestUnexpiredNotificationShortcutWinsThenOlderResumes()
    {
        var calls = new List<string>();
        HotkeyManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMinutes(1),
            [new UiShortcut(ConsoleKey.F9, () =>
            {
                calls.Add("old");
                return Task.CompletedTask;
            })]);
        HotkeyManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMilliseconds(80),
            [new UiShortcut(ConsoleKey.F9, () =>
            {
                calls.Add("new");
                return Task.CompletedTask;
            })]);

        await PressAsync(KeyCode.F9);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        await PressAsync(KeyCode.F9);

        Assert.Equal(["new", "old"], calls);
    }

    [Fact]
    public async Task MouseWheel_UsesTerminalGuiStepsAndSuppressesNonVerticalOrModifiedInput()
    {
        var sink = new RecordingOverlaySink { HandleWorkspaceCommand = _ => true };
        HotkeyManager.OverlaySink = sink;

        await HotkeyManager.HandleMouseWheelAsync(2, hasModifiers: false);
        await HotkeyManager.HandleMouseWheelAsync(-2, hasModifiers: false);
        await HotkeyManager.HandleMouseWheelAsync(1, hasModifiers: true);
        await HotkeyManager.HandleMouseWheelAsync(1, hasModifiers: false, isHorizontal: true);

        Assert.Equal(
            [Command.Up, Command.Up, Command.Down, Command.Down],
            sink.WorkspaceCommands);

        HotkeyManager.ShowPopup(new HotkeyContext().AddLine("popup"));
        await HotkeyManager.HandleMouseWheelAsync(1, hasModifiers: false);
        Assert.Equal(4, sink.WorkspaceCommands.Count);
    }

    [Fact]
    public async Task HandlerException_IsReportedWithoutBreakingLaterDispatch()
    {
        var attempts = 0;
        HotkeyManager.Register(ConsoleKey.F10, "throws", () =>
        {
            attempts++;
            throw new InvalidOperationException("boom");
        });

        Assert.Null(await Record.ExceptionAsync(() => PressAsync(KeyCode.F10)));
        Assert.Null(await Record.ExceptionAsync(() => PressAsync(KeyCode.F10)));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WorkspaceRemoval_UnregistersOnlyItsTransientShortcut()
    {
        var output = new UiHost(application, static () => [], static _ => { }).ForPlugin("owner");
        var removed = output.CreateWorkspace("removed");
        var kept = output.CreateWorkspace("kept");
        var calls = new List<string>();
        output.Notify(
            removed,
            "removed",
            ttl: TimeSpan.FromMinutes(1),
            shortcuts: new UiShortcut(ConsoleKey.F7, () =>
            {
                calls.Add("removed");
                return Task.CompletedTask;
            }));
        output.Notify(
            kept,
            "kept",
            ttl: TimeSpan.FromMinutes(1),
            shortcuts: new UiShortcut(ConsoleKey.F8, () =>
            {
                calls.Add("kept");
                return Task.CompletedTask;
            }));

        output.RemoveWorkspace(removed);
        await PressAsync(KeyCode.F7);
        await PressAsync(KeyCode.F8);

        Assert.Equal(["kept"], calls);
    }

    static Task PressAsync(KeyCode keyCode) => HotkeyManager.HandleKeyAsync(new Key(keyCode));

    sealed class RecordingOverlaySink : IUiInputSink
    {
        readonly List<Command> workspaceCommands = [];

        public int PopupVisibleLineCount { get; init; } = 10;
        public Func<Command, bool>? HandleWorkspaceCommand { get; init; }
        public IReadOnlyList<Command> WorkspaceCommands => workspaceCommands;
        public HotkeyPopup? Popup { get; private set; }

        public Task<bool> TryHandleWorkspaceCommandAsync(Command command)
        {
            if (HandleWorkspaceCommand?.Invoke(command) != true)
                return Task.FromResult(false);

            workspaceCommands.Add(command);
            return Task.FromResult(true);
        }

        public void ShowPopup(HotkeyPopup popup, int generation) => Popup = popup;
        public void HidePopup(int generation) => Popup = null;
    }
}
