using Terminal.Gui.App;
using UmamusumeResponseAnalyzer.LiveDisplay;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[CollectionDefinition("KeyboardManager", DisableParallelization = true)]
public sealed class KeyboardManagerCollection;

[Collection("KeyboardManager")]
public sealed class KeyboardManagerTests : IDisposable
{
    readonly IApplication application = Application.Create();

    public KeyboardManagerTests() => ResetKeyboardManager();

    public void Dispose()
    {
        ResetKeyboardManager();
        application.Dispose();
    }

    static Func<Task> NoopHandler => () => Task.CompletedTask;

    static void ResetKeyboardManager()
    {
        KeyboardManager.UnregisterAll();
        KeyboardManager.SetCommandHandler(null);
        KeyboardManager.OverlaySink = null;
        KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
        LiveDisplayConsole.UnbindForTests();
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
        Assert.Equal(expected, KeyboardManager.FormatKeyCombo(key, modifiers));
    }

    [Theory]
    [InlineData(ConsoleKey.S)]
    [InlineData(ConsoleKey.Q)]
    [InlineData(ConsoleKey.Z)]
    public void Register_RejectsCtrlTerminalFlowControlKeys(ConsoleKey key)
    {
        Assert.Throws<InvalidOperationException>(() =>
            KeyboardManager.Register(key, ConsoleModifiers.Control, "reserved", NoopHandler));
        Assert.Empty(KeyboardManager.Hotkeys);
    }

    [Fact]
    public void Registrations_ReplacePreciselyAndTrackOwnerByReference()
    {
        var owner = new object();
        var otherOwner = new object();
        KeyboardManager.HotkeyEntry first;
        using (KeyboardManager.RegisterScope(owner))
            first = KeyboardManager.RegisterTracked(ConsoleKey.K, ConsoleModifiers.Control, "first", NoopHandler);
        using (KeyboardManager.RegisterScope(otherOwner))
            KeyboardManager.Register(ConsoleKey.A, "other", NoopHandler);

        var replacement = KeyboardManager.RegisterTracked(
            ConsoleKey.K,
            ConsoleModifiers.Control,
            "replacement",
            NoopHandler);

        Assert.False(KeyboardManager.Unregister(ConsoleKey.K, ConsoleModifiers.Control, first));
        Assert.Same(replacement, KeyboardManager.Hotkeys[(ConsoleKey.K, ConsoleModifiers.Control)]);
        Assert.Equal(1, KeyboardManager.UnregisterByOwner(otherOwner));
        Assert.Single(KeyboardManager.Hotkeys);
        Assert.True(KeyboardManager.Unregister(ConsoleKey.K, ConsoleModifiers.Control));
        Assert.Empty(KeyboardManager.Hotkeys);
    }

    [Fact]
    public async Task Popup_NavigatesClampsSelectsAndCloses()
    {
        var sink = new RecordingOverlaySink { PopupVisibleLineCount = 2 };
        KeyboardManager.OverlaySink = sink;
        KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var confirmedLine = -1;
        KeyboardManager.ShowPopup(new KeyboardPopup(
            [
                new("title", ConsoleColor.White),
                new("first", ConsoleColor.White),
                new("second", ConsoleColor.White)
            ],
            Selection: new KeyboardPopupSelection([1, 2], 0, line =>
            {
                confirmedLine = line;
                return Task.CompletedTask;
            })));

        await PressAsync(ConsoleKey.DownArrow);
        await PressAsync(ConsoleKey.DownArrow);

        Assert.Equal(1, sink.Popup?.Selection?.SelectedIndex);
        Assert.Equal(1, sink.Popup?.ScrollOffset);

        await PressAsync(ConsoleKey.Enter);

        Assert.Equal(2, confirmedLine);
        Assert.Null(sink.Popup);
    }

    [Fact]
    public async Task CommandInput_SubmitsHistoryAndCompletesWithoutLeakingToHotkeys()
    {
        var sink = new RecordingOverlaySink();
        KeyboardManager.OverlaySink = sink;
        var submitted = new List<string>();
        var hotkeyInvocations = 0;
        KeyboardManager.SetCommandHandler(
            command =>
            {
                submitted.Add(command);
                return Task.CompletedTask;
            },
            input => input == "/wor" ? ["/workspace", "/worktree"] : []);
        KeyboardManager.Register(
            ConsoleKey.K,
            ConsoleModifiers.Control,
            "must not run in command mode",
            () =>
            {
                hotkeyInvocations++;
                return Task.CompletedTask;
            });

        await TypeAsync('/', ConsoleKey.Oem2);
        await TypeAsync('w', ConsoleKey.W);
        await TypeAsync('o', ConsoleKey.O);
        await TypeAsync('r', ConsoleKey.R);
        await PressAsync(ConsoleKey.Tab);

        Assert.Equal("/work", sink.CommandInput?.Text);
        Assert.Equal(["/workspace", "/worktree"], sink.CommandInput?.CompletionCandidates);

        await PressAsync(ConsoleKey.K, ConsoleModifiers.Control);
        await PressAsync(ConsoleKey.Enter);
        await PressAsync(ConsoleKey.Enter);
        await PressAsync(ConsoleKey.UpArrow);

        Assert.Equal(0, hotkeyInvocations);
        Assert.Equal(["/work"], submitted);
        Assert.Equal("/work", sink.CommandInput?.Text);

        await PressAsync(ConsoleKey.Escape);
        Assert.Null(sink.CommandInput);
    }

    [Fact]
    public async Task InputPriority_IsCommandPopupNotificationWorkspaceThenPersistent()
    {
        var sink = new RecordingOverlaySink
        {
            HandleWorkspaceKey = key => key.Key == ConsoleKey.UpArrow
        };
        KeyboardManager.OverlaySink = sink;
        KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var calls = new List<string>();

        KeyboardManager.Register(ConsoleKey.F8, "persistent", () =>
        {
            calls.Add("persistent");
            return Task.CompletedTask;
        });
        KeyboardManager.Register(ConsoleKey.UpArrow, "persistent-up", () =>
        {
            calls.Add("persistent-up");
            return Task.CompletedTask;
        });
        var notification = KeyboardManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMinutes(1),
            [new LiveDisplayShortcut(ConsoleKey.F8, () =>
            {
                calls.Add("notification");
                return Task.CompletedTask;
            })]);

        await PressAsync(ConsoleKey.Enter);
        KeyboardManager.ShowPopup(new KeyboardHandlerContext()
            .WriteLine("popup")
            .BindShortcut(new LiveDisplayShortcut(ConsoleKey.F8, () =>
            {
                calls.Add("popup");
                return Task.CompletedTask;
            })));

        await PressAsync(ConsoleKey.F8);
        await PressAsync(ConsoleKey.Escape);
        Assert.Empty(calls);

        await PressAsync(ConsoleKey.F8);
        Assert.Equal(["popup"], calls);

        await PressAsync(ConsoleKey.Escape);
        await PressAsync(ConsoleKey.F8);
        Assert.Equal(["popup", "notification"], calls);

        KeyboardManager.UnregisterNotificationShortcuts(notification);
        await PressAsync(ConsoleKey.UpArrow);
        await PressAsync(ConsoleKey.F8);

        Assert.Equal(["popup", "notification", "persistent"], calls);
        Assert.Equal(ConsoleKey.UpArrow, Assert.Single(sink.WorkspaceKeys).Key);
    }

    [Fact]
    public async Task PopupBuiltInKey_PrecedesNotificationAndPersistentShortcut()
    {
        var sink = new RecordingOverlaySink();
        KeyboardManager.OverlaySink = sink;
        KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var notification = 0;
        var persistent = 0;
        KeyboardManager.Register(ConsoleKey.Enter, "persistent", () =>
        {
            persistent++;
            return Task.CompletedTask;
        });
        KeyboardManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMinutes(1),
            [new LiveDisplayShortcut(ConsoleKey.Enter, () =>
            {
                notification++;
                return Task.CompletedTask;
            })]);
        KeyboardManager.ShowPopup(new KeyboardHandlerContext().WriteLine("popup"));

        await PressAsync(ConsoleKey.Enter);

        Assert.Null(sink.Popup);
        Assert.Equal((0, 0), (notification, persistent));
    }

    [Fact]
    public async Task TransientShortcut_MatchesModifiersExactlyAndDoesNotClosePopup()
    {
        var sink = new RecordingOverlaySink();
        KeyboardManager.OverlaySink = sink;
        KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
        var transient = 0;
        var persistent = 0;
        KeyboardManager.Register(ConsoleKey.K, "bare", () =>
        {
            persistent++;
            return Task.CompletedTask;
        });
        KeyboardManager.ShowPopup(new KeyboardHandlerContext()
            .WriteLine("popup")
            .BindShortcut(new LiveDisplayShortcut(
                ConsoleKey.K,
                () =>
                {
                    transient++;
                    return Task.CompletedTask;
                },
                ConsoleModifiers.Control)));

        await PressAsync(ConsoleKey.K, ConsoleModifiers.Control);
        await PressAsync(ConsoleKey.K, ConsoleModifiers.Alt);

        Assert.Equal((1, 0), (transient, persistent));
        Assert.NotNull(sink.Popup);

        await PressAsync(ConsoleKey.K);
        Assert.Equal((1, 1), (transient, persistent));
        Assert.Null(sink.Popup);
    }

    [Fact]
    public async Task LatestUnexpiredNotificationShortcutWinsThenOlderResumes()
    {
        var calls = new List<string>();
        KeyboardManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMinutes(1),
            [new LiveDisplayShortcut(ConsoleKey.F9, () =>
            {
                calls.Add("old");
                return Task.CompletedTask;
            })]);
        KeyboardManager.RegisterNotificationShortcuts(
            workspace: null,
            DateTimeOffset.Now.AddMilliseconds(80),
            [new LiveDisplayShortcut(ConsoleKey.F9, () =>
            {
                calls.Add("new");
                return Task.CompletedTask;
            })]);

        await PressAsync(ConsoleKey.F9);
        await Task.Delay(120, TestContext.Current.CancellationToken);
        await PressAsync(ConsoleKey.F9);

        Assert.Equal(["new", "old"], calls);
    }

    [Fact]
    public async Task MouseWheel_UsesTerminalGuiStepsAndSuppressesNonVerticalOrModifiedInput()
    {
        var sink = new RecordingOverlaySink { HandleWorkspaceKey = _ => true };
        KeyboardManager.OverlaySink = sink;

        await KeyboardManager.HandleMouseWheelAsync(2, 0);
        await KeyboardManager.HandleMouseWheelAsync(-2, 0);
        await KeyboardManager.HandleMouseWheelAsync(1, ConsoleModifiers.Shift);
        await KeyboardManager.HandleMouseWheelAsync(1, 0, isHorizontal: true);

        Assert.Equal(
            [ConsoleKey.UpArrow, ConsoleKey.UpArrow, ConsoleKey.DownArrow, ConsoleKey.DownArrow],
            sink.WorkspaceKeys.Select(key => key.Key));

        KeyboardManager.ShowPopup(new KeyboardHandlerContext().WriteLine("popup"));
        await KeyboardManager.HandleMouseWheelAsync(1, 0);
        Assert.Equal(4, sink.WorkspaceKeys.Count);
    }

    [Fact]
    public async Task HandlerException_IsReportedWithoutBreakingLaterDispatch()
    {
        var attempts = 0;
        KeyboardManager.Register(ConsoleKey.F10, "throws", () =>
        {
            attempts++;
            throw new InvalidOperationException("boom");
        });

        Assert.Null(await Record.ExceptionAsync(() => PressAsync(ConsoleKey.F10)));
        Assert.Null(await Record.ExceptionAsync(() => PressAsync(ConsoleKey.F10)));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task WorkspaceRemoval_UnregistersOnlyItsTransientShortcut()
    {
        var output = new UiHost(application).ForPlugin("owner");
        var removed = output.CreateWorkspace("removed");
        var kept = output.CreateWorkspace("kept");
        var calls = new List<string>();
        output.Notify(
            removed,
            "removed",
            ttl: TimeSpan.FromMinutes(1),
            shortcuts: new LiveDisplayShortcut(ConsoleKey.F7, () =>
            {
                calls.Add("removed");
                return Task.CompletedTask;
            }));
        output.Notify(
            kept,
            "kept",
            ttl: TimeSpan.FromMinutes(1),
            shortcuts: new LiveDisplayShortcut(ConsoleKey.F8, () =>
            {
                calls.Add("kept");
                return Task.CompletedTask;
            }));

        output.RemoveWorkspace(removed);
        await PressAsync(ConsoleKey.F7);
        await PressAsync(ConsoleKey.F8);

        Assert.Equal(["kept"], calls);
    }

    static Task PressAsync(ConsoleKey key, ConsoleModifiers modifiers = 0)
        => KeyboardManager.HandleKeyAsync(new ConsoleKeyInfo(
            '\0',
            key,
            modifiers.HasFlag(ConsoleModifiers.Shift),
            modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control)));

    static Task TypeAsync(char keyChar, ConsoleKey key, ConsoleModifiers modifiers = 0)
        => KeyboardManager.HandleKeyAsync(new ConsoleKeyInfo(
            keyChar,
            key,
            modifiers.HasFlag(ConsoleModifiers.Shift),
            modifiers.HasFlag(ConsoleModifiers.Alt),
            modifiers.HasFlag(ConsoleModifiers.Control)));

    sealed class RecordingOverlaySink : IKeyboardOverlaySink
    {
        readonly List<ConsoleKeyInfo> workspaceKeys = [];

        public int PopupVisibleLineCount { get; init; } = 10;
        public Func<ConsoleKeyInfo, bool>? HandleWorkspaceKey { get; init; }
        public IReadOnlyList<ConsoleKeyInfo> WorkspaceKeys => workspaceKeys;
        public KeyboardPopup? Popup { get; private set; }
        public KeyboardCommandInput? CommandInput { get; private set; }

        public Task<bool> TryHandleWorkspaceKeyAsync(ConsoleKeyInfo keyInfo)
        {
            if (HandleWorkspaceKey?.Invoke(keyInfo) != true)
                return Task.FromResult(false);

            workspaceKeys.Add(keyInfo);
            return Task.FromResult(true);
        }

        public void ShowPopup(KeyboardPopup popup, int generation) => Popup = popup;
        public void HidePopup(int generation) => Popup = null;
        public void ShowCommandInput(KeyboardCommandInput input) => CommandInput = input;
        public void HideCommandInput() => CommandInput = null;
    }
}
