using System.Collections.Concurrent;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using UmamusumeResponseAnalyzer.Plugin;

namespace UmamusumeResponseAnalyzer.TerminalGui
{
    public static class HotkeyManager
    {
        public sealed class HotkeyEntry(
            string description,
            Func<Task> handler,
            object? owner = null)
        {
            public string Description { get; } = description;
            public Func<Task> Handler { get; } = handler;
            public object? Owner { get; } = owner;
        }

        static readonly ConcurrentDictionary<(ConsoleKey Key, ConsoleModifiers Modifiers), HotkeyEntry> hotkeys = [];
        static readonly SemaphoreSlim dispatchGate = new(1, 1);
        static readonly object popupSync = new();
        static readonly object notificationShortcutSync = new();
        static readonly Dictionary<long, NotificationShortcutRegistration> notificationShortcutRegistrations = [];

        static HotkeyPopup? activePopup;
        static IReadOnlyList<TransientShortcutEntry> popupShortcuts = [];
        static CancellationTokenSource? popupAutoCloseCts;
        static int popupGeneration;
        static long notificationShortcutRegistrationId;
        static readonly AsyncLocal<object?> registrationOwner = new();

        public static TimeSpan PopupAutoCloseDelay { get; set; } = TimeSpan.FromSeconds(3);
        internal static IUiInputSink? OverlaySink { get; set; }
        internal static bool HasPriorityPopup => HasActivePopup();

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
            Func<HotkeyContext, Task> handler)
        {
            RegisterCore(
                key,
                modifiers,
                description,
                async () =>
                {
                    var context = new HotkeyContext();
                    await handler(context);
                    ShowPopup(context.ToPopup());
                });
        }

        public static void Register(ConsoleKey key, string description, Func<Task> handler)
        {
            Register(key, 0, description, handler);
        }

        public static void Register(ConsoleKey key, string description, Func<HotkeyContext, Task> handler)
        {
            Register(key, 0, description, handler);
        }

        internal static HotkeyEntry RegisterTracked(
            ConsoleKey key,
            ConsoleModifiers modifiers,
            string description,
            Func<Task> handler)
        {
            return RegisterCore(key, modifiers, description, handler);
        }

        static HotkeyEntry RegisterCore(
            ConsoleKey key,
            ConsoleModifiers modifiers,
            string description,
            Func<Task> handler)
        {
            if (modifiers.HasFlag(ConsoleModifiers.Control) &&
                key is ConsoleKey.S or ConsoleKey.Q or ConsoleKey.Z)
            {
                throw new InvalidOperationException($"Ctrl+{key} 由终端保留，不能注册为热键。");
            }

            var entry = new HotkeyEntry(
                description,
                handler,
                registrationOwner.Value);
            hotkeys[(key, modifiers)] = entry;
            return entry;
        }

        public static bool Unregister(ConsoleKey key, ConsoleModifiers modifiers = 0)
        {
            return hotkeys.TryRemove((key, modifiers), out _);
        }

        internal static bool Unregister(
            ConsoleKey key,
            ConsoleModifiers modifiers,
            HotkeyEntry entry)
        {
            return hotkeys.TryRemove(new KeyValuePair<(ConsoleKey Key, ConsoleModifiers Modifiers), HotkeyEntry>(
                (key, modifiers),
                entry));
        }

        public static void UnregisterAll()
        {
            hotkeys.Clear();
            ClearTransientShortcuts();
            HidePopup();
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
            return count + RemoveTransientShortcuts(entry => ReferenceEquals(entry.Owner, owner));
        }

        sealed class RegistrationScope(object? previous) : IDisposable
        {
            public void Dispose()
            {
                registrationOwner.Value = previous;
            }
        }

        public static IReadOnlyDictionary<(ConsoleKey Key, ConsoleModifiers Modifiers), HotkeyEntry> Hotkeys => hotkeys;
        internal static int TransientShortcutCountForTests
        {
            get
            {
                int popupCount;
                lock (popupSync)
                    popupCount = popupShortcuts.Count;
                lock (notificationShortcutSync)
                    return popupCount + notificationShortcutRegistrations.Values.Sum(x => x.Shortcuts.Count);
            }
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

        internal static async Task HandleMouseWheelAsync(
            int steps,
            bool hasModifiers,
            bool isHorizontal = false)
        {
            await dispatchGate.WaitAsync();
            try
            {
                await HandleMouseWheelCoreAsync(steps, hasModifiers, isHorizontal);
            }
            finally
            {
                dispatchGate.Release();
            }
        }

        static async Task HandleMouseWheelCoreAsync(
            int steps,
            bool hasModifiers,
            bool isHorizontal)
        {
            if (steps == 0 ||
                isHorizontal ||
                hasModifiers ||
                HasActivePopup() ||
                OverlaySink is not { } overlaySink)
            {
                return;
            }

            var command = steps > 0 ? Command.Up : Command.Down;
            for (var remaining = Math.Abs(steps); remaining > 0; remaining--)
                await overlaySink.TryHandleWorkspaceCommandAsync(command);
        }

        internal static async Task<bool> HandleKeyAsync(Key key)
        {
            await dispatchGate.WaitAsync();
            try
            {
                return await HandleKeyCoreAsync(key);
            }
            finally
            {
                dispatchGate.Release();
            }
        }

        static async Task<bool> HandleKeyCoreAsync(Key key)
        {
            if (await TryHandlePopupShortcutAsync(key))
                return true;

            var hasActivePopup = HasActivePopup();
            if (hasActivePopup && await HandlePopupKeyAsync(key))
                return true;

            if (await TryHandleNotificationShortcutAsync(key))
                return true;

            if (!hasActivePopup &&
                OverlaySink is { } overlaySink &&
                TryGetWorkspaceCommand(key, out var workspaceCommand) &&
                await overlaySink.TryHandleWorkspaceCommandAsync(workspaceCommand))
                return true;

            return await TryHandleHotkeyAsync(key);
        }

        static bool TryGetWorkspaceCommand(Key key, out Command command)
        {
            command = key.KeyCode switch
            {
                KeyCode.CursorUp => Command.Up,
                KeyCode.CursorDown => Command.Down,
                KeyCode.PageUp => Command.PageUp,
                KeyCode.PageDown => Command.PageDown,
                KeyCode.Home => Command.Start,
                KeyCode.End => Command.End,
                _ => Command.NotBound
            };
            return command != Command.NotBound;
        }

        static async Task<bool> TryHandleHotkeyAsync(Key key)
        {
            if (hotkeys.IsEmpty)
                return false;

            var keyInfo = ConsoleKeyMapping.GetConsoleKeyInfoFromKeyCode(key.KeyCode);
            var combo = (keyInfo.Key, keyInfo.Modifiers);
            if (!hotkeys.TryGetValue(combo, out var entry))
                return false;

            HidePopup();
            await InvokeSafely(entry);
            return true;
        }

        static async Task<bool> TryHandlePopupShortcutAsync(Key key)
        {
            HotkeyEntry? entry;
            lock (popupSync)
            {
                if (activePopup is null || popupShortcuts.Count == 0)
                    return false;

                var keyInfo = ConsoleKeyMapping.GetConsoleKeyInfoFromKeyCode(key.KeyCode);
                entry = popupShortcuts.LastOrDefault(x =>
                    x.Key == keyInfo.Key && x.Modifiers == keyInfo.Modifiers)?.Entry;
            }

            if (entry is null)
                return false;

            await InvokeSafely(entry);
            return true;
        }

        static async Task<bool> TryHandleNotificationShortcutAsync(Key key)
        {
            HotkeyEntry? entry = null;
            var latestRegistrationId = 0L;
            lock (notificationShortcutSync)
            {
                RemoveExpiredNotificationShortcutsLocked(DateTimeOffset.Now);
                if (notificationShortcutRegistrations.Count == 0)
                    return false;

                var keyInfo = ConsoleKeyMapping.GetConsoleKeyInfoFromKeyCode(key.KeyCode);
                foreach (var registration in notificationShortcutRegistrations.Values)
                {
                    if (registration.Id <= latestRegistrationId)
                        continue;

                    var candidate = registration.Shortcuts.LastOrDefault(x =>
                        x.Key == keyInfo.Key && x.Modifiers == keyInfo.Modifiers);
                    if (candidate is null)
                        continue;

                    latestRegistrationId = registration.Id;
                    entry = candidate.Entry;
                }
            }

            if (entry is null)
                return false;

            await InvokeSafely(entry);
            return true;
        }

        static async Task<bool> HandlePopupKeyAsync(Key key)
        {
            if (key.IsCtrl || key.IsAlt || key.IsShift)
                return false;

            if (HasSelectablePopup())
                return await HandleSelectablePopupKeyAsync(key);

            switch (key.KeyCode)
            {
                case KeyCode.Space:
                case KeyCode.Enter:
                case KeyCode.Esc:
                    HidePopup();
                    return true;

                case KeyCode.CursorUp:
                    ScrollPopup(-1);
                    return true;

                case KeyCode.CursorDown:
                    ScrollPopup(1);
                    return true;

                case KeyCode.PageUp:
                    ScrollPopup(-5);
                    return true;

                case KeyCode.PageDown:
                    ScrollPopup(5);
                    return true;

                case KeyCode.Home:
                    SetPopupScroll(0);
                    return true;

                case KeyCode.End:
                    SetPopupScroll(int.MaxValue);
                    return true;

                default:
                    return false;
            }
        }

        static async Task<bool> HandleSelectablePopupKeyAsync(Key key)
        {
            switch (key.KeyCode)
            {
                case KeyCode.Enter:
                    await ConfirmPopupSelectionAsync();
                    return true;

                case KeyCode.Space:
                case KeyCode.Esc:
                    HidePopup();
                    return true;

                case KeyCode.CursorUp:
                    MovePopupSelection(-1);
                    return true;

                case KeyCode.CursorDown:
                    MovePopupSelection(1);
                    return true;

                case KeyCode.PageUp:
                    MovePopupSelection(-5);
                    return true;

                case KeyCode.PageDown:
                    MovePopupSelection(5);
                    return true;

                case KeyCode.Home:
                    SetPopupSelection(0);
                    return true;

                case KeyCode.End:
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

        internal static void ShowPopup(HotkeyContext context)
        {
            ShowPopup(context.ToPopup());
        }

        internal static void ShowPopup(HotkeyPopup popup)
        {
            if (popup.Lines.Count == 0)
                return;

            var overlaySink = OverlaySink ?? throw new InvalidOperationException("Hotkey popup 需要先绑定 UI input sink。");
            HotkeyPopup shownPopup;
            int generation;
            var shortcuts = CreateTransientShortcutEntries(popup.Shortcuts ?? []);
            lock (popupSync)
            {
                shownPopup = NormalizePopupForDisplay(
                    popup with { Shortcuts = null },
                    Math.Max(0, popup.ScrollOffset),
                    refreshExpiresAt: true);
                activePopup = shownPopup;
                popupShortcuts = shortcuts;
                generation = unchecked(++popupGeneration);
            }

            overlaySink.ShowPopup(shownPopup, generation);
            SchedulePopupAutoClose(generation, shownPopup.ExpiresAt);
        }

        static void HidePopup()
        {
            HidePopup(null);
        }

        static void HidePopup(int? generation)
        {
            IUiInputSink? overlaySink;
            int hiddenGeneration;
            lock (popupSync)
            {
                if (generation is not null && generation.Value != popupGeneration)
                    return;

                if (activePopup is null)
                    return;

                activePopup = null;
                popupShortcuts = [];
                popupGeneration = unchecked(popupGeneration + 1);
                hiddenGeneration = popupGeneration;
                CancelPopupAutoCloseLocked();
                overlaySink = OverlaySink;
            }

            overlaySink?.HidePopup(hiddenGeneration);
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
            HotkeyPopup popup;
            int generation;
            lock (popupSync)
            {
                if (activePopup is null)
                    return;

                popup = NormalizePopupForDisplay(activePopup, scrollOffset, refreshExpiresAt: true);
                activePopup = popup;
                generation = unchecked(++popupGeneration);
            }

            OverlaySink?.ShowPopup(popup, generation);
            SchedulePopupAutoClose(generation, popup.ExpiresAt);
        }

        static void MovePopupSelection(int delta)
        {
            HotkeyPopupSelection? selection;
            lock (popupSync)
                selection = activePopup?.Selection;

            if (selection is null)
                return;

            SetPopupSelection(selection.BoundedSelectedIndex + delta);
        }

        static void SetPopupSelection(int selectedIndex)
        {
            HotkeyPopup popup;
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

            OverlaySink?.ShowPopup(popup, generation);
            SchedulePopupAutoClose(generation, popup.ExpiresAt);
        }

        static async Task ConfirmPopupSelectionAsync()
        {
            HotkeyPopupSelection? selection;
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

        static HotkeyPopup NormalizePopupForDisplay(HotkeyPopup popup, int scrollOffset, bool refreshExpiresAt)
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

        internal static long RegisterNotificationShortcuts(
            Workspace? workspace,
            DateTimeOffset expiresAt,
            IReadOnlyList<UiShortcut> shortcuts)
        {
            ArgumentNullException.ThrowIfNull(shortcuts);
            if (shortcuts.Count == 0)
                return 0;

            var entries = CreateTransientShortcutEntries(shortcuts);
            lock (notificationShortcutSync)
            {
                RemoveExpiredNotificationShortcutsLocked(DateTimeOffset.Now);
                var id = unchecked(++notificationShortcutRegistrationId);
                notificationShortcutRegistrations[id] = new(id, workspace, expiresAt, entries);
                return id;
            }
        }

        internal static void UnregisterNotificationShortcuts(long registrationId)
        {
            if (registrationId == 0)
                return;

            lock (notificationShortcutSync)
                notificationShortcutRegistrations.Remove(registrationId);
        }

        internal static void RemoveNotificationShortcuts(Workspace workspace)
        {
            lock (notificationShortcutSync)
            {
                foreach (var id in notificationShortcutRegistrations
                    .Where(x => ReferenceEquals(x.Value.Workspace, workspace))
                    .Select(x => x.Key)
                    .ToArray())
                {
                    notificationShortcutRegistrations.Remove(id);
                }
            }
        }

        static TransientShortcutEntry[] CreateTransientShortcutEntries(IReadOnlyList<UiShortcut> shortcuts)
        {
            var owner = registrationOwner.Value;
            var entries = new TransientShortcutEntry[shortcuts.Count];
            for (var i = 0; i < shortcuts.Count; i++)
            {
                var shortcut = shortcuts[i];
                ArgumentNullException.ThrowIfNull(shortcut);
                ArgumentNullException.ThrowIfNull(shortcut.Handler);
                entries[i] = new(
                    shortcut.Key,
                    shortcut.Modifiers,
                    new HotkeyEntry(string.Empty, shortcut.Handler, owner));
            }
            return entries;
        }

        static int RemoveTransientShortcuts(Func<HotkeyEntry, bool> predicate)
        {
            var count = 0;
            lock (popupSync)
            {
                var remaining = popupShortcuts.Where(x => !predicate(x.Entry)).ToArray();
                count += popupShortcuts.Count - remaining.Length;
                popupShortcuts = remaining;
            }

            lock (notificationShortcutSync)
            {
                foreach (var (id, registration) in notificationShortcutRegistrations.ToArray())
                {
                    var remaining = registration.Shortcuts.Where(x => !predicate(x.Entry)).ToArray();
                    count += registration.Shortcuts.Count - remaining.Length;
                    if (remaining.Length == 0)
                        notificationShortcutRegistrations.Remove(id);
                    else if (remaining.Length != registration.Shortcuts.Count)
                        notificationShortcutRegistrations[id] = registration with { Shortcuts = remaining };
                }
            }

            return count;
        }

        static void ClearTransientShortcuts()
        {
            lock (popupSync)
                popupShortcuts = [];
            lock (notificationShortcutSync)
                notificationShortcutRegistrations.Clear();
        }

        static void RemoveExpiredNotificationShortcutsLocked(DateTimeOffset now)
        {
            foreach (var id in notificationShortcutRegistrations
                .Where(x => x.Value.ExpiresAt <= now)
                .Select(x => x.Key)
                .ToArray())
            {
                notificationShortcutRegistrations.Remove(id);
            }
        }

        static int EstimateVisiblePopupLines()
        {
            return Math.Max(1, OverlaySink?.PopupVisibleLineCount ?? 1);
        }

        static async Task InvokeSafely(Func<Task> handler)
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                TerminalUi.Notify("Keyboard", $"热键处理失败: {ex.Message}", UiSeverity.Error);
                TerminalUi.LogException("Keyboard", ex);
            }
        }

        static async Task InvokeSafely(HotkeyEntry entry)
        {
            if (entry.Owner is IPlugin plugin)
            {
                await InvokeSafely(async () =>
                {
                    using var callback = await PluginManager.EnterPluginCallbackAsync();
                    if (!PluginManager.IsLoadedPluginInstance(plugin))
                        return;

                    using var callbackScope = PluginManager.EnterPluginCallbackScope();
                    using var registrationScope = RegisterScope(plugin);
                    await entry.Handler();
                });
                return;
            }

            using var scope = entry.Owner is null ? null : RegisterScope(entry.Owner);
            await InvokeSafely(entry.Handler);
        }

        sealed record TransientShortcutEntry(
            ConsoleKey Key,
            ConsoleModifiers Modifiers,
            HotkeyEntry Entry);

        sealed record NotificationShortcutRegistration(
            long Id,
            Workspace? Workspace,
            DateTimeOffset ExpiresAt,
            IReadOnlyList<TransientShortcutEntry> Shortcuts);
    }
}
