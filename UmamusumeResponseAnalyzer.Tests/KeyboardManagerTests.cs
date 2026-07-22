using System.Runtime.InteropServices;
using UmamusumeResponseAnalyzer.LiveDisplay;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    [CollectionDefinition("KeyboardManager")]
    public sealed class KeyboardManagerCollection
    {
    }

    /// <summary>
    /// 单元测试 <see cref="UmamusumeResponseAnalyzer.KeyboardManager"/> 的纯逻辑：
    /// 组合键格式化、保留键校验、注册/反注册字典增删、popup sink 路由。
    /// RunAsync 生命周期仅通过 fake input session 验证，不触碰真实终端。
    ///
    /// KeyboardManager 是 static，被测方法直接读写全局 hotkeys 字典。
    /// xUnit 对同一测试类的方法串行执行、且每个测试方法新建一个实例；
    /// 会改动 hotkeys 字典的测试放在同一个 collection 中串行执行，
    /// 并在每个测试前后调用 UnregisterAll() 清空全局状态、避免互相污染。
    /// </summary>
    [Collection("KeyboardManager")]
    public class KeyboardManagerTests : IDisposable
    {
        // 构造函数在每个测试方法前运行：先洗干净全局字典，确保测试从空状态起步
        public KeyboardManagerTests() => ResetKeyboardManager();

        // 测试结束再洗一次，不给后续测试留残留
        public void Dispose() => ResetKeyboardManager();

        static Func<Task> NoopHandler => () => Task.CompletedTask;

        static void ResetKeyboardManager()
        {
            using (KeyboardManager.SuspendInput())
            {
            }

            KeyboardManager.UnregisterAll();
            KeyboardManager.SetCommandHandler(null);
            KeyboardManager.OverlaySink = null;
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = null;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
            LiveDisplayConsole.UnbindForTests();
        }

        // ── Windows Console input ────────────────────────────────────────

        [Fact]
        public void WindowsConsoleInputRecord_MatchesWin32Abi()
        {
            Assert.Equal(20, Marshal.SizeOf<WindowsConsoleInputRecord>());
            Assert.Equal(16, Marshal.SizeOf<WindowsConsoleKeyEventRecord>());
            Assert.Equal(16, Marshal.SizeOf<WindowsConsoleMouseEventRecord>());
            Assert.Equal(4, Marshal.OffsetOf<WindowsConsoleInputRecord>(nameof(WindowsConsoleInputRecord.KeyEvent)).ToInt32());
            Assert.Equal(4, Marshal.OffsetOf<WindowsConsoleInputRecord>(nameof(WindowsConsoleInputRecord.MouseEvent)).ToInt32());
        }

        [Fact]
        public void WindowsConsoleInputDecoder_PreservesWheelAndKeyOrderAcrossIgnoredKeyUp()
        {
            var records = new[]
            {
                WindowsConsoleInputRecord.Key(false, 1, ConsoleKey.A, 'a'),
                WindowsConsoleInputRecord.Wheel(120),
                WindowsConsoleInputRecord.Key(true, 1, ConsoleKey.B, 'b')
            };
            var decoded = new List<ConsoleInputEvent>();

            foreach (var record in records)
            {
                if (WindowsConsoleInputSession.TryDecode(record, out var input, out _))
                    decoded.Add(input);
            }

            Assert.Collection(
                decoded,
                input =>
                {
                    Assert.Equal(ConsoleInputEventKind.MouseWheel, input.Kind);
                    Assert.Equal(120, input.WheelDelta);
                },
                input =>
                {
                    Assert.Equal(ConsoleInputEventKind.Key, input.Kind);
                    Assert.Equal(ConsoleKey.B, input.KeyInfo.Key);
                    Assert.Equal('b', input.KeyInfo.KeyChar);
                });
        }

        [Fact]
        public void WindowsConsoleInputDecoder_DecodesRepeatImeAltKeyUpAndControlC()
        {
            Assert.True(WindowsConsoleInputSession.TryDecode(
                WindowsConsoleInputRecord.Key(true, 3, ConsoleKey.A, 'a'),
                out var repeated,
                out var repeatCount));
            Assert.Equal(ConsoleInputEventKind.Key, repeated.Kind);
            Assert.Equal(ConsoleKey.A, repeated.KeyInfo.Key);
            Assert.Equal((ushort)3, repeatCount);

            Assert.True(WindowsConsoleInputSession.TryDecode(
                WindowsConsoleInputRecord.Key(
                    false,
                    1,
                    0x12,
                    '中',
                    WindowsConsoleControlKeyState.LeftAltPressed),
                out var ime,
                out repeatCount));
            Assert.Equal('中', ime.KeyInfo.KeyChar);
            Assert.Equal(ConsoleModifiers.Alt, ime.KeyInfo.Modifiers);
            Assert.Equal((ushort)1, repeatCount);

            Assert.True(WindowsConsoleInputSession.TryDecode(
                WindowsConsoleInputRecord.Key(
                    true,
                    1,
                    ConsoleKey.C,
                    '\u0003',
                    WindowsConsoleControlKeyState.LeftCtrlPressed),
                out var controlC,
                out repeatCount));
            Assert.Equal(ConsoleKey.C, controlC.KeyInfo.Key);
            Assert.Equal('\u0003', controlC.KeyInfo.KeyChar);
            Assert.Equal(ConsoleModifiers.Control, controlC.KeyInfo.Modifiers);
        }

        [Fact]
        public void WindowsConsoleInputDecoder_FiltersModifierLockAndAltNumpadConstituentKeys()
        {
            var ignored = new[]
            {
                WindowsConsoleInputRecord.Key(true, 1, 0x10, '\0'),
                WindowsConsoleInputRecord.Key(true, 1, 0x14, '\0'),
                WindowsConsoleInputRecord.Key(true, 1, 0x90, '\0'),
                WindowsConsoleInputRecord.Key(true, 1, ConsoleKey.NumPad2, '\0', WindowsConsoleControlKeyState.LeftAltPressed),
                WindowsConsoleInputRecord.Key(true, 1, ConsoleKey.Insert, '\0', WindowsConsoleControlKeyState.LeftAltPressed)
            };

            foreach (var record in ignored)
            {
                Assert.False(WindowsConsoleInputSession.TryDecode(record, out _, out var repeatCount));
                Assert.Equal((ushort)0, repeatCount);
            }
        }

        [Fact]
        public void WindowsConsoleInputDecoder_DecodesSignedWheelDeltaModifiersAndHorizontalFlag()
        {
            var positive = WindowsConsoleInputRecord.Wheel(
                120,
                WindowsConsoleControlKeyState.CapsLockOn |
                WindowsConsoleControlKeyState.NumLockOn |
                WindowsConsoleControlKeyState.ScrollLockOn);
            var negativeHorizontal = WindowsConsoleInputRecord.Wheel(
                -120,
                WindowsConsoleControlKeyState.ShiftPressed |
                WindowsConsoleControlKeyState.RightAltPressed |
                WindowsConsoleControlKeyState.LeftCtrlPressed,
                horizontal: true);

            Assert.Equal(0x0078_0000u, positive.MouseEvent.ButtonState);
            Assert.Equal(0xFF88_0000u, negativeHorizontal.MouseEvent.ButtonState);

            Assert.True(WindowsConsoleInputSession.TryDecode(positive, out var up, out var repeatCount));
            Assert.Equal(ConsoleInputEventKind.MouseWheel, up.Kind);
            Assert.Equal(120, up.WheelDelta);
            Assert.Equal((ConsoleModifiers)0, up.Modifiers);
            Assert.False(up.IsHorizontal);
            Assert.Equal((ushort)1, repeatCount);

            Assert.True(WindowsConsoleInputSession.TryDecode(
                negativeHorizontal,
                out var down,
                out repeatCount));
            Assert.Equal(ConsoleInputEventKind.MouseWheel, down.Kind);
            Assert.Equal(-120, down.WheelDelta);
            Assert.Equal(
                ConsoleModifiers.Shift | ConsoleModifiers.Alt | ConsoleModifiers.Control,
                down.Modifiers);
            Assert.True(down.IsHorizontal);
            Assert.Equal((ushort)1, repeatCount);
        }

        [Fact]
        public async Task MouseWheel_AccumulatesPartialDeltaCancelsReversalAndRoutesEveryDetent()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;

            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            await KeyboardManager.HandleMouseWheelAsync(240, 0);
            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            await KeyboardManager.HandleMouseWheelAsync(-60, 0);
            await KeyboardManager.HandleMouseWheelAsync(-240, 0);

            Assert.Equal(
                [
                    ConsoleKey.UpArrow,
                    ConsoleKey.UpArrow,
                    ConsoleKey.UpArrow,
                    ConsoleKey.DownArrow,
                    ConsoleKey.DownArrow
                ],
                sink.WorkspaceKeys.Select(key => key.Key));
        }

        [Fact]
        public async Task MouseWheel_HorizontalModifierAndSuspensionClearPartialDelta()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;

            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            await KeyboardManager.HandleMouseWheelAsync(120, 0, isHorizontal: true);
            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            await KeyboardManager.HandleMouseWheelAsync(120, ConsoleModifiers.Shift);
            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            using (KeyboardManager.SuspendInput())
                await KeyboardManager.HandleMouseWheelAsync(120, 0);
            await KeyboardManager.HandleMouseWheelAsync(60, 0);

            Assert.Empty(sink.WorkspaceKeys);

            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            Assert.Equal(ConsoleKey.UpArrow, Assert.Single(sink.WorkspaceKeys).Key);
        }

        [Fact]
        public async Task MouseWheel_RemainderResetsBetweenInputSessions()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            var session = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => session;

            var run = KeyboardManager.RunAsync(CancellationToken.None);
            await session.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await KeyboardManager.HandleMouseWheelAsync(60, 0);

            Assert.Empty(sink.WorkspaceKeys);
            await KeyboardManager.HandleMouseWheelAsync(60, 0);
            Assert.Equal(ConsoleKey.UpArrow, Assert.Single(sink.WorkspaceKeys).Key);
        }

        [Fact]
        public async Task MouseWheel_PopupAndCommandInputSuppressWorkspaceAndKeyboardHandlers()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
            var popupShortcut = 0;
            var persistent = 0;
            KeyboardManager.Register(ConsoleKey.UpArrow, "persistent", () =>
            {
                persistent++;
                return Task.CompletedTask;
            });

            KeyboardManager.ShowPopup(new KeyboardHandlerContext()
                .WriteLine("popup")
                .BindShortcut(new LiveDisplayShortcut(ConsoleKey.UpArrow, () =>
                {
                    popupShortcut++;
                    return Task.CompletedTask;
                })));
            await KeyboardManager.HandleMouseWheelAsync(120, 0);
            Assert.Equal(0, sink.Popup?.ScrollOffset);
            Assert.Equal(0, popupShortcut);
            await PressAsync(ConsoleKey.Escape);

            KeyboardManager.ShowPopup(new KeyboardPopup(
                [
                    new KeyboardPopupLine("title", ConsoleColor.White),
                    new KeyboardPopupLine("first", ConsoleColor.White),
                    new KeyboardPopupLine("second", ConsoleColor.White)
                ],
                Selection: new KeyboardPopupSelection([1, 2], 0, _ => Task.CompletedTask)));
            await KeyboardManager.HandleMouseWheelAsync(-120, 0);
            Assert.Equal(0, sink.Popup?.Selection?.SelectedIndex);
            await PressAsync(ConsoleKey.Escape);

            KeyboardManager.SetCommandHandler(_ => Task.CompletedTask);
            await SubmitCommandAsync("old");
            await TypeAsync('/', ConsoleKey.Oem2);
            sink.ClearWorkspaceKeys();
            await KeyboardManager.HandleMouseWheelAsync(120, 0);
            Assert.Equal("/", sink.CommandInput?.Text);
            await PressAsync(ConsoleKey.Escape);

            Assert.Empty(sink.WorkspaceKeys);
            Assert.Equal(0, persistent);
        }

        [Fact]
        public async Task MouseWheel_BypassesNotificationAndPersistentShortcutsEvenAtWorkspaceBoundary()
        {
            var sink = new RecordingOverlaySink { WorkspaceKeyHandled = false };
            KeyboardManager.OverlaySink = sink;
            var notification = 0;
            var persistent = 0;
            KeyboardManager.Register(ConsoleKey.UpArrow, "persistent up", () =>
            {
                persistent++;
                return Task.CompletedTask;
            });
            KeyboardManager.Register(ConsoleKey.DownArrow, "persistent down", () =>
            {
                persistent++;
                return Task.CompletedTask;
            });
            var output = new UiHost().ForPlugin("Wheel");
            var workspace = output.CreateWorkspace("Wheel");
            output.Notify(
                workspace,
                "notification",
                ttl: TimeSpan.FromMinutes(1),
                shortcuts: new LiveDisplayShortcut(ConsoleKey.UpArrow, () =>
                {
                    notification++;
                    return Task.CompletedTask;
                }));

            await KeyboardManager.HandleMouseWheelAsync(120, 0);
            await KeyboardManager.HandleMouseWheelAsync(-120, 0);

            Assert.Equal([ConsoleKey.UpArrow, ConsoleKey.DownArrow], sink.WorkspaceKeys.Select(key => key.Key));
            Assert.Equal(0, notification);
            Assert.Equal(0, persistent);
        }

        [Fact]
        public async Task KeyboardRun_NativePumpPreservesRepeatedKeyAndWheelFifo()
        {
            var order = new List<string>();
            var sink = new RecordingOverlaySink
            {
                WorkspaceKeyObserved = keyInfo =>
                {
                    if (keyInfo.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
                        order.Add(keyInfo.Key.ToString());
                }
            };
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.Register(ConsoleKey.A, "A", () =>
            {
                order.Add("A");
                return Task.CompletedTask;
            });
            KeyboardManager.Register(ConsoleKey.B, "B", () =>
            {
                order.Add("B");
                return Task.CompletedTask;
            });
            var session = new FakeConsoleInputSession();
            session.Enqueue(
                ConsoleInputEvent.Key(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)),
                ConsoleInputEvent.Key(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)),
                ConsoleInputEvent.Key(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)),
                ConsoleInputEvent.MouseWheel(120, 0, isHorizontal: false),
                ConsoleInputEvent.Key(new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false)));
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => session;

            var run = KeyboardManager.RunAsync(CancellationToken.None);
            await session.InputsDrained.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Equal(["A", "A", "A", "UpArrow", "B"], order);
        }

        [Fact]
        public async Task KeyboardRun_NoInputSupportsStopNestedSuspensionConcurrentGuardAndSequentialRun()
        {
            var factoryCalls = 0;
            var first = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () =>
            {
                factoryCalls++;
                return first;
            };
            var firstRun = KeyboardManager.RunAsync(CancellationToken.None);
            await first.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() => KeyboardManager.RunAsync(CancellationToken.None));
            Assert.Equal(1, factoryCalls);

            var outer = KeyboardManager.SuspendInput();
            var inner = KeyboardManager.SuspendInput();
            Assert.Equal(1, first.SuspendCount);
            inner.Dispose();
            Assert.Equal(0, first.ResumeCount);
            outer.Dispose();
            Assert.Equal(1, first.ResumeCount);

            KeyboardManager.Stop();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, first.DisposeCount);

            var second = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => second;
            var secondRun = KeyboardManager.RunAsync(CancellationToken.None);
            await second.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await secondRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, second.DisposeCount);
        }

        [Fact]
        public async Task KeyboardRun_CancellationReleasesSessionAndSingletonLease()
        {
            using var cts = new CancellationTokenSource();
            var first = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => first;
            var firstRun = KeyboardManager.RunAsync(cts.Token);
            await first.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            cts.Cancel();

            await firstRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(KeyboardManager.IsRunning);
            Assert.Equal(1, first.DisposeCount);

            var second = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => second;
            var secondRun = KeyboardManager.RunAsync(CancellationToken.None);
            await second.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await secondRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, second.DisposeCount);
        }

        [Fact]
        public async Task KeyboardRun_NestedSuspensionDropsQueuedWheelAndPreservesKeyboardFifo()
        {
            var order = new List<string>();
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.Register(ConsoleKey.A, "A", () =>
            {
                order.Add("A");
                return Task.CompletedTask;
            });
            KeyboardManager.Register(ConsoleKey.B, "B", () =>
            {
                order.Add("B");
                handled.TrySetResult();
                return Task.CompletedTask;
            });
            var session = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => session;
            var run = KeyboardManager.RunAsync(CancellationToken.None);
            await session.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            var outer = KeyboardManager.SuspendInput();
            var inner = KeyboardManager.SuspendInput();
            session.Enqueue(
                ConsoleInputEvent.Key(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)),
                ConsoleInputEvent.MouseWheel(120, 0, isHorizontal: false),
                ConsoleInputEvent.Key(new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false)));
            inner.Dispose();
            Assert.Equal(0, session.ResumeCount);
            Assert.Empty(order);
            outer.Dispose();

            await handled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Equal(["A", "B"], order);
            Assert.DoesNotContain(
                sink.WorkspaceKeys,
                key => key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow);
            Assert.Equal(1, session.ResumeCount);
        }

        [Fact]
        public async Task KeyboardRun_RefreshConsoleInputModeReappliesNativeSessionMode()
        {
            var session = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => session;
            var run = KeyboardManager.RunAsync(CancellationToken.None);
            await session.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            KeyboardManager.RefreshConsoleInputMode();

            KeyboardManager.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, session.RefreshModeCount);
        }

        [Fact]
        public async Task KeyboardRun_StopCancelsBorrowedReadKeyAndReleasesSingletonLease()
        {
            var first = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => first;
            var firstRun = KeyboardManager.RunAsync(CancellationToken.None);
            await first.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var readTask = Task.Run(() => LiveDisplayConsole.ReadKey(intercept: true));
            await first.SuspendedReadAttempted.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);

            KeyboardManager.Stop();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => readTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            await firstRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(KeyboardManager.IsRunning);
            Assert.Equal(1, first.ResumeCount);
            Assert.Equal(1, first.DisposeCount);

            var second = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => second;
            var secondRun = KeyboardManager.RunAsync(CancellationToken.None);
            await second.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await secondRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, second.DisposeCount);
        }

        [Fact]
        public async Task LiveDisplayConsole_ReadLineUsesSuspendedNativeSession()
        {
            var session = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => session;
            var run = KeyboardManager.RunAsync(CancellationToken.None);
            await session.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            session.EnqueueSuspendedKeys(
                new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false),
                new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false),
                new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                new ConsoleKeyInfo('o', ConsoleKey.O, false, false, false),
                new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

            var value = await Task.Run(LiveDisplayConsole.ReadLine)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            KeyboardManager.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal("ho", value);
            Assert.Equal(1, session.SuspendCount);
            Assert.Equal(1, session.ResumeCount);
        }

        [Fact]
        public async Task KeyboardRun_ReleasesSingletonLeaseAfterReadAndRestoreFailures()
        {
            var readFailure = new WindowsConsoleInputException("ReadConsoleInputExW", 5);
            var readFailing = new FakeConsoleInputSession
            {
                ReadFailure = readFailure,
                DisposeFailure = new WindowsConsoleInputException("SetConsoleMode(restore)", 6)
            };
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => readFailing;

            var observedReadFailure = await Assert.ThrowsAsync<WindowsConsoleInputException>(
                () => KeyboardManager.RunAsync(CancellationToken.None));
            Assert.Same(readFailure, observedReadFailure);
            Assert.Equal(1, readFailing.DisposeCount);

            var restoreFailure = new WindowsConsoleInputException("SetConsoleMode(restore)", 6);
            var restoreFailing = new FakeConsoleInputSession { DisposeFailure = restoreFailure };
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => restoreFailing;
            var restoreRun = KeyboardManager.RunAsync(CancellationToken.None);
            await restoreFailing.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            var observedRestoreFailure = await Assert.ThrowsAsync<WindowsConsoleInputException>(
                () => restoreRun);
            Assert.Same(restoreFailure, observedRestoreFailure);
            Assert.Equal(1, restoreFailing.DisposeCount);

            var final = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => final;
            var finalRun = KeyboardManager.RunAsync(CancellationToken.None);
            await final.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await finalRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, final.DisposeCount);
        }

        [Fact]
        public async Task KeyboardRun_InitializationFailureFallsBackAndReleasesSingletonLease()
        {
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () =>
                throw new WindowsConsoleInputException("GetConsoleMode", 5);

            var failedInitializationRun = KeyboardManager.RunAsync(CancellationToken.None);
            KeyboardManager.Stop();
            await failedInitializationRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            var final = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => final;
            var finalRun = KeyboardManager.RunAsync(CancellationToken.None);
            await final.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await finalRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, final.DisposeCount);
        }

        [Fact]
        public async Task KeyboardRun_ExistingSuspensionSuspendsNewSessionExactlyOnce()
        {
            using var suspension = KeyboardManager.SuspendInput();
            var session = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => session;

            var run = KeyboardManager.RunAsync(CancellationToken.None);
            KeyboardManager.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.Equal(1, session.SuspendCount);
            Assert.Equal(0, session.ResumeCount);
            Assert.Equal(1, session.DisposeCount);
        }

        [Fact]
        public async Task KeyboardRun_SetupSuspendFailureFallsBackAndReleasesSingletonLease()
        {
            var suspension = KeyboardManager.SuspendInput();
            var suspendFailure = new WindowsConsoleInputException("SetConsoleMode(suspended)", 5);
            var failing = new FakeConsoleInputSession { SuspendFailure = suspendFailure };
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => failing;
            try
            {
                var run = KeyboardManager.RunAsync(CancellationToken.None);
                KeyboardManager.Stop();
                await run.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                Assert.Equal(1, failing.SuspendCount);
                Assert.Equal(1, failing.DisposeCount);
            }
            finally
            {
                suspension.Dispose();
            }

            var final = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => final;
            var finalRun = KeyboardManager.RunAsync(CancellationToken.None);
            await final.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            KeyboardManager.Stop();
            await finalRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(1, final.DisposeCount);
        }

        [Fact]
        public async Task KeyboardRun_ResumeFailureDoesNotLeakSuspensionIntoNextRun()
        {
            var resumeFailure = new WindowsConsoleInputException("SetConsoleMode(active resume)", 5);
            var first = new FakeConsoleInputSession { ResumeFailure = resumeFailure };
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => first;
            var firstRun = KeyboardManager.RunAsync(CancellationToken.None);
            await first.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            var suspension = KeyboardManager.SuspendInput();

            var observed = Assert.Throws<WindowsConsoleInputException>(suspension.Dispose);
            Assert.Same(resumeFailure, observed);
            await firstRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(KeyboardManager.IsRunning);
            Assert.Equal(1, first.DisposeCount);

            var second = new FakeConsoleInputSession();
            KeyboardManager.ConsoleInputSessionFactoryOverrideForTests = () => second;
            var secondRun = KeyboardManager.RunAsync(CancellationToken.None);
            await second.FirstRead.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(0, second.SuspendCount);
            KeyboardManager.Stop();
            await secondRun.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        // ── ① FormatKeyCombo ────────────────────────────────────────────────

        [Fact]
        public void FormatKeyCombo_NoModifiers_ReturnsBareKeyName()
        {
            // 无修饰键时直接是 key.ToString()
            Assert.Equal("K", KeyboardManager.FormatKeyCombo(ConsoleKey.K, 0));
            Assert.Equal("F1", KeyboardManager.FormatKeyCombo(ConsoleKey.F1, 0));
        }

        [Fact]
        public void FormatKeyCombo_SingleModifier_PrependsPrefix()
        {
            Assert.Equal("Ctrl+K", KeyboardManager.FormatKeyCombo(ConsoleKey.K, ConsoleModifiers.Control));
            Assert.Equal("Alt+K", KeyboardManager.FormatKeyCombo(ConsoleKey.K, ConsoleModifiers.Alt));
            Assert.Equal("Shift+K", KeyboardManager.FormatKeyCombo(ConsoleKey.K, ConsoleModifiers.Shift));
        }

        [Fact]
        public void FormatKeyCombo_MultipleModifiers_OrderedCtrlAltShift()
        {
            // 拼接顺序固定为 Ctrl → Alt → Shift，与传入 flag 的顺序无关
            Assert.Equal("Ctrl+Alt+A",
                KeyboardManager.FormatKeyCombo(ConsoleKey.A, ConsoleModifiers.Control | ConsoleModifiers.Alt));
            Assert.Equal("Ctrl+Shift+A",
                KeyboardManager.FormatKeyCombo(ConsoleKey.A, ConsoleModifiers.Control | ConsoleModifiers.Shift));
            Assert.Equal("Alt+Shift+A",
                KeyboardManager.FormatKeyCombo(ConsoleKey.A, ConsoleModifiers.Alt | ConsoleModifiers.Shift));
            Assert.Equal("Ctrl+Alt+Shift+A",
                KeyboardManager.FormatKeyCombo(ConsoleKey.A,
                    ConsoleModifiers.Control | ConsoleModifiers.Alt | ConsoleModifiers.Shift));
            // flag 顺序反过来传，输出仍是固定的 Ctrl+Alt+Shift
            Assert.Equal("Ctrl+Alt+Shift+A",
                KeyboardManager.FormatKeyCombo(ConsoleKey.A,
                    ConsoleModifiers.Shift | ConsoleModifiers.Alt | ConsoleModifiers.Control));
        }

        [Theory]
        [InlineData(ConsoleKey.UpArrow, "↑")]
        [InlineData(ConsoleKey.DownArrow, "↓")]
        [InlineData(ConsoleKey.LeftArrow, "←")]
        [InlineData(ConsoleKey.RightArrow, "→")]
        public void FormatKeyCombo_ArrowKeys_MapToUnicodeGlyphs(ConsoleKey key, string glyph)
        {
            Assert.Equal(glyph, KeyboardManager.FormatKeyCombo(key, 0));
            // 带修饰键时前缀照拼，方向键仍替换为符号
            Assert.Equal("Ctrl+" + glyph, KeyboardManager.FormatKeyCombo(key, ConsoleModifiers.Control));
        }

        // ── ② 保留键校验 ─────────────────────────────────────────────────────

        [Theory]
        [InlineData(ConsoleKey.S)]
        [InlineData(ConsoleKey.Q)]
        [InlineData(ConsoleKey.Z)]
        public void Register_CtrlReservedKey_Throws(ConsoleKey key)
        {
            // Ctrl+S/Q/Z 被终端保留（XOFF/XON/Suspend），注册应抛 InvalidOperationException
            Assert.Throws<InvalidOperationException>(() =>
                KeyboardManager.Register(key, ConsoleModifiers.Control, "x", NoopHandler));
            // 抛异常后不应写入字典
            Assert.False(KeyboardManager.Hotkeys.ContainsKey((key, ConsoleModifiers.Control)));
        }

        [Fact]
        public void Register_CtrlReservedKey_WithExtraModifier_StillThrows()
        {
            // 校验用的是 HasFlag(Control)，叠加 Shift 仍命中保留键判定
            Assert.Throws<InvalidOperationException>(() =>
                KeyboardManager.Register(ConsoleKey.S, ConsoleModifiers.Control | ConsoleModifiers.Shift, "x", NoopHandler));
        }

        [Fact]
        public void Register_NonReservedCombos_Succeed()
        {
            // Ctrl+ 其它键、保留键但无 Ctrl、保留键配其它修饰键，都应正常注册
            KeyboardManager.Register(ConsoleKey.A, ConsoleModifiers.Control, "ctrl-a", NoopHandler);
            KeyboardManager.Register(ConsoleKey.S, 0, "bare-s", NoopHandler);          // 无 Ctrl 的 S
            KeyboardManager.Register(ConsoleKey.Q, ConsoleModifiers.Alt, "alt-q", NoopHandler); // Alt+Q 不受限

            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.A, ConsoleModifiers.Control)));
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.S, 0)));
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.Q, ConsoleModifiers.Alt)));
        }

        // ── ③ Register / Unregister / UnregisterAll 字典增删 ─────────────────

        [Fact]
        public void Register_NoModifierOverload_DefaultsToZeroModifiers()
        {
            KeyboardManager.Register(ConsoleKey.F5, "refresh", NoopHandler);
            // 无修饰键重载等价于 modifiers = 0
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.F5, 0)));
            Assert.Equal("refresh", KeyboardManager.Hotkeys[(ConsoleKey.F5, 0)].Description);
        }

        [Fact]
        public void Register_F1WithoutModifier_Succeeds()
        {
            KeyboardManager.Register(ConsoleKey.F1, "默认帮助", NoopHandler);

            var entry = KeyboardManager.Hotkeys[(ConsoleKey.F1, 0)];
            Assert.Equal("默认帮助", entry.Description);
        }

        [Fact]
        public void Register_StoresEntryFields()
        {
            KeyboardManager.Register(ConsoleKey.F1, ConsoleModifiers.Control, "帮助", NoopHandler);
            var entry = KeyboardManager.Hotkeys[(ConsoleKey.F1, ConsoleModifiers.Control)];
            Assert.Equal("帮助", entry.Description);
            Assert.Same(NoopHandler, entry.Handler);
            Assert.Null(entry.Owner);
        }

        [Fact]
        public async Task Register_ContextHandler_RoutesPopupToOverlaySink()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.Register(ConsoleKey.P, "popup", ctx =>
            {
                ctx.WriteLine("纯文本", ConsoleColor.Yellow)
                    .WriteLine("<green>Literal</green>");
                return Task.CompletedTask;
            });

            await PressAsync(ConsoleKey.P);

            Assert.NotNull(sink.Popup);
            Assert.NotNull(sink.Popup.ExpiresAt);
            Assert.Equal(["纯文本", "<green>Literal</green>"], sink.Popup.Lines.Select(x => x.Text).ToArray());
        }

        [Fact]
        public void PopupAutoCloseDelay_DefaultsToThreeSeconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(3), KeyboardManager.PopupAutoCloseDelay);
        }

        [Fact]
        public async Task Register_ContextHandler_AutoClosesPopupAfterDelay()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromMilliseconds(50);
            KeyboardManager.Register(ConsoleKey.P, "popup", ctx =>
            {
                ctx.WriteLine("自动关闭");
                return Task.CompletedTask;
            });

            await PressAsync(ConsoleKey.P);

            Assert.NotNull(sink.Popup);
            await sink.WaitForHiddenAsync();
        }

        [Fact]
        public async Task Register_ContextHandler_WithoutOverlaySink_DoesNotEscapeKeyboardLoop()
        {
            KeyboardManager.Register(ConsoleKey.P, "popup", ctx =>
            {
                ctx.WriteLine("不会静默丢失");
                return Task.CompletedTask;
            });

            await KeyboardManager.HandleKeyAsync(new ConsoleKeyInfo('p', ConsoleKey.P, shift: false, alt: false, control: false));
        }

        [Fact]
        public async Task HandleKey_PopupDoesNotConsumeModifierHotkey()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.Register(ConsoleKey.P, "popup", ctx =>
            {
                ctx.WriteLine("popup");
                return Task.CompletedTask;
            });
            await PressAsync(ConsoleKey.P);
            var triggered = false;
            KeyboardManager.Register(
                ConsoleKey.Enter,
                ConsoleModifiers.Control,
                "ctrl-enter",
                () =>
                {
                    triggered = true;
                    return Task.CompletedTask;
                });

            await KeyboardManager.HandleKeyAsync(new ConsoleKeyInfo('\n', ConsoleKey.Enter, shift: false, alt: false, control: true));

            Assert.True(triggered);
            Assert.Null(sink.Popup);
        }

        [Fact]
        public async Task HandleKey_PopupNavigationScrollsAndCloses()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
            KeyboardManager.Register(ConsoleKey.P, "popup", ctx =>
            {
                for (var i = 0; i < 10; i++)
                    ctx.WriteLine($"line {i}");
                return Task.CompletedTask;
            });

            await PressAsync(ConsoleKey.P);
            Assert.Equal(0, sink.Popup?.ScrollOffset);

            await PressAsync(ConsoleKey.DownArrow);
            Assert.Equal(1, sink.Popup?.ScrollOffset);

            await PressAsync(ConsoleKey.PageDown);
            Assert.Equal(6, sink.Popup?.ScrollOffset);

            await PressAsync(ConsoleKey.End);
            Assert.Equal(9, sink.Popup?.ScrollOffset);

            await PressAsync(ConsoleKey.Home);
            Assert.Equal(0, sink.Popup?.ScrollOffset);

            await PressAsync(ConsoleKey.Escape);
            Assert.Null(sink.Popup);
        }

        [Fact]
        public async Task HandleKey_SelectablePopupMovesSelectionAndConfirms()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
            var confirmedLineIndex = -1;
            KeyboardManager.ShowPopup(new KeyboardPopup(
                [
                    new KeyboardPopupLine("Workspaces", ConsoleColor.White),
                    new KeyboardPopupLine("* First", ConsoleColor.White),
                    new KeyboardPopupLine("  Second", ConsoleColor.White)
                ],
                Selection: new KeyboardPopupSelection([1, 2], 0, lineIndex =>
                {
                    confirmedLineIndex = lineIndex;
                    return Task.CompletedTask;
                })));

            Assert.NotNull(sink.Popup);
            Assert.Null(sink.Popup.ExpiresAt);
            Assert.Equal(0, sink.Popup.Selection?.SelectedIndex);

            await PressAsync(ConsoleKey.DownArrow);
            Assert.Equal(1, sink.Popup?.Selection?.SelectedIndex);

            await PressAsync(ConsoleKey.DownArrow);
            Assert.Equal(1, sink.Popup?.Selection?.SelectedIndex);

            await PressAsync(ConsoleKey.Enter);
            Assert.Equal(2, confirmedLineIndex);
            Assert.Null(sink.Popup);
        }

        [Fact]
        public async Task HandleKey_SelectablePopupDoesNotConsumeModifierHotkey()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
            KeyboardManager.ShowPopup(new KeyboardPopup(
                [
                    new KeyboardPopupLine("Workspaces", ConsoleColor.White),
                    new KeyboardPopupLine("* First", ConsoleColor.White)
                ],
                Selection: new KeyboardPopupSelection([1], 0, _ => Task.CompletedTask)));
            var triggered = false;
            KeyboardManager.Register(
                ConsoleKey.Enter,
                ConsoleModifiers.Control,
                "ctrl-enter",
                () =>
                {
                    triggered = true;
                    return Task.CompletedTask;
                });

            await KeyboardManager.HandleKeyAsync(new ConsoleKeyInfo('\n', ConsoleKey.Enter, shift: false, alt: false, control: true));

            Assert.True(triggered);
            Assert.Null(sink.Popup);
        }

        [Fact]
        public async Task HandleKey_EnterCommandInput_SubmitsTypedCommand()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            string? submitted = null;
            KeyboardManager.SetCommandHandler(command =>
            {
                submitted = command;
                return Task.CompletedTask;
            });

            await PressAsync(ConsoleKey.Enter);
            await TypeAsync('s', ConsoleKey.S);
            await TypeAsync('t', ConsoleKey.T);
            await PressAsync(ConsoleKey.Backspace);
            await TypeAsync('a', ConsoleKey.A);
            await PressAsync(ConsoleKey.Enter);

            Assert.Equal("sa", submitted);
            Assert.Null(sink.CommandInput);
        }

        [Fact]
        public async Task HandleKey_SlashCommandInput_StartsWithSlashAndCancelsWithEscape()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;

            await TypeAsync('/', ConsoleKey.Oem2);
            Assert.Equal("/", sink.CommandInput?.Text);

            await PressAsync(ConsoleKey.Escape);
            Assert.Null(sink.CommandInput);
        }

        [Fact]
        public async Task HandleKey_CommandInputAcceptsShiftTextAndSuppressesHotkeys()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            var triggered = false;
            KeyboardManager.Register(
                ConsoleKey.A,
                ConsoleModifiers.Shift,
                "shift-a",
                () =>
                {
                    triggered = true;
                    return Task.CompletedTask;
                });

            await PressAsync(ConsoleKey.Enter);
            await TypeAsync('A', ConsoleKey.A, ConsoleModifiers.Shift);

            Assert.False(triggered);
            Assert.Equal("A", sink.CommandInput?.Text);
        }

        [Fact]
        public async Task HandleKey_CommandInputSuppressesModifierHotkeys()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            var triggered = new List<string>();
            KeyboardManager.Register(ConsoleKey.K, ConsoleModifiers.Control, "ctrl-k", () =>
            {
                triggered.Add("ctrl");
                return Task.CompletedTask;
            });
            KeyboardManager.Register(ConsoleKey.P, ConsoleModifiers.Alt, "alt-p", () =>
            {
                triggered.Add("alt");
                return Task.CompletedTask;
            });

            await PressAsync(ConsoleKey.Enter);
            await PressAsync(ConsoleKey.K, ConsoleModifiers.Control);
            await PressAsync(ConsoleKey.P, ConsoleModifiers.Alt);

            Assert.Empty(triggered);
            Assert.Equal("", sink.CommandInput?.Text);
        }

        [Fact]
        public async Task HandleKey_CommandHistoryStoresSubmittedCommandsAndNavigates()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.SetCommandHandler(_ => Task.CompletedTask);

            await SubmitCommandAsync("first");
            await SubmitCommandAsync("second");

            await PressAsync(ConsoleKey.Enter);
            await PressAsync(ConsoleKey.UpArrow);
            Assert.Equal("second", sink.CommandInput?.Text);

            await PressAsync(ConsoleKey.UpArrow);
            Assert.Equal("first", sink.CommandInput?.Text);

            await PressAsync(ConsoleKey.UpArrow);
            Assert.Equal("first", sink.CommandInput?.Text);

            await PressAsync(ConsoleKey.DownArrow);
            Assert.Equal("second", sink.CommandInput?.Text);

            await PressAsync(ConsoleKey.DownArrow);
            Assert.Equal("", sink.CommandInput?.Text);
        }

        [Fact]
        public async Task HandleKey_CommandHistoryRestoresDraftAfterBrowsing()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.SetCommandHandler(_ => Task.CompletedTask);

            await SubmitCommandAsync("first");
            await SubmitCommandAsync("second");

            await PressAsync(ConsoleKey.Enter);
            foreach (var ch in "draft")
                await TypeAsync(ch, (ConsoleKey)char.ToUpperInvariant(ch));

            await PressAsync(ConsoleKey.UpArrow);
            Assert.Equal("second", sink.CommandInput?.Text);

            await PressAsync(ConsoleKey.DownArrow);
            Assert.Equal("draft", sink.CommandInput?.Text);
        }

        [Fact]
        public async Task HandleKey_CommandCompletionUniqueCandidateReplacesText()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.SetCommandHandler(
                _ => Task.CompletedTask,
                input => input == "/p" ? ["/plugin"] : []);

            await TypeAsync('/', ConsoleKey.Oem2);
            await TypeAsync('p', ConsoleKey.P);
            await PressAsync(ConsoleKey.Tab);

            Assert.Equal("/plugin", sink.CommandInput?.Text);
            Assert.Empty(sink.CommandInput?.CompletionCandidates ?? []);
        }

        [Fact]
        public async Task HandleKey_CommandCompletionMultipleCandidatesUsesCommonPrefixAndShowsCandidates()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.SetCommandHandler(
                _ => Task.CompletedTask,
                input => input == "/p" ? ["/plugin list", "/plugin load"] : []);

            await TypeAsync('/', ConsoleKey.Oem2);
            await TypeAsync('p', ConsoleKey.P);
            await PressAsync(ConsoleKey.Tab);

            Assert.Equal("/plugin l", sink.CommandInput?.Text);
            Assert.Equal(["/plugin list", "/plugin load"], sink.CommandInput?.CompletionCandidates);
        }

        [Fact]
        public async Task HandleKey_CommandCompletionNoCandidatesKeepsText()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.SetCommandHandler(
                _ => Task.CompletedTask,
                _ => []);

            await TypeAsync('/', ConsoleKey.Oem2);
            await TypeAsync('x', ConsoleKey.X);
            await PressAsync(ConsoleKey.Tab);

            Assert.Equal("/x", sink.CommandInput?.Text);
            Assert.Empty(sink.CommandInput?.CompletionCandidates ?? []);
        }

        [Fact]
        public async Task HandleKey_CommandCompletionClearsOnEditAndCancel()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.SetCommandHandler(
                _ => Task.CompletedTask,
                input => input == "/p" ? ["/plugin list", "/plugin load"] : []);

            await TypeAsync('/', ConsoleKey.Oem2);
            await TypeAsync('p', ConsoleKey.P);
            await PressAsync(ConsoleKey.Tab);
            Assert.NotEmpty(sink.CommandInput?.CompletionCandidates ?? []);

            await TypeAsync('o', ConsoleKey.O);
            Assert.Equal("/plugin lo", sink.CommandInput?.Text);
            Assert.Empty(sink.CommandInput?.CompletionCandidates ?? []);

            await PressAsync(ConsoleKey.Tab);
            await PressAsync(ConsoleKey.Escape);
            Assert.Null(sink.CommandInput);
        }

        [Fact]
        public async Task Register_ContextHandler_OldAutoCloseDoesNotCloseNewPopup()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromMilliseconds(500);
            var text = "first";
            KeyboardManager.Register(ConsoleKey.P, "popup", ctx =>
            {
                ctx.WriteLine(text);
                return Task.CompletedTask;
            });

            await PressAsync(ConsoleKey.P);
            await Task.Delay(250, TestContext.Current.CancellationToken);
            text = "second";
            await PressAsync(ConsoleKey.P);
            await Task.Delay(350, TestContext.Current.CancellationToken);

            Assert.NotNull(sink.Popup);
            Assert.Equal("second", sink.Popup.Lines.Single().Text);

            await sink.WaitForHiddenAsync();
        }

        [Fact]
        public async Task HandleKey_TransientPriorityIsCommandPopupNotificationThenPersistent()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
            var persistent = 0;
            var notification = 0;
            var popup = 0;
            KeyboardManager.Register(ConsoleKey.F8, "persistent", () =>
            {
                persistent++;
                return Task.CompletedTask;
            });
            var persistentEntry = KeyboardManager.Hotkeys[(ConsoleKey.F8, 0)];

            var uiHost = new UiHost();
            var output = uiHost.ForPlugin("Priority");
            var workspace = output.CreateWorkspace("Priority");
            output.Notify(
                workspace,
                "notification",
                ttl: TimeSpan.FromMinutes(1),
                shortcuts: new LiveDisplayShortcut(ConsoleKey.F8, () =>
                {
                    notification++;
                    return Task.CompletedTask;
                }));

            await PressAsync(ConsoleKey.Enter);
            await PressAsync(ConsoleKey.F8);
            Assert.Equal((0, 0, 0), (popup, notification, persistent));
            await PressAsync(ConsoleKey.Escape);

            KeyboardManager.ShowPopup(new KeyboardHandlerContext()
                .WriteLine("popup")
                .BindShortcut(new LiveDisplayShortcut(ConsoleKey.F8, () =>
                {
                    popup++;
                    return Task.CompletedTask;
                })));
            await PressAsync(ConsoleKey.F8);
            await PressAsync(ConsoleKey.F8);
            Assert.Equal((2, 0, 0), (popup, notification, persistent));
            Assert.NotNull(sink.Popup);

            await PressAsync(ConsoleKey.Escape);
            await PressAsync(ConsoleKey.F8);
            Assert.Equal((2, 1, 0), (popup, notification, persistent));

            output.RemoveWorkspace(workspace);
            await PressAsync(ConsoleKey.F8);
            Assert.Equal((2, 1, 1), (popup, notification, persistent));
            Assert.Same(persistentEntry, KeyboardManager.Hotkeys[(ConsoleKey.F8, 0)]);
            Assert.Single(KeyboardManager.Hotkeys);
        }

        [Fact]
        public async Task HandleKey_PopupBuiltInKeyPrecedesPersistentHotkey()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
            var persistent = 0;
            var notification = 0;
            KeyboardManager.Register(ConsoleKey.Enter, "persistent", () =>
            {
                persistent++;
                return Task.CompletedTask;
            });
            var uiHost = new UiHost();
            var output = uiHost.ForPlugin("Built-in priority");
            var workspace = output.CreateWorkspace("Built-in priority");
            output.Notify(
                workspace,
                "notification",
                ttl: TimeSpan.FromMinutes(1),
                shortcuts: new LiveDisplayShortcut(ConsoleKey.Enter, () =>
                {
                    notification++;
                    return Task.CompletedTask;
                }));
            KeyboardManager.ShowPopup(new KeyboardHandlerContext().WriteLine("popup"));

            await PressAsync(ConsoleKey.Enter);

            Assert.Equal((1, 0), (notification, persistent));
            Assert.NotNull(sink.Popup);

            output.RemoveWorkspace(workspace);
            await PressAsync(ConsoleKey.Enter);

            Assert.Equal((1, 0), (notification, persistent));
            Assert.Null(sink.Popup);
        }

        [Fact]
        public async Task HandleKey_TransientShortcutMatchesKeyAndModifiersExactly()
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
        public async Task PopupShortcut_RepeatsWithoutClosingOrRefreshingExpiry()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(2);
            var invocations = 0;
            KeyboardManager.ShowPopup(new KeyboardHandlerContext()
                .WriteLine("popup")
                .BindShortcut(new LiveDisplayShortcut(ConsoleKey.F8, () =>
                {
                    invocations++;
                    return Task.CompletedTask;
                })));
            var expiresAt = sink.Popup?.ExpiresAt;

            await PressAsync(ConsoleKey.F8);
            await PressAsync(ConsoleKey.F8);

            Assert.Equal(2, invocations);
            Assert.NotNull(sink.Popup);
            Assert.Equal(expiresAt, sink.Popup.ExpiresAt);
        }

        [Fact]
        public async Task NotificationShortcut_RepeatsWithoutExtendingTtlThenPersistentResumes()
        {
            var persistent = 0;
            var transient = 0;
            KeyboardManager.Register(ConsoleKey.F8, "persistent", () =>
            {
                persistent++;
                return Task.CompletedTask;
            });
            var uiHost = new UiHost();
            var output = uiHost.ForPlugin("TTL");
            var workspace = output.CreateWorkspace("TTL");
            output.Notify(
                workspace,
                "short",
                ttl: TimeSpan.FromMilliseconds(240),
                shortcuts: new LiveDisplayShortcut(ConsoleKey.F8, () =>
                {
                    transient++;
                    return Task.CompletedTask;
                }));

            await PressAsync(ConsoleKey.F8);
            await Task.Delay(140, TestContext.Current.CancellationToken);
            await PressAsync(ConsoleKey.F8);
            await Task.Delay(140, TestContext.Current.CancellationToken);
            await PressAsync(ConsoleKey.F8);

            Assert.Equal((2, 1), (transient, persistent));
        }

        [Fact]
        public async Task NotificationShortcut_LatestWinsWhileInactiveAndNeverRendered()
        {
            var invoked = new List<string>();
            var uiHost = new UiHost();
            var output = uiHost.ForPlugin("Latest");
            var inactive = output.CreateWorkspace("Inactive");
            var active = output.CreateWorkspace("Active");
            output.SwitchWorkspace(active);
            output.Notify(
                inactive,
                "old",
                ttl: TimeSpan.FromMinutes(1),
                shortcuts: new LiveDisplayShortcut(ConsoleKey.F9, () =>
                {
                    invoked.Add("old");
                    return Task.CompletedTask;
                }));
            output.Notify(
                inactive,
                "new",
                ttl: TimeSpan.FromMilliseconds(160),
                shortcuts: new LiveDisplayShortcut(ConsoleKey.F9, () =>
                {
                    invoked.Add("new");
                    return Task.CompletedTask;
                }));

            await PressAsync(ConsoleKey.F9);
            await Task.Delay(220, TestContext.Current.CancellationToken);
            await PressAsync(ConsoleKey.F9);

            Assert.Equal(["new", "old"], invoked);
        }

        [Fact]
        public async Task PopupShortcut_ReplacementSurvivesOldAutoCloseTimer()
        {
            var sink = new RecordingOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromMilliseconds(400);
            var invoked = new List<string>();
            KeyboardManager.ShowPopup(new KeyboardHandlerContext()
                .WriteLine("old")
                .BindShortcut(new LiveDisplayShortcut(ConsoleKey.F8, () =>
                {
                    invoked.Add("old");
                    return Task.CompletedTask;
                })));
            await Task.Delay(220, TestContext.Current.CancellationToken);
            KeyboardManager.ShowPopup(new KeyboardHandlerContext()
                .WriteLine("new")
                .BindShortcut(new LiveDisplayShortcut(ConsoleKey.F8, () =>
                {
                    invoked.Add("new");
                    return Task.CompletedTask;
                })));
            await Task.Delay(220, TestContext.Current.CancellationToken);

            await PressAsync(ConsoleKey.F8);

            Assert.Equal(["new"], invoked);
            Assert.Equal("new", sink.Popup?.Lines.Single().Text);
        }

        [Fact]
        public async Task PopupShortcut_ConcurrentCloseCannotHideOrReleaseReplacement()
        {
            var sink = new BlockingHideOverlaySink();
            KeyboardManager.OverlaySink = sink;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.Zero;
            var invoked = new List<string>();
            KeyboardManager.ShowPopup(new KeyboardHandlerContext()
                .WriteLine("old")
                .BindShortcut(new LiveDisplayShortcut(ConsoleKey.F8, () =>
                {
                    invoked.Add("old");
                    return Task.CompletedTask;
                })));

            var close = Task.Run(() => PressAsync(ConsoleKey.Escape), TestContext.Current.CancellationToken);
            try
            {
                await sink.HideEntered.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                KeyboardManager.ShowPopup(new KeyboardHandlerContext()
                    .WriteLine("new")
                    .BindShortcut(new LiveDisplayShortcut(ConsoleKey.F8, () =>
                    {
                        invoked.Add("new");
                        return Task.CompletedTask;
                    })));
            }
            finally
            {
                sink.ReleaseHide();
            }
            await close;

            await PressAsync(ConsoleKey.F8);

            Assert.Equal(["new"], invoked);
            Assert.Equal("new", sink.Popup?.Lines.Single().Text);
        }

        [Fact]
        public async Task TransientHandlerExceptionUsesKeyboardErrorPathAndLoopContinues()
        {
            var attempts = 0;
            var uiHost = new UiHost();
            var output = uiHost.ForPlugin("Failure");
            var workspace = output.CreateWorkspace("Failure");
            output.Notify(
                workspace,
                "throws",
                ttl: TimeSpan.FromMinutes(1),
                shortcuts: new LiveDisplayShortcut(ConsoleKey.F10, () =>
                {
                    attempts++;
                    throw new InvalidOperationException("boom");
                }));

            var first = await Record.ExceptionAsync(() => PressAsync(ConsoleKey.F10));
            var second = await Record.ExceptionAsync(() => PressAsync(ConsoleKey.F10));

            Assert.Null(first);
            Assert.Null(second);
            Assert.Equal(2, attempts);
        }

        [Fact]
        public void Register_SameComboTwice_OverwritesEntry()
        {
            KeyboardManager.Register(ConsoleKey.K, ConsoleModifiers.Control, "first", NoopHandler);
            KeyboardManager.Register(ConsoleKey.K, ConsoleModifiers.Control, "second", NoopHandler);
            // 同一组合键重复注册覆盖先前入口，字典仍只有一条
            Assert.Single(KeyboardManager.Hotkeys);
            Assert.Equal("second", KeyboardManager.Hotkeys[(ConsoleKey.K, ConsoleModifiers.Control)].Description);
        }

        [Fact]
        public void Register_DifferentModifiers_AreSeparateKeys()
        {
            // 同一 ConsoleKey 配不同修饰键属于不同字典键，互不覆盖
            KeyboardManager.Register(ConsoleKey.K, 0, "bare", NoopHandler);
            KeyboardManager.Register(ConsoleKey.K, ConsoleModifiers.Control, "ctrl", NoopHandler);
            Assert.Equal(2, KeyboardManager.Hotkeys.Count);
        }

        [Fact]
        public void Unregister_ExistingCombo_RemovesAndReturnsTrue()
        {
            KeyboardManager.Register(ConsoleKey.K, ConsoleModifiers.Control, "x", NoopHandler);
            Assert.True(KeyboardManager.Unregister(ConsoleKey.K, ConsoleModifiers.Control));
            Assert.False(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.K, ConsoleModifiers.Control)));
        }

        [Fact]
        public void Unregister_ExpectedEntryDoesNotRemoveReplacementWithSameValues()
        {
            var handler = NoopHandler;
            var first = KeyboardManager.RegisterTracked(ConsoleKey.K, ConsoleModifiers.Control, "same", handler);
            var replacement = KeyboardManager.RegisterTracked(ConsoleKey.K, ConsoleModifiers.Control, "same", handler);

            Assert.False(KeyboardManager.Unregister(ConsoleKey.K, ConsoleModifiers.Control, first));
            Assert.Same(replacement, KeyboardManager.Hotkeys[(ConsoleKey.K, ConsoleModifiers.Control)]);
        }

        [Fact]
        public void Unregister_MissingCombo_ReturnsFalse()
        {
            // 原本不存在的组合键，移除返回 false
            Assert.False(KeyboardManager.Unregister(ConsoleKey.K, ConsoleModifiers.Control));
        }

        [Fact]
        public void Unregister_DefaultsToZeroModifiers()
        {
            KeyboardManager.Register(ConsoleKey.F5, "x", NoopHandler); // 注册到 (F5, 0)
            // Unregister 不传 modifiers 默认 0，应能精确命中
            Assert.True(KeyboardManager.Unregister(ConsoleKey.F5));
            Assert.Empty(KeyboardManager.Hotkeys);
        }

        [Fact]
        public void Unregister_WrongModifiers_DoesNotRemove()
        {
            KeyboardManager.Register(ConsoleKey.K, ConsoleModifiers.Control, "x", NoopHandler);
            // 修饰键不匹配不应误删
            Assert.False(KeyboardManager.Unregister(ConsoleKey.K, ConsoleModifiers.Alt));
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.K, ConsoleModifiers.Control)));
        }

        [Fact]
        public void UnregisterAll_ClearsEverything()
        {
            KeyboardManager.Register(ConsoleKey.A, "a", NoopHandler);
            KeyboardManager.Register(ConsoleKey.B, ConsoleModifiers.Control, "b", NoopHandler);
            KeyboardManager.UnregisterAll();
            Assert.Empty(KeyboardManager.Hotkeys);
        }

        [Fact]
        public void RegisterScope_StampsOwnerOnEntriesRegisteredInside()
        {
            var owner = new object();
            using (KeyboardManager.RegisterScope(owner))
            {
                KeyboardManager.Register(ConsoleKey.A, "a", NoopHandler);
            }

            Assert.Same(owner, KeyboardManager.Hotkeys[(ConsoleKey.A, 0)].Owner);
        }

        [Fact]
        public void RegisterScope_RestoresPreviousOwnerOnDispose()
        {
            var owner = new object();
            using (KeyboardManager.RegisterScope(owner))
            {
            }

            KeyboardManager.Register(ConsoleKey.B, "b", NoopHandler);

            Assert.Null(KeyboardManager.Hotkeys[(ConsoleKey.B, 0)].Owner);
        }

        [Fact]
        public void UnregisterByOwner_RemovesOnlyMatchingOwner_AndReturnsCount()
        {
            var ownerA = new object();
            var ownerB = new object();

            using (KeyboardManager.RegisterScope(ownerA))
            {
                KeyboardManager.Register(ConsoleKey.A, "a1", NoopHandler);
                KeyboardManager.Register(ConsoleKey.B, "a2", NoopHandler);
            }
            using (KeyboardManager.RegisterScope(ownerB))
            {
                KeyboardManager.Register(ConsoleKey.C, "b1", NoopHandler);
            }
            KeyboardManager.Register(ConsoleKey.D, "host", NoopHandler);

            Assert.Equal(2, KeyboardManager.UnregisterByOwner(ownerA));
            Assert.False(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.A, 0)));
            Assert.False(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.B, 0)));
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.C, 0)));
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.D, 0)));
        }

        [Fact]
        public void UnregisterByOwner_NoMatch_ReturnsZero()
        {
            KeyboardManager.Register(ConsoleKey.A, "host", NoopHandler);

            Assert.Equal(0, KeyboardManager.UnregisterByOwner(new object()));
            Assert.Single(KeyboardManager.Hotkeys);
        }

        [Fact]
        public void UnregisterByOwner_UsesReferenceEquality_NotValueEquality()
        {
            var ownerA = new OwnerKey(1);
            var ownerB = new OwnerKey(1);
            using (KeyboardManager.RegisterScope(ownerA))
            {
                KeyboardManager.Register(ConsoleKey.A, "a", NoopHandler);
            }

            Assert.Equal(0, KeyboardManager.UnregisterByOwner(ownerB));
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.A, 0)));
            Assert.Equal(1, KeyboardManager.UnregisterByOwner(ownerA));
        }

        sealed record OwnerKey(int Id);

        static Task PressAsync(ConsoleKey key, ConsoleModifiers modifiers = 0)
        {
            return KeyboardManager.HandleKeyAsync(new ConsoleKeyInfo(
                '\0',
                key,
                modifiers.HasFlag(ConsoleModifiers.Shift),
                modifiers.HasFlag(ConsoleModifiers.Alt),
                modifiers.HasFlag(ConsoleModifiers.Control)));
        }

        static Task TypeAsync(char keyChar, ConsoleKey key, ConsoleModifiers modifiers = 0)
        {
            return KeyboardManager.HandleKeyAsync(new ConsoleKeyInfo(
                keyChar,
                key,
                modifiers.HasFlag(ConsoleModifiers.Shift),
                modifiers.HasFlag(ConsoleModifiers.Alt),
                modifiers.HasFlag(ConsoleModifiers.Control)));
        }

        static async Task SubmitCommandAsync(string command)
        {
            await PressAsync(ConsoleKey.Enter);
            foreach (var ch in command)
                await TypeAsync(ch, (ConsoleKey)char.ToUpperInvariant(ch));
            await PressAsync(ConsoleKey.Enter);
        }

        sealed class FakeConsoleInputSession : IConsoleInputSession
        {
            readonly object sync = new();
            readonly TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly TaskCompletionSource inputsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly TaskCompletionSource suspendedReadAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly Queue<ConsoleInputEvent> inputs = [];
            readonly Queue<ConsoleKeyInfo> suspendedKeys = [];

            public Task FirstRead => firstRead.Task;
            public Task InputsDrained => inputsDrained.Task;
            public Task SuspendedReadAttempted => suspendedReadAttempted.Task;
            public Exception? ReadFailure { get; init; }
            public Exception? SuspendFailure { get; init; }
            public Exception? ResumeFailure { get; init; }
            public Exception? DisposeFailure { get; init; }
            public int SuspendCount { get; private set; }
            public int ResumeCount { get; private set; }
            public int DisposeCount { get; private set; }
            public int RefreshModeCount { get; private set; }
            public int SuspendedAvailabilityChecks { get; private set; }
            public int SuspendedReadCount { get; private set; }

            public void Enqueue(params ConsoleInputEvent[] values)
            {
                lock (sync)
                {
                    foreach (var value in values)
                        inputs.Enqueue(value);
                }
            }

            public void EnqueueSuspendedKeys(params ConsoleKeyInfo[] values)
            {
                lock (sync)
                {
                    foreach (var value in values)
                        suspendedKeys.Enqueue(value);
                }
            }

            public bool TryRead(out ConsoleInputEvent input)
            {
                firstRead.TrySetResult();
                if (ReadFailure is not null)
                    throw ReadFailure;

                lock (sync)
                {
                    if (inputs.TryDequeue(out input))
                        return true;
                }

                inputsDrained.TrySetResult();
                return false;
            }

            public bool IsSuspendedKeyAvailable()
            {
                lock (sync)
                {
                    SuspendedAvailabilityChecks++;
                    return suspendedKeys.Count > 0;
                }
            }

            public bool TryReadSuspendedKey(out ConsoleKeyInfo keyInfo)
            {
                suspendedReadAttempted.TrySetResult();
                lock (sync)
                {
                    SuspendedReadCount++;
                    return suspendedKeys.TryDequeue(out keyInfo);
                }
            }

            public void Suspend()
            {
                SuspendCount++;
                if (SuspendFailure is not null)
                    throw SuspendFailure;
            }

            public void Resume()
            {
                ResumeCount++;
                lock (sync)
                {
                    var preservedKeys = inputs
                        .Where(input => input.Kind == ConsoleInputEventKind.Key)
                        .ToArray();
                    inputs.Clear();
                    foreach (var input in preservedKeys)
                        inputs.Enqueue(input);
                }
                if (ResumeFailure is not null)
                    throw ResumeFailure;
            }

            public void RefreshMode() => RefreshModeCount++;

            public void Dispose()
            {
                DisposeCount++;
                if (DisposeFailure is not null)
                    throw DisposeFailure;
            }
        }

        sealed class RecordingOverlaySink : IKeyboardOverlaySink
        {
            readonly object sync = new();
            TaskCompletionSource hidden = new(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly List<ConsoleKeyInfo> workspaceKeys = [];
            KeyboardPopup? popup;
            KeyboardCommandInput? commandInput;
            int popupGeneration;

            public bool WorkspaceKeyHandled { get; set; }
            public Action<ConsoleKeyInfo>? WorkspaceKeyObserved { get; init; }

            public IReadOnlyList<ConsoleKeyInfo> WorkspaceKeys
            {
                get
                {
                    lock (sync)
                        return [.. workspaceKeys];
                }
            }

            public void ClearWorkspaceKeys()
            {
                lock (sync)
                    workspaceKeys.Clear();
            }

            public KeyboardPopup? Popup
            {
                get
                {
                    lock (sync)
                        return popup;
                }
            }

            public KeyboardCommandInput? CommandInput
            {
                get
                {
                    lock (sync)
                        return commandInput;
                }
            }

            public Task<bool> TryHandleWorkspaceKeyAsync(ConsoleKeyInfo keyInfo)
            {
                lock (sync)
                    workspaceKeys.Add(keyInfo);
                WorkspaceKeyObserved?.Invoke(keyInfo);
                return Task.FromResult(WorkspaceKeyHandled);
            }

            public void ShowPopup(KeyboardPopup popup, int generation)
            {
                lock (sync)
                {
                    if (generation < popupGeneration)
                        return;

                    popupGeneration = generation;
                    hidden = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    this.popup = popup;
                }
            }

            public void HidePopup(int generation)
            {
                TaskCompletionSource toComplete;
                lock (sync)
                {
                    if (generation < popupGeneration)
                        return;

                    popupGeneration = generation;
                    popup = null;
                    toComplete = hidden;
                }

                toComplete.TrySetResult();
            }

            public void ShowCommandInput(KeyboardCommandInput input)
            {
                lock (sync)
                    commandInput = input;
            }

            public void HideCommandInput()
            {
                lock (sync)
                    commandInput = null;
            }

            public Task WaitForHiddenAsync()
            {
                lock (sync)
                {
                    return popup is null ? Task.CompletedTask : hidden.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
        }

        sealed class BlockingHideOverlaySink : IKeyboardOverlaySink
        {
            readonly object sync = new();
            readonly TaskCompletionSource hideEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            readonly TaskCompletionSource releaseHide = new(TaskCreationOptions.RunContinuationsAsynchronously);
            KeyboardPopup? popup;
            int popupGeneration;

            public Task HideEntered => hideEntered.Task;

            public KeyboardPopup? Popup
            {
                get
                {
                    lock (sync)
                        return popup;
                }
            }

            public Task<bool> TryHandleWorkspaceKeyAsync(ConsoleKeyInfo keyInfo) => Task.FromResult(false);

            public void ShowPopup(KeyboardPopup popup, int generation)
            {
                lock (sync)
                {
                    if (generation < popupGeneration)
                        return;

                    popupGeneration = generation;
                    this.popup = popup;
                }
            }

            public void HidePopup(int generation)
            {
                hideEntered.TrySetResult();
                releaseHide.Task.GetAwaiter().GetResult();
                lock (sync)
                {
                    if (generation < popupGeneration)
                        return;

                    popupGeneration = generation;
                    popup = null;
                }
            }

            public void ShowCommandInput(KeyboardCommandInput input)
            {
            }

            public void HideCommandInput()
            {
            }

            public void ReleaseHide()
            {
                releaseHide.TrySetResult();
            }
        }

        // ── ⑤ Hotkeys 只读快照反映当前注册 ──────────────────────────────────

        [Fact]
        public void Hotkeys_ReflectsCurrentRegistrations()
        {
            Assert.Empty(KeyboardManager.Hotkeys);

            KeyboardManager.Register(ConsoleKey.A, "a", NoopHandler);
            Assert.Single(KeyboardManager.Hotkeys);
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.A, 0)));

            KeyboardManager.Register(ConsoleKey.B, ConsoleModifiers.Control, "b", NoopHandler);
            Assert.Equal(2, KeyboardManager.Hotkeys.Count);

            KeyboardManager.Unregister(ConsoleKey.A);
            Assert.Single(KeyboardManager.Hotkeys);
            Assert.False(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.A, 0)));
            Assert.True(KeyboardManager.Hotkeys.ContainsKey((ConsoleKey.B, ConsoleModifiers.Control)));
        }

        [Fact]
        public void Hotkeys_KeyTupleMatchesRegisteredKeyAndModifiers()
        {
            KeyboardManager.Register(ConsoleKey.K, ConsoleModifiers.Control | ConsoleModifiers.Shift, "x", NoopHandler);
            var (key, mods) = Assert.Single(KeyboardManager.Hotkeys).Key;
            Assert.Equal(ConsoleKey.K, key);
            Assert.Equal(ConsoleModifiers.Control | ConsoleModifiers.Shift, mods);
        }
    }
}
