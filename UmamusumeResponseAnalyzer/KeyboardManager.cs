using System.Collections.Concurrent;
using System.Reflection;
using UmamusumeResponseAnalyzer.LiveDisplay;

namespace UmamusumeResponseAnalyzer
{
    public static class KeyboardManager
    {
        public record HotkeyEntry(
            string Description,
            Func<Task> Handler,
            object? Owner = null,
            Assembly? DeclaringAssembly = null);

        const int PollIntervalMs = 50;

        static readonly ConcurrentDictionary<(ConsoleKey Key, ConsoleModifiers Modifiers), HotkeyEntry> hotkeys = [];
        static readonly object popupSync = new();
        static readonly object commandInputSync = new();

        static KeyboardPopup? activePopup;
        static CancellationTokenSource? runCts;
        static CancellationTokenSource? popupAutoCloseCts;
        static int inputSuspensionCount;
        static int popupGeneration;
        static readonly AsyncLocal<object?> registrationOwner = new();
        static string commandBuffer = string.Empty;
        static readonly List<string> commandHistory = [];
        static string commandDraft = string.Empty;
        static int commandHistoryIndex;
        static bool inCommandInput;
        static Func<string, Task>? commandHandler;
        static Func<string, IReadOnlyList<string>>? commandCompletionProvider;

        public static TimeSpan PopupAutoCloseDelay { get; set; } = TimeSpan.FromSeconds(3);
        internal static IKeyboardOverlaySink? OverlaySink { get; set; }

        public static void Register(
            ConsoleKey key,
            ConsoleModifiers modifiers,
            string description,
            Func<Task> handler)
        {
            RegisterCore(key, modifiers, description, handler);
        }

        public static void Register(
            ConsoleKey key,
            ConsoleModifiers modifiers,
            string description,
            Func<KeyboardHandlerContext, Task> handler)
        {
            var declaringAssembly = AssemblyOf(handler);
            RegisterCore(
                key,
                modifiers,
                description,
                async () =>
                {
                    var context = new KeyboardHandlerContext();
                    await handler(context);
                    ShowPopup(context.ToPopup());
                },
                declaringAssembly);
        }

        public static void Register(ConsoleKey key, string description, Func<Task> handler)
        {
            Register(key, 0, description, handler);
        }

        public static void Register(ConsoleKey key, string description, Func<KeyboardHandlerContext, Task> handler)
        {
            Register(key, 0, description, handler);
        }

        static void RegisterCore(
            ConsoleKey key,
            ConsoleModifiers modifiers,
            string description,
            Func<Task> handler,
            Assembly? declaringAssembly = null)
        {
            if (modifiers.HasFlag(ConsoleModifiers.Control) &&
                key is ConsoleKey.S or ConsoleKey.Q or ConsoleKey.Z)
            {
                throw new InvalidOperationException($"Ctrl+{key} 由终端保留，不能注册为热键。");
            }

            hotkeys[(key, modifiers)] = new(
                description,
                handler,
                registrationOwner.Value,
                declaringAssembly ?? AssemblyOf(handler));
        }

        public static bool Unregister(ConsoleKey key, ConsoleModifiers modifiers = 0)
        {
            return hotkeys.TryRemove((key, modifiers), out _);
        }

        public static void SetCommandHandler(Func<string, Task>? handler)
        {
            SetCommandHandler(handler, completionProvider: null);
        }

        public static void SetCommandHandler(
            Func<string, Task>? handler,
            Func<string, IReadOnlyList<string>>? completionProvider)
        {
            commandHandler = handler;
            commandCompletionProvider = completionProvider;
            if (handler is null)
            {
                CancelCommandInput();
                ClearCommandHistory();
            }
        }

        public static void UnregisterAll()
        {
            hotkeys.Clear();
        }

        public static IDisposable RegisterScope(object owner)
        {
            var previous = registrationOwner.Value;
            registrationOwner.Value = owner;
            return new RegistrationScope(previous);
        }

        public static int UnregisterByOwner(object owner)
        {
            var count = 0;
            foreach (var (combo, entry) in hotkeys)
            {
                if (ReferenceEquals(entry.Owner, owner) && hotkeys.TryRemove(combo, out _))
                    count++;
            }
            return count;
        }

        sealed class RegistrationScope(object? previous) : IDisposable
        {
            public void Dispose()
            {
                registrationOwner.Value = previous;
            }
        }

        static Assembly? AssemblyOf(Delegate handler)
        {
            return handler.Target?.GetType().Assembly ?? handler.Method.DeclaringType?.Assembly;
        }

        public static void ClearHandlersByAssembly(IReadOnlySet<Assembly> assemblies)
        {
            foreach (var (combo, entry) in hotkeys)
            {
                if (entry.DeclaringAssembly != null && assemblies.Contains(entry.DeclaringAssembly))
                    hotkeys.TryRemove(combo, out _);
            }

            var handler = commandHandler;
            var handlerAssembly = handler is null ? null : AssemblyOf(handler);
            var completionProvider = commandCompletionProvider;
            var completionProviderAssembly = completionProvider is null ? null : AssemblyOf(completionProvider);
            if ((handlerAssembly is not null && assemblies.Contains(handlerAssembly)) ||
                (completionProviderAssembly is not null && assemblies.Contains(completionProviderAssembly)))
            {
                SetCommandHandler(null);
            }
        }

        public static IReadOnlyDictionary<(ConsoleKey Key, ConsoleModifiers Modifiers), HotkeyEntry> Hotkeys => hotkeys;

        public static void Stop()
        {
            try
            {
                Volatile.Read(ref runCts)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public static IDisposable SuspendInput()
        {
            Interlocked.Increment(ref inputSuspensionCount);
            HidePopup();
            CancelCommandInput();
            return new InputSuspension();
        }

        public static string FormatKeyCombo(ConsoleKey key, ConsoleModifiers modifiers)
        {
            var parts = new List<string>(3);
            if (modifiers.HasFlag(ConsoleModifiers.Control))
                parts.Add("Ctrl");
            if (modifiers.HasFlag(ConsoleModifiers.Alt))
                parts.Add("Alt");
            if (modifiers.HasFlag(ConsoleModifiers.Shift))
                parts.Add("Shift");

            var keyName = key switch
            {
                ConsoleKey.Oem1 => ";",
                ConsoleKey.Oem2 => "/",
                ConsoleKey.Oem3 => "`",
                ConsoleKey.Oem4 => "[",
                ConsoleKey.Oem5 => "\\",
                ConsoleKey.Oem6 => "]",
                ConsoleKey.Oem7 => "'",
                ConsoleKey.OemPlus => "+",
                ConsoleKey.OemMinus => "-",
                ConsoleKey.OemComma => ",",
                ConsoleKey.OemPeriod => ".",
                ConsoleKey.Spacebar => "Space",
                ConsoleKey.Enter => "Enter",
                ConsoleKey.Escape => "Esc",
                ConsoleKey.UpArrow => "↑",
                ConsoleKey.DownArrow => "↓",
                ConsoleKey.LeftArrow => "←",
                ConsoleKey.RightArrow => "→",
                _ => key.ToString()
            };
            parts.Add(keyName);

            return string.Join("+", parts);
        }

        public static async Task RunAsync(CancellationToken cancellationToken)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (Interlocked.CompareExchange(ref runCts, linkedCts, null) is not null)
                throw new InvalidOperationException("KeyboardManager.RunAsync 已在运行中。");

            var treatControlCAsInputChanged = false;
            var previousTreatControlCAsInput = false;
            try
            {
                previousTreatControlCAsInput = Console.TreatControlCAsInput;
                Console.TreatControlCAsInput = true;
                treatControlCAsInputChanged = true;
            }
            catch (IOException)
            {
            }

            try
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    if (Volatile.Read(ref inputSuspensionCount) > 0)
                    {
                        await DelayPollAsync(linkedCts.Token);
                        continue;
                    }

                    if (!TryKeyAvailable())
                    {
                        await DelayPollAsync(linkedCts.Token);
                        continue;
                    }

                    ConsoleKeyInfo keyInfo;
                    try
                    {
                        keyInfo = Console.ReadKey(intercept: true);
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    await HandleKeyAsync(keyInfo);
                }
            }
            finally
            {
                if (treatControlCAsInputChanged)
                {
                    try
                    {
                        Console.TreatControlCAsInput = previousTreatControlCAsInput;
                    }
                    catch (IOException)
                    {
                    }
                }

                HidePopup();
                Interlocked.CompareExchange(ref runCts, null, linkedCts);
            }
        }

        internal static async Task HandleKeyAsync(ConsoleKeyInfo keyInfo)
        {
            if (IsInCommandInput())
            {
                await HandleCommandInputKeyAsync(keyInfo);
                return;
            }

            if (HasActivePopup())
            {
                if (await HandlePopupKeyAsync(keyInfo))
                    return;

                if (await TryHandleHotkeyAsync(keyInfo))
                    return;

                return;
            }

            if (await TryHandleHotkeyAsync(keyInfo))
                return;

            TryBeginCommandInput(keyInfo);
        }

        static async Task HandleCommandInputKeyAsync(ConsoleKeyInfo keyInfo)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.Enter:
                    var (command, handler) = EndCommandInputForSubmit();
                    if (handler is not null && !string.IsNullOrWhiteSpace(command))
                        await InvokeSafely(() => handler(command));
                    break;

                case ConsoleKey.Escape:
                    CancelCommandInput();
                    break;

                case ConsoleKey.Backspace:
                    if (!RemoveLastCommandInputChar())
                        CancelCommandInput();
                    break;

                case ConsoleKey.UpArrow:
                    MoveCommandHistory(-1);
                    break;

                case ConsoleKey.DownArrow:
                    MoveCommandHistory(1);
                    break;

                case ConsoleKey.Tab:
                    CompleteCommandInput();
                    break;

                default:
                    if (!char.IsControl(keyInfo.KeyChar))
                        AppendCommandInput(keyInfo.KeyChar);
                    break;
            }
        }

        static bool TryBeginCommandInput(ConsoleKeyInfo keyInfo)
        {
            if (keyInfo.Modifiers != 0)
                return false;

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                BeginCommandInput(string.Empty);
                return true;
            }

            if (keyInfo.Key is ConsoleKey.Oem2 or ConsoleKey.Divide && keyInfo.KeyChar == '/')
            {
                BeginCommandInput("/");
                return true;
            }

            return false;
        }

        static bool IsInCommandInput()
        {
            lock (commandInputSync)
                return inCommandInput;
        }

        static void BeginCommandInput(string initialText)
        {
            HidePopup();
            lock (commandInputSync)
            {
                inCommandInput = true;
                commandBuffer = initialText;
                commandHistoryIndex = commandHistory.Count;
                commandDraft = initialText;
            }

            OverlaySink?.ShowCommandInput(new KeyboardCommandInput(initialText));
        }

        static void AppendCommandInput(char keyChar)
        {
            string text;
            lock (commandInputSync)
            {
                if (!inCommandInput)
                    return;

                commandBuffer += keyChar;
                text = commandBuffer;
                ResetCommandHistoryNavigationLocked(text);
            }

            OverlaySink?.ShowCommandInput(new KeyboardCommandInput(text));
        }

        static bool RemoveLastCommandInputChar()
        {
            string text;
            lock (commandInputSync)
            {
                if (!inCommandInput || commandBuffer.Length == 0)
                    return false;

                commandBuffer = commandBuffer[..^1];
                text = commandBuffer;
                ResetCommandHistoryNavigationLocked(text);
            }

            OverlaySink?.ShowCommandInput(new KeyboardCommandInput(text));
            return true;
        }

        static void MoveCommandHistory(int delta)
        {
            string text;
            lock (commandInputSync)
            {
                if (!inCommandInput || commandHistory.Count == 0)
                    return;

                if (delta < 0)
                {
                    if (commandHistoryIndex == commandHistory.Count)
                        commandDraft = commandBuffer;

                    commandHistoryIndex = Math.Max(0, commandHistoryIndex - 1);
                    commandBuffer = commandHistory[commandHistoryIndex];
                }
                else
                {
                    if (commandHistoryIndex >= commandHistory.Count)
                        return;

                    commandHistoryIndex++;
                    commandBuffer = commandHistoryIndex == commandHistory.Count
                        ? commandDraft
                        : commandHistory[commandHistoryIndex];
                }

                text = commandBuffer;
            }

            OverlaySink?.ShowCommandInput(new KeyboardCommandInput(text));
        }

        static void CompleteCommandInput()
        {
            string text;
            Func<string, IReadOnlyList<string>>? provider;
            lock (commandInputSync)
            {
                if (!inCommandInput)
                    return;

                text = commandBuffer;
                provider = commandCompletionProvider;
            }

            if (provider is null)
            {
                ClearCommandCompletionCandidates();
                return;
            }

            IReadOnlyList<string> candidates;
            try
            {
                candidates = provider(text);
            }
            catch (Exception ex)
            {
                LiveDisplayConsole.Notify("Keyboard", $"命令补全失败: {ex.Message}", LiveDisplaySeverity.Error);
                LiveDisplayConsole.LogException("Keyboard", ex);
                ClearCommandCompletionCandidates();
                return;
            }

            ApplyCommandCompletion(text, candidates);
        }

        static void ApplyCommandCompletion(string originalText, IReadOnlyList<string> candidates)
        {
            string text;
            IReadOnlyList<string> shownCandidates = [];
            lock (commandInputSync)
            {
                if (!inCommandInput || commandBuffer != originalText)
                    return;

                if (candidates.Count == 0)
                {
                    text = commandBuffer;
                }
                else if (candidates.Count == 1)
                {
                    commandBuffer = candidates[0];
                    text = commandBuffer;
                    ResetCommandHistoryNavigationLocked(text);
                }
                else
                {
                    var commonPrefix = LongestCommonPrefix(candidates);
                    if (commonPrefix.Length > commandBuffer.Length)
                        commandBuffer = commonPrefix;

                    text = commandBuffer;
                    shownCandidates = candidates.ToArray();
                    ResetCommandHistoryNavigationLocked(text);
                }
            }

            OverlaySink?.ShowCommandInput(new KeyboardCommandInput(text, shownCandidates));
        }

        static (string Command, Func<string, Task>? Handler) EndCommandInputForSubmit()
        {
            string command;
            Func<string, Task>? handler;
            lock (commandInputSync)
            {
                command = commandBuffer;
                if (!string.IsNullOrWhiteSpace(command))
                    commandHistory.Add(command);
                commandBuffer = string.Empty;
                inCommandInput = false;
                handler = commandHandler;
                commandHistoryIndex = commandHistory.Count;
                commandDraft = string.Empty;
            }

            OverlaySink?.HideCommandInput();
            return (command, handler);
        }

        static void CancelCommandInput()
        {
            var shouldHide = false;
            lock (commandInputSync)
            {
                if (inCommandInput || commandBuffer.Length > 0)
                    shouldHide = true;

                inCommandInput = false;
                commandBuffer = string.Empty;
                commandHistoryIndex = commandHistory.Count;
                commandDraft = string.Empty;
            }

            if (shouldHide)
                OverlaySink?.HideCommandInput();
        }

        static void ClearCommandHistory()
        {
            lock (commandInputSync)
            {
                commandHistory.Clear();
                commandHistoryIndex = 0;
                commandDraft = string.Empty;
            }
        }

        static void ClearCommandCompletionCandidates()
        {
            string text;
            lock (commandInputSync)
            {
                if (!inCommandInput)
                    return;

                text = commandBuffer;
            }

            OverlaySink?.ShowCommandInput(new KeyboardCommandInput(text));
        }

        static void ResetCommandHistoryNavigationLocked(string text)
        {
            commandHistoryIndex = commandHistory.Count;
            commandDraft = text;
        }

        static string LongestCommonPrefix(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return string.Empty;

            var prefix = values[0];
            for (var i = 1; i < values.Count && prefix.Length > 0; i++)
            {
                var value = values[i];
                var length = Math.Min(prefix.Length, value.Length);
                var j = 0;
                while (j < length && prefix[j] == value[j])
                    j++;
                prefix = prefix[..j];
            }

            return prefix;
        }

        static async Task<bool> TryHandleHotkeyAsync(ConsoleKeyInfo keyInfo)
        {
            var combo = (keyInfo.Key, keyInfo.Modifiers);
            if (!hotkeys.TryGetValue(combo, out var entry))
                return false;

            HidePopup();
            await InvokeSafely(entry.Handler);
            return true;
        }

        static async Task<bool> HandlePopupKeyAsync(ConsoleKeyInfo keyInfo)
        {
            if (keyInfo.Modifiers != 0)
                return false;

            if (HasSelectablePopup())
                return await HandleSelectablePopupKeyAsync(keyInfo);

            switch (keyInfo.Key)
            {
                case ConsoleKey.Spacebar:
                case ConsoleKey.Enter:
                case ConsoleKey.Escape:
                    HidePopup();
                    return true;

                case ConsoleKey.UpArrow:
                    ScrollPopup(-1);
                    return true;

                case ConsoleKey.DownArrow:
                    ScrollPopup(1);
                    return true;

                case ConsoleKey.PageUp:
                    ScrollPopup(-5);
                    return true;

                case ConsoleKey.PageDown:
                    ScrollPopup(5);
                    return true;

                case ConsoleKey.Home:
                    SetPopupScroll(0);
                    return true;

                case ConsoleKey.End:
                    SetPopupScroll(int.MaxValue);
                    return true;

                default:
                    return false;
            }
        }

        static async Task<bool> HandleSelectablePopupKeyAsync(ConsoleKeyInfo keyInfo)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.Enter:
                    await ConfirmPopupSelectionAsync();
                    return true;

                case ConsoleKey.Spacebar:
                case ConsoleKey.Escape:
                    HidePopup();
                    return true;

                case ConsoleKey.UpArrow:
                    MovePopupSelection(-1);
                    return true;

                case ConsoleKey.DownArrow:
                    MovePopupSelection(1);
                    return true;

                case ConsoleKey.PageUp:
                    MovePopupSelection(-5);
                    return true;

                case ConsoleKey.PageDown:
                    MovePopupSelection(5);
                    return true;

                case ConsoleKey.Home:
                    SetPopupSelection(0);
                    return true;

                case ConsoleKey.End:
                    SetPopupSelection(int.MaxValue);
                    return true;

                default:
                    return false;
            }
        }

        static bool HasActivePopup()
        {
            lock (popupSync)
                return activePopup is not null;
        }

        static bool HasSelectablePopup()
        {
            lock (popupSync)
                return activePopup?.Selection?.LineIndexes.Count > 0;
        }

        internal static void ShowPopup(KeyboardHandlerContext context)
        {
            ShowPopup(context.ToPopup());
        }

        internal static void ShowPopup(KeyboardPopup popup)
        {
            if (popup.Lines.Count == 0)
                return;

            var overlaySink = OverlaySink ?? throw new InvalidOperationException("Keyboard popup 需要先绑定 LiveDisplay overlay sink。");
            KeyboardPopup shownPopup;
            int generation;
            lock (popupSync)
            {
                shownPopup = NormalizePopupForDisplay(popup, Math.Max(0, popup.ScrollOffset), refreshExpiresAt: true);
                activePopup = shownPopup;
                generation = unchecked(++popupGeneration);
            }

            overlaySink.ShowPopup(shownPopup);
            SchedulePopupAutoClose(generation, shownPopup.ExpiresAt);
        }

        static void HidePopup()
        {
            HidePopup(null);
        }

        static void HidePopup(int? generation)
        {
            IKeyboardOverlaySink? overlaySink;
            lock (popupSync)
            {
                if (generation is not null && generation.Value != popupGeneration)
                    return;

                if (activePopup is null)
                    return;

                activePopup = null;
                popupGeneration = unchecked(popupGeneration + 1);
                CancelPopupAutoCloseLocked();
                overlaySink = OverlaySink;
            }

            overlaySink?.HidePopup();
        }

        static async Task DelayPollAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        static void ScrollPopup(int delta)
        {
            int scrollOffset;
            lock (popupSync)
            {
                if (activePopup is null)
                    return;

                scrollOffset = activePopup.ScrollOffset + delta;
            }

            SetPopupScroll(scrollOffset);
        }

        static void SetPopupScroll(int scrollOffset)
        {
            KeyboardPopup popup;
            int generation;
            lock (popupSync)
            {
                if (activePopup is null)
                    return;

                popup = NormalizePopupForDisplay(activePopup, scrollOffset, refreshExpiresAt: true);
                activePopup = popup;
                generation = unchecked(++popupGeneration);
            }

            OverlaySink?.ShowPopup(popup);
            SchedulePopupAutoClose(generation, popup.ExpiresAt);
        }

        static void MovePopupSelection(int delta)
        {
            KeyboardPopupSelection? selection;
            lock (popupSync)
                selection = activePopup?.Selection;

            if (selection is null)
                return;

            SetPopupSelection(selection.BoundedSelectedIndex + delta);
        }

        static void SetPopupSelection(int selectedIndex)
        {
            KeyboardPopup popup;
            int generation;
            lock (popupSync)
            {
                if (activePopup?.Selection is null)
                    return;

                var normalizedSelection = activePopup.Selection.Normalize();
                var nextSelection = normalizedSelection with
                {
                    SelectedIndex = Math.Clamp(selectedIndex, 0, normalizedSelection.LineIndexes.Count - 1)
                };
                var visibleCount = Math.Min(activePopup.Lines.Count, EstimateVisiblePopupLines());
                var scrollOffset = ScrollOffsetForSelectedLine(
                    activePopup.ScrollOffset,
                    nextSelection.SelectedLineIndex,
                    visibleCount,
                    activePopup.Lines.Count);
                popup = activePopup with
                {
                    ScrollOffset = scrollOffset,
                    Selection = nextSelection,
                    ExpiresAt = null
                };
                activePopup = popup;
                generation = unchecked(++popupGeneration);
            }

            OverlaySink?.ShowPopup(popup);
            SchedulePopupAutoClose(generation, popup.ExpiresAt);
        }

        static async Task ConfirmPopupSelectionAsync()
        {
            KeyboardPopupSelection? selection;
            int selectedLineIndex;
            lock (popupSync)
            {
                selection = activePopup?.Selection?.Normalize();
                selectedLineIndex = selection?.SelectedLineIndex ?? -1;
            }

            HidePopup();
            if (selection is null || selectedLineIndex < 0)
                return;

            await InvokeSafely(() => selection.ConfirmAsync(selectedLineIndex));
        }

        static KeyboardPopup NormalizePopupForDisplay(KeyboardPopup popup, int scrollOffset, bool refreshExpiresAt)
        {
            var selection = popup.Selection?.LineIndexes.Count > 0 ? popup.Selection.Normalize() : null;
            var visibleCount = Math.Min(popup.Lines.Count, EstimateVisiblePopupLines());
            scrollOffset = selection is null
                ? ClampPopupScroll(popup.Lines.Count, visibleCount, scrollOffset)
                : ScrollOffsetForSelectedLine(scrollOffset, selection.SelectedLineIndex, visibleCount, popup.Lines.Count);
            return popup with
            {
                ScrollOffset = scrollOffset,
                Selection = selection,
                ExpiresAt = selection is null && refreshExpiresAt ? GetPopupExpiresAt() : null
            };
        }

        static int ClampPopupScroll(int lineCount, int visibleCount, int scrollOffset)
        {
            var maxOffset = Math.Max(0, lineCount - visibleCount);
            return Math.Clamp(scrollOffset, 0, maxOffset);
        }

        static int ScrollOffsetForSelectedLine(int scrollOffset, int selectedLineIndex, int visibleCount, int lineCount)
        {
            scrollOffset = ClampPopupScroll(lineCount, visibleCount, scrollOffset);
            if (selectedLineIndex < 0)
                return scrollOffset;

            if (selectedLineIndex < scrollOffset)
                return selectedLineIndex;

            if (selectedLineIndex >= scrollOffset + visibleCount)
                return ClampPopupScroll(lineCount, visibleCount, selectedLineIndex - visibleCount + 1);

            return scrollOffset;
        }

        static DateTimeOffset? GetPopupExpiresAt()
        {
            var delay = PopupAutoCloseDelay;
            return delay <= TimeSpan.Zero ? null : DateTimeOffset.Now.Add(delay);
        }

        static void SchedulePopupAutoClose(int generation, DateTimeOffset? expiresAt)
        {
            CancellationToken token;
            lock (popupSync)
            {
                CancelPopupAutoCloseLocked();
                if (expiresAt is null || activePopup is null || generation != popupGeneration)
                    return;

                popupAutoCloseCts = new();
                token = popupAutoCloseCts.Token;
            }

            _ = AutoClosePopupAsync(generation, expiresAt.Value, token);
        }

        static async Task AutoClosePopupAsync(int generation, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            try
            {
                var delay = expiresAt - DateTimeOffset.Now;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                if (!cancellationToken.IsCancellationRequested)
                    HidePopup(generation);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ArgumentOutOfRangeException)
            {
                HidePopup(generation);
            }
        }

        static void CancelPopupAutoCloseLocked()
        {
            popupAutoCloseCts?.Cancel();
            popupAutoCloseCts?.Dispose();
            popupAutoCloseCts = null;
        }

        static int EstimateVisiblePopupLines()
        {
            try
            {
                return Math.Max(1, Console.WindowHeight - 2);
            }
            catch (IOException)
            {
                return 1;
            }
            catch (InvalidOperationException)
            {
                return 1;
            }
        }

        static bool TryKeyAvailable()
        {
            try
            {
                return Console.KeyAvailable;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        static async Task InvokeSafely(Func<Task> handler)
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                LiveDisplayConsole.Notify("Keyboard", $"热键处理失败: {ex.Message}", LiveDisplaySeverity.Error);
                LiveDisplayConsole.LogException("Keyboard", ex);
            }
        }

        sealed class InputSuspension : IDisposable
        {
            int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    Interlocked.Decrement(ref inputSuspensionCount);
            }
        }
    }
}
