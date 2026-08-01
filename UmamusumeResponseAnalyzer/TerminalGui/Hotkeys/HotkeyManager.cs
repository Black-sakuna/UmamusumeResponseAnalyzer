using System.Collections.Frozen;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using UmamusumeResponseAnalyzer.Plugin;

namespace UmamusumeResponseAnalyzer.TerminalGui;

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

    static readonly object stateGate = new();
    static readonly Dictionary<(ConsoleKey Key, ConsoleModifiers Modifiers), HotkeyEntry> hotkeys = [];
    static readonly Dictionary<long, NotificationShortcutRegistration> notificationShortcutRegistrations = [];
    static readonly SemaphoreSlim dispatchGate = new(1, 1);
    static readonly AsyncLocal<object?> registrationOwner = new();

    static PopupState popupState = new(null, [], null, 0);
    static IUiInputSink? overlaySink;
    static long overlaySinkGeneration;
    static int activeSinkCallouts;
    static bool overlaySinkDetaching;
    static TimeSpan popupAutoCloseDelay = TimeSpan.FromSeconds(3);
    static long notificationShortcutRegistrationId;

    public static TimeSpan PopupAutoCloseDelay
    {
        get
        {
            lock (stateGate)
                return popupAutoCloseDelay;
        }
        set
        {
            lock (stateGate)
                popupAutoCloseDelay = value;
        }
    }

    internal static IUiInputSink? OverlaySink
    {
        get
        {
            lock (stateGate)
                return overlaySink;
        }
        set
        {
            PopupTransitionEffect detachEffect = default;
            lock (stateGate)
            {
                while (overlaySinkDetaching)
                    Monitor.Wait(stateGate);

                if (value is not null)
                {
                    if (ReferenceEquals(overlaySink, value))
                        return;
                    if (overlaySink is not null)
                    {
                        throw new InvalidOperationException(
                            "UI input sink 已绑定；必须先完整 detach，不能原地替换 Host。");
                    }

                    overlaySink = value;
                    overlaySinkGeneration = unchecked(overlaySinkGeneration + 1);
                    return;
                }

                if (overlaySink is null)
                    return;

                overlaySinkDetaching = true;
                detachEffect = TransitionPopupLocked(PopupTransitionKind.Detach);
                overlaySink = null;
                overlaySinkGeneration = unchecked(overlaySinkGeneration + 1);
                while (activeSinkCallouts > 0)
                    Monitor.Wait(stateGate);
            }

            try
            {
                ApplyPopupTransition(detachEffect);
            }
            finally
            {
                lock (stateGate)
                {
                    overlaySinkDetaching = false;
                    Monitor.PulseAll(stateGate);
                }
            }
        }
    }

    internal static bool HasPriorityPopup
    {
        get
        {
            lock (stateGate)
                return popupState.Popup is not null;
        }
    }

    public static void Register(
        ConsoleKey key,
        ConsoleModifiers modifiers,
        string description,
        Func<Task> handler)
        => RegisterCore(key, modifiers, description, handler);

    public static void Register(
        ConsoleKey key,
        ConsoleModifiers modifiers,
        string description,
        Func<HotkeyContext, Task> handler)
        => RegisterCore(key, modifiers, description, CreateContextHandler(handler));

    public static void Register(ConsoleKey key, string description, Func<Task> handler)
        => RegisterCore(key, 0, description, handler);

    public static void Register(
        ConsoleKey key,
        string description,
        Func<HotkeyContext, Task> handler)
        => RegisterCore(key, 0, description, CreateContextHandler(handler));

    internal static HotkeyEntry CaptureTracked(
        string description,
        Func<Task> handler)
        => new(description, handler, registrationOwner.Value);

    internal static HotkeyEntry? RegisterTracked(
        ConsoleKey key,
        ConsoleModifiers modifiers,
        HotkeyEntry entry)
    {
        lock (stateGate)
        {
            var combo = (key, modifiers);
            hotkeys.Remove(combo, out var replaced);
            hotkeys.Add(combo, entry);
            return replaced;
        }
    }

    internal static void RestoreTracked(
        ConsoleKey key,
        ConsoleModifiers modifiers,
        HotkeyEntry entry,
        HotkeyEntry? replaced)
    {
        lock (stateGate)
        {
            var combo = (key, modifiers);
            if (!hotkeys.TryGetValue(combo, out var registered))
            {
                if (replaced is not null &&
                    !ReferenceEquals(replaced.Owner, entry.Owner))
                {
                    hotkeys.Add(combo, replaced);
                }
                return;
            }
            if (!ReferenceEquals(registered, entry))
                return;

            if (replaced is null)
                hotkeys.Remove(combo);
            else
                hotkeys[combo] = replaced;
        }
    }

    static Func<Task> CreateContextHandler(Func<HotkeyContext, Task> handler)
    {
        return async () =>
        {
            var context = new HotkeyContext();
            await handler(context);
            ShowPopup(context.ToPopup());
        };
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

        var entry = new HotkeyEntry(description, handler, registrationOwner.Value);
        lock (stateGate)
            hotkeys[(key, modifiers)] = entry;
        return entry;
    }

    public static bool Unregister(ConsoleKey key, ConsoleModifiers modifiers = 0)
    {
        lock (stateGate)
            return hotkeys.Remove((key, modifiers));
    }

    internal static bool Unregister(
        ConsoleKey key,
        ConsoleModifiers modifiers,
        HotkeyEntry entry)
    {
        lock (stateGate)
        {
            var combo = (key, modifiers);
            return hotkeys.TryGetValue(combo, out var registered) &&
                   ReferenceEquals(registered, entry) &&
                   hotkeys.Remove(combo);
        }
    }

    public static void UnregisterAll()
    {
        PopupTransitionEffect effect;
        lock (stateGate)
        {
            hotkeys.Clear();
            notificationShortcutRegistrations.Clear();
            effect = TransitionPopupLocked(PopupTransitionKind.Hide);
        }
        ApplyPopupTransition(effect);
    }

    public static IDisposable RegisterScope(object owner)
    {
        var previous = registrationOwner.Value;
        registrationOwner.Value = owner;
        return new RegistrationScope(previous);
    }

    public static int UnregisterByOwner(object owner)
    {
        UiHost? host;
        lock (stateGate)
            host = overlaySink as UiHost;

        var count = host?.RemoveWorkspaceHotkeysByOwner(owner) ?? 0;
        lock (stateGate)
        {
            foreach (var combo in hotkeys
                .Where(x => ReferenceEquals(x.Value.Owner, owner))
                .Select(x => x.Key)
                .ToArray())
            {
                hotkeys.Remove(combo);
                count++;
            }

            foreach (var (id, registration) in notificationShortcutRegistrations.ToArray())
            {
                var remaining = registration.Shortcuts
                    .Where(x => !ReferenceEquals(x.Entry.Owner, owner))
                    .ToArray();
                count += registration.Shortcuts.Count - remaining.Length;
                if (remaining.Length == 0)
                    notificationShortcutRegistrations.Remove(id);
                else if (remaining.Length != registration.Shortcuts.Count)
                    notificationShortcutRegistrations[id] = registration with { Shortcuts = remaining };
            }

            var popupShortcuts = popupState.Shortcuts
                .Where(x => !ReferenceEquals(x.Entry.Owner, owner))
                .ToArray();
            count += popupState.Shortcuts.Count - popupShortcuts.Length;
            if (popupShortcuts.Length != popupState.Shortcuts.Count)
            {
                TransitionPopupLocked(
                    PopupTransitionKind.ReplaceShortcuts,
                    shortcuts: popupShortcuts);
            }
        }

        return count;
    }

    public static IReadOnlyDictionary<(ConsoleKey Key, ConsoleModifiers Modifiers), HotkeyEntry> Hotkeys
    {
        get
        {
            lock (stateGate)
                return hotkeys.ToFrozenDictionary();
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

        parts.Add(key switch
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
        });
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
            if (steps == 0 || isHorizontal || hasModifiers)
                return;

            IUiInputSink? sink;
            long sinkGeneration;
            lock (stateGate)
            {
                if (popupState.Popup is not null)
                    return;
                sink = overlaySink;
                sinkGeneration = overlaySinkGeneration;
            }
            if (sink is null)
                return;

            var command = steps > 0 ? Command.Up : Command.Down;
            for (var remaining = Math.Abs(steps); remaining > 0; remaining--)
                await TryHandleWorkspaceCommandAsync(sink, sinkGeneration, command);
        }
        finally
        {
            dispatchGate.Release();
        }
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

        IUiInputSink? sink;
        long sinkGeneration;
        lock (stateGate)
        {
            sink = popupState.Popup is null ? overlaySink : null;
            sinkGeneration = overlaySinkGeneration;
        }
        if (!hasActivePopup &&
            sink is not null &&
            TryGetWorkspaceCommand(key, out var workspaceCommand) &&
            await TryHandleWorkspaceCommandAsync(sink, sinkGeneration, workspaceCommand))
        {
            return true;
        }

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
        var keyInfo = ConsoleKeyMapping.GetConsoleKeyInfoFromKeyCode(key.KeyCode);
        HotkeyEntry? entry;
        lock (stateGate)
            hotkeys.TryGetValue((keyInfo.Key, keyInfo.Modifiers), out entry);
        if (entry is null)
            return false;

        HidePopup();
        await InvokeSafely(entry);
        return true;
    }

    static async Task<bool> TryHandlePopupShortcutAsync(Key key)
    {
        var keyInfo = ConsoleKeyMapping.GetConsoleKeyInfoFromKeyCode(key.KeyCode);
        HotkeyEntry? entry;
        lock (stateGate)
        {
            entry = popupState.Popup is null
                ? null
                : popupState.Shortcuts.LastOrDefault(x =>
                    x.Key == keyInfo.Key && x.Modifiers == keyInfo.Modifiers)?.Entry;
        }
        if (entry is null)
            return false;

        await InvokeSafely(entry);
        return true;
    }

    static async Task<bool> TryHandleNotificationShortcutAsync(Key key)
    {
        var keyInfo = ConsoleKeyMapping.GetConsoleKeyInfoFromKeyCode(key.KeyCode);
        HotkeyEntry? entry = null;
        var latestRegistrationId = 0L;
        lock (stateGate)
        {
            RemoveExpiredNotificationShortcutsLocked(DateTimeOffset.Now);
            foreach (var (id, registration) in notificationShortcutRegistrations)
            {
                if (id <= latestRegistrationId)
                    continue;

                var candidate = registration.Shortcuts.LastOrDefault(x =>
                    x.Key == keyInfo.Key && x.Modifiers == keyInfo.Modifiers);
                if (candidate is null)
                    continue;

                latestRegistrationId = id;
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
        lock (stateGate)
            return popupState.Popup is not null;
    }

    static bool HasSelectablePopup()
    {
        lock (stateGate)
            return popupState.Popup?.Selection?.LineIndexes.Count > 0;
    }

    internal static void ShowPopup(HotkeyContext context)
        => ShowPopup(context.ToPopup());

    internal static void ShowPopup(HotkeyPopup popup)
    {
        if (popup.Lines.Count == 0)
            return;

        var snapshot = SnapshotPopup();
        var sink = snapshot.Sink
            ?? throw new InvalidOperationException("Hotkey popup 需要先绑定 UI input sink。");
        var shownPopup = NormalizePopupForDisplay(
            popup with { Shortcuts = null },
            Math.Max(0, popup.ScrollOffset),
            refreshExpiresAt: true,
            EstimateVisiblePopupLines(sink, snapshot.SinkGeneration),
            snapshot.AutoCloseDelay);
        var shortcuts = CreateTransientShortcutEntries(popup.Shortcuts ?? []);

        PopupTransitionEffect effect;
        lock (stateGate)
        {
            if (overlaySink is null ||
                overlaySinkGeneration != snapshot.SinkGeneration ||
                !ReferenceEquals(overlaySink, sink))
            {
                throw new InvalidOperationException("Hotkey popup 的 UI input sink 已 detach。");
            }
            effect = TransitionPopupLocked(
                PopupTransitionKind.Display,
                shownPopup,
                shortcuts);
        }
        ApplyPopupTransition(effect);
    }

    static void HidePopup()
        => HidePopup(null);

    static void HidePopup(int? generation)
    {
        PopupTransitionEffect effect;
        lock (stateGate)
        {
            effect = TransitionPopupLocked(
                PopupTransitionKind.Hide,
                expectedGeneration: generation);
        }
        ApplyPopupTransition(effect);
    }

    static void ScrollPopup(int delta)
    {
        var snapshot = SnapshotPopup();
        if (snapshot.Popup is null)
            return;
        SetPopupScroll(snapshot, snapshot.Popup.ScrollOffset + delta);
    }

    static void SetPopupScroll(int scrollOffset)
        => SetPopupScroll(SnapshotPopup(), scrollOffset);

    static void SetPopupScroll(PopupSnapshot snapshot, int scrollOffset)
    {
        if (snapshot.Popup is null)
            return;

        var popup = NormalizePopupForDisplay(
            snapshot.Popup,
            scrollOffset,
            refreshExpiresAt: true,
            EstimateVisiblePopupLines(snapshot.Sink, snapshot.SinkGeneration),
            snapshot.AutoCloseDelay);
        PopupTransitionEffect effect;
        lock (stateGate)
        {
            effect = TransitionPopupLocked(
                PopupTransitionKind.Display,
                popup,
                expectedGeneration: snapshot.Generation);
        }
        ApplyPopupTransition(effect);
    }

    static void MovePopupSelection(int delta)
    {
        var snapshot = SnapshotPopup();
        var selection = snapshot.Popup?.Selection;
        if (selection is null)
            return;
        SetPopupSelection(snapshot, selection.BoundedSelectedIndex + delta);
    }

    static void SetPopupSelection(int selectedIndex)
        => SetPopupSelection(SnapshotPopup(), selectedIndex);

    static void SetPopupSelection(PopupSnapshot snapshot, int selectedIndex)
    {
        if (snapshot.Popup?.Selection is null)
            return;

        var normalizedSelection = snapshot.Popup.Selection.Normalize();
        var nextSelection = normalizedSelection with
        {
            SelectedIndex = Math.Clamp(selectedIndex, 0, normalizedSelection.LineIndexes.Count - 1)
        };
        var visibleCount = Math.Min(
            snapshot.Popup.Lines.Count,
            EstimateVisiblePopupLines(snapshot.Sink, snapshot.SinkGeneration));
        var popup = snapshot.Popup with
        {
            ScrollOffset = ScrollOffsetForSelectedLine(
                snapshot.Popup.ScrollOffset,
                nextSelection.SelectedLineIndex,
                visibleCount,
                snapshot.Popup.Lines.Count),
            Selection = nextSelection,
            ExpiresAt = null
        };

        PopupTransitionEffect effect;
        lock (stateGate)
        {
            effect = TransitionPopupLocked(
                PopupTransitionKind.Display,
                popup,
                expectedGeneration: snapshot.Generation);
        }
        ApplyPopupTransition(effect);
    }

    static async Task ConfirmPopupSelectionAsync()
    {
        var snapshot = SnapshotPopup();
        var selection = snapshot.Popup?.Selection?.Normalize();
        var selectedLineIndex = selection?.SelectedLineIndex ?? -1;

        PopupTransitionEffect effect;
        lock (stateGate)
        {
            effect = TransitionPopupLocked(
                PopupTransitionKind.Hide,
                expectedGeneration: snapshot.Generation);
        }
        ApplyPopupTransition(effect);
        if (!effect.Changed || selection is null || selectedLineIndex < 0)
            return;

        await InvokeSafely(() => selection.ConfirmAsync(selectedLineIndex));
    }

    static HotkeyPopup NormalizePopupForDisplay(
        HotkeyPopup popup,
        int scrollOffset,
        bool refreshExpiresAt,
        int visibleCount,
        TimeSpan autoCloseDelay)
    {
        var selection = popup.Selection?.LineIndexes.Count > 0
            ? popup.Selection.Normalize()
            : null;
        visibleCount = Math.Min(popup.Lines.Count, visibleCount);
        scrollOffset = selection is null
            ? ClampPopupScroll(popup.Lines.Count, visibleCount, scrollOffset)
            : ScrollOffsetForSelectedLine(
                scrollOffset,
                selection.SelectedLineIndex,
                visibleCount,
                popup.Lines.Count);
        return popup with
        {
            ScrollOffset = scrollOffset,
            Selection = selection,
            ExpiresAt = selection is null && refreshExpiresAt
                ? GetPopupExpiresAt(autoCloseDelay)
                : null
        };
    }

    static int ClampPopupScroll(int lineCount, int visibleCount, int scrollOffset)
        => Math.Clamp(scrollOffset, 0, Math.Max(0, lineCount - visibleCount));

    static int ScrollOffsetForSelectedLine(
        int scrollOffset,
        int selectedLineIndex,
        int visibleCount,
        int lineCount)
    {
        scrollOffset = ClampPopupScroll(lineCount, visibleCount, scrollOffset);
        if (selectedLineIndex < 0)
            return scrollOffset;
        if (selectedLineIndex < scrollOffset)
            return selectedLineIndex;
        return selectedLineIndex >= scrollOffset + visibleCount
            ? ClampPopupScroll(lineCount, visibleCount, selectedLineIndex - visibleCount + 1)
            : scrollOffset;
    }

    static DateTimeOffset? GetPopupExpiresAt(TimeSpan delay)
        => delay <= TimeSpan.Zero ? null : DateTimeOffset.Now.Add(delay);

    static int EstimateVisiblePopupLines(IUiInputSink? sink, long sinkGeneration)
    {
        if (sink is null || !TryBeginSinkCallout(sink, sinkGeneration))
            return 1;
        try
        {
            return Math.Max(1, sink.PopupVisibleLineCount);
        }
        finally
        {
            EndSinkCallout();
        }
    }

    static PopupSnapshot SnapshotPopup()
    {
        lock (stateGate)
        {
            return new(
                popupState.Popup,
                popupState.Generation,
                overlaySink,
                overlaySinkGeneration,
                popupAutoCloseDelay);
        }
    }

    static PopupTransitionEffect TransitionPopupLocked(
        PopupTransitionKind kind,
        HotkeyPopup? popup = null,
        IReadOnlyList<TransientShortcutEntry>? shortcuts = null,
        int? expectedGeneration = null)
    {
        if (expectedGeneration is not null && expectedGeneration.Value != popupState.Generation)
            return default;

        if (kind == PopupTransitionKind.ReplaceShortcuts)
        {
            popupState = popupState with { Shortcuts = shortcuts ?? [] };
            return new(
                true,
                PopupRenderAction.None,
                null,
                null,
                popupState.Generation,
                overlaySinkGeneration,
                null,
                null);
        }

        if (kind is PopupTransitionKind.Hide or PopupTransitionKind.Detach)
        {
            if (kind == PopupTransitionKind.Hide &&
                popupState.Popup is null &&
                popupState.Shortcuts.Count == 0 &&
                popupState.AutoClose is null)
            {
                return default;
            }

            var wasVisible = popupState.Popup is not null;
            var previousAutoClose = popupState.AutoClose;
            var generation = unchecked(popupState.Generation + 1);
            popupState = new(null, [], null, generation);
            return new(
                true,
                kind == PopupTransitionKind.Detach
                    ? PopupRenderAction.Detach
                    : wasVisible ? PopupRenderAction.Hide : PopupRenderAction.None,
                overlaySink,
                null,
                generation,
                overlaySinkGeneration,
                previousAutoClose,
                null);
        }

        ArgumentNullException.ThrowIfNull(popup);
        var nextAutoClose = popup.ExpiresAt is null ? null : new CancellationTokenSource();
        var nextGeneration = unchecked(popupState.Generation + 1);
        var oldAutoClose = popupState.AutoClose;
        popupState = new(
            popup,
            shortcuts ?? popupState.Shortcuts,
            nextAutoClose,
            nextGeneration);
        return new(
            true,
            PopupRenderAction.Show,
            overlaySink,
            popup,
            nextGeneration,
            overlaySinkGeneration,
            oldAutoClose,
            nextAutoClose is null
                ? null
                : new(
                    nextGeneration,
                    popup.ExpiresAt!.Value,
                    nextAutoClose,
                    nextAutoClose.Token));
    }

    static void ApplyPopupTransition(PopupTransitionEffect effect)
    {
        CancelAndDispose(effect.PreviousAutoClose);
        if (!effect.Changed)
            return;

        switch (effect.Render)
        {
            case PopupRenderAction.Show:
                if (effect.Sink is null ||
                    !TryBeginSinkCallout(effect.Sink, effect.SinkGeneration))
                {
                    return;
                }
                try
                {
                    try
                    {
                        effect.Sink.ShowPopup(effect.Popup!, effect.Generation);
                    }
                    finally
                    {
                        EndSinkCallout();
                    }
                }
                catch
                {
                    PopupTransitionEffect rollback;
                    lock (stateGate)
                    {
                        rollback = TransitionPopupLocked(
                            PopupTransitionKind.Hide,
                            expectedGeneration: effect.Generation);
                    }
                    CancelAndDispose(rollback.PreviousAutoClose);
                    throw;
                }
                break;
            case PopupRenderAction.Hide:
                if (effect.Sink is null ||
                    !TryBeginSinkCallout(effect.Sink, effect.SinkGeneration))
                {
                    return;
                }
                try
                {
                    effect.Sink.HidePopup(effect.Generation);
                }
                finally
                {
                    EndSinkCallout();
                }
                break;
            case PopupRenderAction.Detach:
                effect.Sink!.HidePopup(effect.Generation);
                break;
        }

        if (effect.AutoClose is { } autoClose)
        {
            lock (stateGate)
            {
                if (popupState.Generation != autoClose.Generation ||
                    !ReferenceEquals(popupState.AutoClose, autoClose.Source))
                {
                    return;
                }
            }
            _ = AutoClosePopupAsync(
                autoClose.Generation,
                autoClose.ExpiresAt,
                autoClose.CancellationToken);
        }
    }

    static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null)
            return;
        source.Cancel();
        source.Dispose();
    }

    static bool TryBeginSinkCallout(IUiInputSink sink, long sinkGeneration)
    {
        lock (stateGate)
        {
            if (overlaySinkDetaching ||
                overlaySinkGeneration != sinkGeneration ||
                !ReferenceEquals(overlaySink, sink))
            {
                return false;
            }

            activeSinkCallouts++;
            return true;
        }
    }

    static void EndSinkCallout()
    {
        lock (stateGate)
        {
            activeSinkCallouts--;
            if (activeSinkCallouts == 0)
                Monitor.PulseAll(stateGate);
        }
    }

    static async Task<bool> TryHandleWorkspaceCommandAsync(
        IUiInputSink sink,
        long sinkGeneration,
        Command command)
    {
        if (!TryBeginSinkCallout(sink, sinkGeneration))
            return false;
        try
        {
            return await sink.TryHandleWorkspaceCommandAsync(command);
        }
        finally
        {
            EndSinkCallout();
        }
    }

    static async Task AutoClosePopupAsync(
        int generation,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = expiresAt - DateTimeOffset.Now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
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

    internal static long RegisterNotificationShortcuts(
        DateTimeOffset expiresAt,
        IReadOnlyList<UiShortcut> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        if (shortcuts.Count == 0)
            return 0;

        var entries = CreateTransientShortcutEntries(shortcuts);
        lock (stateGate)
        {
            RemoveExpiredNotificationShortcutsLocked(DateTimeOffset.Now);
            var id = unchecked(++notificationShortcutRegistrationId);
            notificationShortcutRegistrations[id] = new(expiresAt, entries);
            return id;
        }
    }

    internal static void UnregisterNotificationShortcuts(long registrationId)
    {
        if (registrationId == 0)
            return;
        lock (stateGate)
            notificationShortcutRegistrations.Remove(registrationId);
    }

    static TransientShortcutEntry[] CreateTransientShortcutEntries(
        IReadOnlyList<UiShortcut> shortcuts)
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
                new(string.Empty, shortcut.Handler, owner));
        }
        return entries;
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

    static async Task InvokeSafely(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex)
        {
            var failure = new InvalidOperationException($"热键处理失败: {ex.Message}", ex);
            TerminalUi.Notify("Keyboard", failure.Message, UiSeverity.Error);
            TerminalUi.LogException("Keyboard", failure);
        }
    }

    static async Task InvokeSafely(HotkeyEntry entry)
    {
        using var registrationScope = RegisterScope(entry.Owner!);
        if (entry.Owner is IPlugin plugin)
        {
            await InvokeSafely(async () =>
            {
                using var callback = PluginManager.TryEnterPluginCallback(plugin);
                if (callback is null)
                    return;

                using var callbackScope = PluginManager.EnterPluginCallbackScope();
                await entry.Handler();
            });
            return;
        }

        await InvokeSafely(entry.Handler);
    }

    sealed class RegistrationScope(object? previous) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                registrationOwner.Value = previous;
        }
    }

    enum PopupTransitionKind
    {
        Display,
        Hide,
        Detach,
        ReplaceShortcuts
    }

    enum PopupRenderAction
    {
        None,
        Show,
        Hide,
        Detach
    }

    readonly record struct PopupState(
        HotkeyPopup? Popup,
        IReadOnlyList<TransientShortcutEntry> Shortcuts,
        CancellationTokenSource? AutoClose,
        int Generation);

    readonly record struct PopupSnapshot(
        HotkeyPopup? Popup,
        int Generation,
        IUiInputSink? Sink,
        long SinkGeneration,
        TimeSpan AutoCloseDelay);

    readonly record struct PopupTransitionEffect(
        bool Changed,
        PopupRenderAction Render,
        IUiInputSink? Sink,
        HotkeyPopup? Popup,
        int Generation,
        long SinkGeneration,
        CancellationTokenSource? PreviousAutoClose,
        AutoCloseRequest? AutoClose);

    readonly record struct AutoCloseRequest(
        int Generation,
        DateTimeOffset ExpiresAt,
        CancellationTokenSource Source,
        CancellationToken CancellationToken);

    sealed record TransientShortcutEntry(
        ConsoleKey Key,
        ConsoleModifiers Modifiers,
        HotkeyEntry Entry);

    sealed record NotificationShortcutRegistration(
        DateTimeOffset ExpiresAt,
        IReadOnlyList<TransientShortcutEntry> Shortcuts);
}
