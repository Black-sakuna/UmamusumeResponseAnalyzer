using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;

namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    internal sealed class UiHost : IKeyboardOverlaySink
    {
        const int MaxLogLines = 300;
        const int RunStateRunning = 1;
        const int RunStateStopped = 2;

        readonly Channel<UiEvent> events = CreateUiChannel<UiEvent>();
        readonly UiRefreshSignal refreshSignal = new();
        readonly NotificationPopupRenderer popupRenderer = new();

        readonly Dictionary<LiveDisplayWorkspace, WorkspaceState> workspaces = [];
        readonly Dictionary<(LiveDisplayWorkspace Workspace, string PluginId, string Key), LiveDisplayPanel> panels = [];
        readonly List<PendingWorkspaceRemoval> pendingWorkspaceRemovals = [];
        readonly object workspaceIdentityGate = new();
        readonly Dictionary<string, WorkspaceRegistration> workspaceRegistrations = new(StringComparer.OrdinalIgnoreCase);
        readonly object removedWorkspaceGate = new();
        readonly HashSet<LiveDisplayWorkspace> removedWorkspaces = new(ReferenceEqualityComparer.Instance);
        readonly List<(long Sequence, LiveDisplayLogLine Line)> globalLogs = [];
        readonly Dictionary<LiveDisplayWorkspace, List<(long Sequence, LiveDisplayLogLine Line)>> workspaceLogs =
            new(ReferenceEqualityComparer.Instance);
        readonly List<LiveDisplayNotification> notifications = [];
        readonly Channel<ConsoleInteractionRequest> consoleInteractions = CreateUiChannel<ConsoleInteractionRequest>();

        LiveDisplayWorkspace? currentWorkspace;
        LiveDisplayWorkspace? activeWorkspace;
        string[] workspaceCompletionTitles = [];
        KeyboardPopup? keyboardPopup;
        int keyboardPopupGeneration;
        KeyboardCommandInput? commandInput;
        int lastViewportWidth;
        int lastViewportHeight;
        int lastViewportMaxScroll;
        bool shutdownRequested;
        long logSequence;
        int runState;
        internal static bool? HasInteractiveConsoleOverrideForTests { get; set; }
        internal static Action? ClearConsoleOverrideForTests { get; set; }
        internal static Func<CancellationToken, Task>? RunLiveDisplayUntilConsoleInteractionOverrideForTests { get; set; }
        int acceptingEvents = 1;

        public ILiveDisplayOutput ForPlugin(string pluginId) => new PluginLiveDisplayOutput(pluginId, this);
        public LiveDisplayWorkspace? CurrentWorkspace => Volatile.Read(ref currentWorkspace);
        internal IReadOnlyList<IReadOnlyList<string>> GetWorkspaceSnapshotPanelKeysForTests(LiveDisplayWorkspace workspace)
        {
            return workspaces.TryGetValue(workspace, out var state) && ReferenceEquals(state.Workspace, workspace)
                ? state.History
                    .Select(x => (IReadOnlyList<string>)x.Panels.Select(panel => panel.Key).ToArray())
                    .ToArray()
                : [];
        }

        internal IReadOnlyList<LiveDisplayLogLine> GetLogsForTests(LiveDisplayWorkspace? workspace)
        {
            var source = workspace is null
                ? globalLogs
                : workspaceLogs.GetValueOrDefault(workspace) ?? [];
            return source.Select(x => x.Line).ToArray();
        }

        internal IReadOnlyList<LiveDisplayNotification> GetNotificationsForTests(LiveDisplayWorkspace? workspace)
        {
            return notifications
                .Where(x => ReferenceEquals(x.Workspace, workspace))
                .ToArray();
        }

        internal KeyboardPopup? GetKeyboardPopupForTests()
        {
            DrainEvents();
            return keyboardPopup;
        }

        internal bool ShouldRefreshPopupCountdownForTests(DateTimeOffset now)
        {
            return popupRenderer.ShouldRefreshPopupCountdown(VisibleNotifications(), keyboardPopup, now);
        }

        public LiveDisplayWorkspace CreateWorkspace(string title, int historyCapacity = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(historyCapacity);
            var workspace = LiveDisplayWorkspace.Create(title);
            lock (workspaceIdentityGate)
            {
                if (workspaceRegistrations.TryGetValue(workspace.Title, out var existing))
                {
                    if (existing.HistoryCapacity != historyCapacity)
                    {
                        throw new InvalidOperationException(
                            $"Workspace '{workspace.Title}' 已使用 historyCapacity={existing.HistoryCapacity} 注册，不能改为 {historyCapacity}。");
                    }

                    return existing.Workspace;
                }

                workspaceRegistrations[workspace.Title] = new(workspace, historyCapacity);
                SetCurrentWorkspaceIfEmpty(workspace);
                Post(new UiEvent.RegisterWorkspace(workspace, historyCapacity));
            }

            return workspace;
        }

        public void RegisterWorkspace(LiveDisplayWorkspace workspace, int historyCapacity = 0)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentOutOfRangeException.ThrowIfNegative(historyCapacity);
            if (IsRemovedWorkspace(workspace))
                return;

            SetCurrentWorkspaceIfEmpty(workspace);
            Post(new UiEvent.RegisterWorkspace(workspace, historyCapacity));
        }

        public void RemoveWorkspace(LiveDisplayWorkspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            TryTombstoneWorkspace(workspace, preferredReplacement: null, queueRemoval: true, out _);
        }

        public void CaptureWorkspaceSnapshot(LiveDisplayWorkspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            if (!IsRemovedWorkspace(workspace))
                Post(new UiEvent.CaptureWorkspaceSnapshot(workspace));
        }

        internal void RemoveWorkspaceWhenAnotherPanelActivates(LiveDisplayWorkspace workspace, Action? removed = null)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            Post(new UiEvent.RemoveWorkspaceWhenAnotherPanelActivates(workspace, removed));
        }

        public void SetPanel(LiveDisplayPanel panel, bool switchToWorkspace = true) => Post(new UiEvent.SetPanel(panel, switchToWorkspace));
        public void Log(LiveDisplayLogLine line) => Post(new UiEvent.Log(line));
        public void Notify(LiveDisplayNotification notification)
        {
            if (notification.Workspace is not null && IsRemovedWorkspace(notification.Workspace))
                return;

            var registrationId = KeyboardManager.RegisterNotificationShortcuts(
                notification.Workspace,
                notification.ExpiresAt,
                notification.Shortcuts);
            if (notification.Workspace is not null && IsRemovedWorkspace(notification.Workspace))
            {
                KeyboardManager.UnregisterNotificationShortcuts(registrationId);
                return;
            }

            if (!Post(new UiEvent.Notify(notification with
            {
                Shortcuts = [],
                ShortcutRegistrationId = registrationId
            })))
            {
                KeyboardManager.UnregisterNotificationShortcuts(registrationId);
            }
        }
        public void SwitchWorkspace(LiveDisplayWorkspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            if (IsRemovedWorkspace(workspace))
                return;

            Volatile.Write(ref currentWorkspace, workspace);
            Post(new UiEvent.SwitchWorkspace(workspace));
        }
        public void BindWorkspaceHotkey(
            LiveDisplayWorkspace workspace,
            ConsoleKey key,
            ConsoleModifiers modifiers = 0,
            string? description = null)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            if (IsRemovedWorkspace(workspace))
                return;

            var shortcutText = KeyboardManager.FormatKeyCombo(key, modifiers);
            var entry = KeyboardManager.RegisterTracked(
                key,
                modifiers,
                description ?? $"切换到 {workspace.Title}",
                () =>
                {
                    SwitchWorkspace(workspace);
                    return Task.CompletedTask;
            });
            RegisterWorkspace(workspace, HistoryCapacityOf(workspace));
            Post(new UiEvent.SetWorkspaceShortcut(workspace, key, modifiers, shortcutText, entry));
        }

        public void RequestShutdown() => Post(new UiEvent.Shutdown());
        internal Task HandleCommandAsync(string command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!command.StartsWith('/'))
                return Task.CompletedTask;

            return LiveDisplayConsole.RunAsync(() =>
            {
                try
                {
                    RunCommand(command);
                }
                catch (Exception ex)
                {
                    LogCommandWarning($"命令执行失败: {ex.Message}");
                }
                return Task.CompletedTask;
            });
        }

        internal IReadOnlyList<string> CompleteCommand(string input)
        {
            ArgumentNullException.ThrowIfNull(input);
            if (!input.StartsWith('/'))
                return [];

            var body = input[1..];
            var spaceIndex = body.IndexOf(' ');
            if (spaceIndex < 0)
                return CompleteByPrefix(input, ["/plugin", "/workspace"]);

            var name = body[..spaceIndex];
            var rest = body[(spaceIndex + 1)..];
            return name.ToLowerInvariant() switch
            {
                "workspace" => CompleteWorkspaceCommand(rest),
                "plugin" => CompletePluginCommand(rest),
                _ => []
            };
        }

        void IKeyboardOverlaySink.ShowPopup(KeyboardPopup popup, int generation) => Post(new UiEvent.ShowPopup(popup, generation));
        void IKeyboardOverlaySink.HidePopup(int generation) => Post(new UiEvent.HidePopup(generation));
        void IKeyboardOverlaySink.ShowCommandInput(KeyboardCommandInput input) => Post(new UiEvent.ShowCommandInput(input));
        void IKeyboardOverlaySink.HideCommandInput() => Post(new UiEvent.HideCommandInput());
        Task<bool> IKeyboardOverlaySink.TryHandleWorkspaceKeyAsync(ConsoleKeyInfo keyInfo)
        {
            if (keyInfo.Modifiers != 0 || keyInfo.Key is not (
                ConsoleKey.UpArrow or ConsoleKey.DownArrow or ConsoleKey.PageUp or ConsoleKey.PageDown or
                ConsoleKey.Home or ConsoleKey.End or ConsoleKey.LeftArrow or ConsoleKey.RightArrow))
            {
                return Task.FromResult(false);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Post(new UiEvent.NavigateWorkspace(keyInfo.Key, completion)))
                completion.TrySetResult(false);
            else if (!IsRunning)
                DrainEvents();

            return completion.Task;
        }
        internal bool IsRunning => Volatile.Read(ref runState) == RunStateRunning;

        internal string RenderSnapshotForTests(int width = 120, int height = 35)
        {
            if (IsRunning)
                throw new InvalidOperationException("RenderSnapshotForTests 只能在 UiHost 未运行时用于测试或诊断。");

            DrainEvents();
            RemoveExpiredNotifications(DateTimeOffset.Now);
            lastViewportWidth = width;
            lastViewportHeight = height;
            using var layout = BuildLayout(width, height);
            return WorkspaceLayoutBuilder.BuildTextSnapshot(
                activeWorkspace,
                DisplayedPanels(),
                VisibleLogs(),
                WorkspaceLabel);
        }

        public Task RunAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken, firstRenderGate: null);

        internal async Task RunAsync(CancellationToken cancellationToken, Task? firstRenderGate)
        {
            try
            {
                await LiveDisplayConsole.ConsoleInputGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (Interlocked.CompareExchange(ref runState, RunStateStopped, 0) == 0)
                    CompleteRun();
                return;
            }

            try
            {
                if (Interlocked.CompareExchange(ref runState, RunStateRunning, 0) != 0)
                    throw new InvalidOperationException("UiHost.RunAsync 已在运行中。");
            }
            finally
            {
                LiveDisplayConsole.ConsoleInputGate.Release();
            }

            try
            {
                if (!HasInteractiveConsole())
                {
                    await RunHeadlessAsync(cancellationToken);
                    return;
                }

                ClearConsole();

                if (firstRenderGate is not null)
                    await DrainConsoleInteractionsBeforeFirstRenderAsync(firstRenderGate, cancellationToken);

                while (!cancellationToken.IsCancellationRequested && !shutdownRequested)
                {
                    var request = await RunLiveDisplayUntilConsoleInteractionAsync(cancellationToken);
                    if (request is null)
                        continue;

                    await ExecuteConsoleInteractionAsync(request, clearConsole: true, cancellationToken);
                }
            }
            finally
            {
                await LiveDisplayConsole.ConsoleInputGate.WaitAsync();
                try
                {
                    CompleteRun();
                }
                finally
                {
                    LiveDisplayConsole.ConsoleInputGate.Release();
                }
            }
        }

        void CompleteRun()
        {
            Volatile.Write(ref runState, RunStateStopped);
            Volatile.Write(ref acceptingEvents, 0);
            events.Writer.TryComplete();
            consoleInteractions.Writer.TryComplete();
            FailPendingConsoleInteractions();
            ReleasePendingUiEvents();
        }

        async Task DrainConsoleInteractionsBeforeFirstRenderAsync(Task firstRenderGate, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !shutdownRequested)
            {
                while (consoleInteractions.Reader.TryRead(out var interaction))
                    await ExecuteConsoleInteractionAsync(interaction, clearConsole: true, cancellationToken);

                if (firstRenderGate.IsCompleted && !consoleInteractions.Reader.TryPeek(out _))
                {
                    await firstRenderGate;
                    return;
                }

                var waitForGate = firstRenderGate.WaitAsync(cancellationToken);
                var waitForInteraction = consoleInteractions.Reader.WaitToReadAsync(cancellationToken).AsTask();
                await Task.WhenAny(waitForGate, waitForInteraction);
            }
        }

        async Task<ConsoleInteractionRequest?> RunLiveDisplayUntilConsoleInteractionAsync(CancellationToken cancellationToken)
        {
            if (RunLiveDisplayUntilConsoleInteractionOverrideForTests is { } runOverride)
            {
                await runOverride(cancellationToken);
                return null;
            }

            await Task.Yield();
            using IApplication app = Application.Create();
            app.Init();
            KeyboardManager.RefreshConsoleInputMode();
            var driver = app.Driver ?? throw new InvalidOperationException("Terminal.Gui 未创建 output driver。");
            using var window = new Window { Title = "UmamusumeResponseAnalyzer", CanFocus = false };
            View? surface = null;
            var width = GetConsoleWidth();
            var height = GetConsoleHeight();

            void Rebuild()
            {
                if (surface is not null)
                {
                    window.Remove(surface);
                    surface.Dispose();
                }
                surface = BuildLayout(width, height);
                window.Add(surface);
                window.SetNeedsLayout();
                window.SetNeedsDraw();
            }

            var session = app.Begin(window) ?? throw new InvalidOperationException("Terminal.Gui 未创建 runnable session。");
            try
            {
                Rebuild();
                app.LayoutAndDraw(forceRedraw: true);
                driver.Refresh();
                while (!cancellationToken.IsCancellationRequested &&
                       !shutdownRequested &&
                       !consoleInteractions.Reader.TryPeek(out _))
                {
                    await Task.Delay(50, cancellationToken);
                    var changed = DrainEvents();
                    var now = DateTimeOffset.Now;
                    changed |= RemoveExpiredNotifications(now);
                    changed |= popupRenderer.ShouldRefreshPopupCountdown(VisibleNotifications(), keyboardPopup, now);
                    var currentWidth = GetConsoleWidth();
                    var currentHeight = GetConsoleHeight();
                    if (currentWidth != width || currentHeight != height)
                    {
                        width = currentWidth;
                        height = currentHeight;
                        changed = true;
                    }
                    if (changed)
                    {
                        Rebuild();
                        app.LayoutAndDraw(forceRedraw: true);
                        driver.Refresh();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                app.End(session);
            }

            if (cancellationToken.IsCancellationRequested || shutdownRequested)
                return null;

            return consoleInteractions.Reader.TryRead(out var pendingInteraction)
                ? pendingInteraction
                : null;
        }

        async Task RunHeadlessAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !shutdownRequested)
            {
                DrainEvents();
                var now = DateTimeOffset.Now;
                RemoveExpiredNotifications(now);
                while (consoleInteractions.Reader.TryRead(out var interaction))
                    await ExecuteConsoleInteractionAsync(interaction, clearConsole: false, cancellationToken);

                try { await refreshSignal.WaitForAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        internal bool TryQueueConsoleInteraction(Func<Task> action, out Task completion)
        {
            if (!IsRunning)
            {
                completion = Task.CompletedTask;
                return false;
            }

            var request = new ConsoleInteractionRequest(action);
            if (!consoleInteractions.Writer.TryWrite(request))
            {
                completion = Task.CompletedTask;
                return false;
            }

            refreshSignal.Signal();
            completion = request.Completion;
            return true;
        }

        static async Task ExecuteConsoleInteractionAsync(
            ConsoleInteractionRequest request,
            bool clearConsole,
            CancellationToken cancellationToken)
        {
            Exception? actionFailure = null;
            try
            {
                await LiveDisplayConsole.ExecuteConsoleInputInteractionAsync(
                    async () =>
                    {
                        try
                        {
                            if (clearConsole)
                                ClearConsole();

                            await request.Action();
                        }
                        catch (Exception ex)
                        {
                            actionFailure = ex;
                        }
                        finally
                        {
                            if (clearConsole)
                                ClearConsole();
                        }
                    },
                    cancellationToken);
            }
            catch (Exception lifecycleFailure)
            {
                if (actionFailure is not null)
                {
                    LiveDisplayConsole.LogSecondaryInputFailure("interaction lifecycle", lifecycleFailure);

                    request.SetException(actionFailure);
                    ExceptionDispatchInfo.Capture(actionFailure).Throw();
                }

                request.SetException(lifecycleFailure);
                throw;
            }

            if (actionFailure is null)
                request.SetResult();
            else
                request.SetException(actionFailure);
        }

        static void ClearConsole() => (ClearConsoleOverrideForTests ?? Console.Clear)();

        void FailPendingConsoleInteractions()
        {
            while (consoleInteractions.Reader.TryRead(out var interaction))
                interaction.SetException(new OperationCanceledException("LiveDisplay 已停止，无法执行 console interaction。"));
        }

        bool Post(UiEvent uiEvent)
        {
            if (Volatile.Read(ref acceptingEvents) == 0)
                return false;

            if (!events.Writer.TryWrite(uiEvent))
                return false;

            refreshSignal.Signal();
            return true;
        }

        void ReleasePendingUiEvents()
        {
            foreach (var notification in notifications)
                KeyboardManager.UnregisterNotificationShortcuts(notification.ShortcutRegistrationId);
            while (events.Reader.TryRead(out var uiEvent))
            {
                switch (uiEvent)
                {
                    case UiEvent.Notify notify:
                        KeyboardManager.UnregisterNotificationShortcuts(notify.Notification.ShortcutRegistrationId);
                        break;
                    case UiEvent.NavigateWorkspace navigation:
                        navigation.Completion.TrySetResult(false);
                        break;
                }
            }
        }

        bool DrainEvents()
        {
            var changed = false;
            while (events.Reader.TryRead(out var uiEvent))
            {
                changed = true;
                Apply(uiEvent);
            }
            return changed;
        }

        void Apply(UiEvent uiEvent)
        {
            switch (uiEvent)
            {
                case UiEvent.RegisterWorkspace registerWorkspace:
                    RegisterKnownWorkspace(registerWorkspace.Workspace, registerWorkspace.HistoryCapacity);
                    break;
                case UiEvent.RemoveWorkspace removeWorkspace:
                    RemoveWorkspaceState(
                        removeWorkspace.Workspace,
                        removeWorkspace.Replacement,
                        removeWorkspace.Removed);
                    break;
                case UiEvent.CaptureWorkspaceSnapshot captureWorkspaceSnapshot:
                    CaptureWorkspaceSnapshotState(captureWorkspaceSnapshot.Workspace);
                    break;
                case UiEvent.SetWorkspaceShortcut setWorkspaceShortcut:
                    SetWorkspaceShortcut(
                        setWorkspaceShortcut.Workspace,
                        setWorkspaceShortcut.Key,
                        setWorkspaceShortcut.Modifiers,
                        setWorkspaceShortcut.ShortcutText,
                        setWorkspaceShortcut.Entry);
                    break;
                case UiEvent.RemoveWorkspaceWhenAnotherPanelActivates removeWorkspace:
                    ArmWorkspaceRemovalWhenAnotherPanelActivates(removeWorkspace.Workspace, removeWorkspace.Removed);
                    break;
                case UiEvent.SetPanel setPanel:
                    if (IsRemovedWorkspace(setPanel.Panel.Workspace))
                        break;

                    RegisterKnownWorkspace(setPanel.Panel.Workspace, HistoryCapacityOf(setPanel.Panel.Workspace));
                    panels[(setPanel.Panel.Workspace, setPanel.Panel.PluginId, setPanel.Panel.Key)] = setPanel.Panel;
                    if (setPanel.SwitchToWorkspace && !IsBrowsingHistory())
                    {
                        RemovePendingWorkspacesAfterPanelActivation(setPanel.Panel.Workspace);
                        if (!Equals(activeWorkspace, setPanel.Panel.Workspace))
                        {
                            activeWorkspace = setPanel.Panel.Workspace;
                            Volatile.Write(ref currentWorkspace, setPanel.Panel.Workspace);
                        }
                    }
                    break;
                case UiEvent.Log log:
                    if (log.Line.Workspace is not null && IsRemovedWorkspace(log.Line.Workspace))
                        break;

                    if (log.Line.Workspace is not null)
                        RegisterKnownWorkspace(log.Line.Workspace, HistoryCapacityOf(log.Line.Workspace));
                    AddLog(log.Line);
                    break;
                case UiEvent.Notify notify:
                    if (notify.Notification.Workspace is not null && IsRemovedWorkspace(notify.Notification.Workspace))
                    {
                        KeyboardManager.UnregisterNotificationShortcuts(notify.Notification.ShortcutRegistrationId);
                        break;
                    }

                    if (notify.Notification.Workspace is not null)
                        RegisterKnownWorkspace(notify.Notification.Workspace, HistoryCapacityOf(notify.Notification.Workspace));
                    notifications.Add(notify.Notification);
                    break;
                case UiEvent.SwitchWorkspace switchWorkspace:
                    if (IsRemovedWorkspace(switchWorkspace.Workspace))
                        break;

                    RegisterKnownWorkspace(switchWorkspace.Workspace, HistoryCapacityOf(switchWorkspace.Workspace));
                    activeWorkspace = switchWorkspace.Workspace;
                    Volatile.Write(ref currentWorkspace, switchWorkspace.Workspace);
                    break;
                case UiEvent.NavigateWorkspace navigateWorkspace:
                    navigateWorkspace.Completion.TrySetResult(TryNavigateWorkspace(navigateWorkspace.Key));
                    break;
                case UiEvent.RunCommand runCommand:
                    try
                    {
                        RunCommand(runCommand.Command);
                    }
                    catch (Exception ex)
                    {
                        LogCommandWarning($"命令执行失败: {ex.Message}");
                    }
                    break;
                case UiEvent.ShowPopup showPopup:
                    if (showPopup.Generation >= keyboardPopupGeneration)
                    {
                        keyboardPopup = showPopup.Popup;
                        keyboardPopupGeneration = showPopup.Generation;
                    }
                    break;
                case UiEvent.HidePopup hidePopup:
                    if (hidePopup.Generation >= keyboardPopupGeneration)
                    {
                        keyboardPopup = null;
                        keyboardPopupGeneration = hidePopup.Generation;
                    }
                    break;
                case UiEvent.ShowCommandInput showCommandInput:
                    keyboardPopup = null;
                    commandInput = showCommandInput.Input;
                    break;
                case UiEvent.HideCommandInput:
                    commandInput = null;
                    break;
                case UiEvent.Shutdown:
                    shutdownRequested = true;
                    break;
            }
        }

        View BuildLayout(int width, int height)
        {
            if (width <= 0)
                width = 120;
            if (height <= 0)
                height = 35;

            var state = activeWorkspace is not null && workspaces.TryGetValue(activeWorkspace, out var activeState)
                ? activeState
                : null;
            var layout = WorkspaceLayoutBuilder.BuildWorkspaceLayout(
                activeWorkspace,
                DisplayedPanels(),
                VisibleLogs(),
                WorkspaceLabel,
                width,
                height,
                state?.ScrollOffset ?? 0);
            var content = new View { Width = Dim.Fill(), Height = Dim.Fill(), CanFocus = false };
            content.Add(layout.View);
            lastViewportWidth = width;
            lastViewportHeight = height;
            lastViewportMaxScroll = layout.MaxScroll;
            if (activeWorkspace is { } workspace && state is not null && state.ScrollOffset > layout.MaxScroll)
                workspaces[workspace] = state with { ScrollOffset = layout.MaxScroll };

            var popupWidth = NotificationPopupRenderer.GetPopupWidth(width);
            var now = DateTimeOffset.Now;
            if (popupWidth > 0)
            {
                var activeNotifications = VisibleNotifications();
                if (activeNotifications.Count > 0)
                {
                    var lines = popupRenderer.BuildLines(
                        activeNotifications,
                        popupWidth,
                        Math.Max(0, height - 1),
                        now,
                        WorkspaceLabel);
                    content.Add(new TextView
                    {
                        Text = string.Join(Environment.NewLine, lines),
                        ReadOnly = true,
                        WordWrap = false,
                        CanFocus = false,
                        X = Math.Max(0, width - popupWidth - 1),
                        Y = 1,
                        Width = popupWidth,
                        Height = Math.Min(lines.Count, Math.Max(1, height - 1))
                    });
                }
            }

            if (keyboardPopup is not null)
            {
                var lines = keyboardPopup.Lines
                    .Select((line, index) => keyboardPopup.Selection?.SelectedLineIndex == index ? $"> {line.Text}" : $"  {line.Text}")
                    .Skip(keyboardPopup.ScrollOffset)
                    .Take(Math.Max(1, height - 2))
                    .ToArray();
                content.Add(new TextView
                {
                    Text = string.Join(Environment.NewLine, lines),
                    ReadOnly = true,
                    WordWrap = false,
                    CanFocus = false,
                    X = 0,
                    Y = Math.Max(0, height - lines.Length),
                    Width = Dim.Fill(),
                    Height = Math.Max(1, lines.Length)
                });
            }

            if (commandInput is not null)
            {
                var lines = commandInput.CompletionCandidates
                    .Take(Math.Max(0, height - 2))
                    .Append($"> {commandInput.Text}")
                    .ToArray();
                content.Add(new TextView
                {
                    Text = string.Join(Environment.NewLine, lines),
                    ReadOnly = true,
                    WordWrap = false,
                    CanFocus = false,
                    X = 0,
                    Y = Math.Max(0, height - lines.Length),
                    Width = Dim.Fill(),
                    Height = Math.Max(1, lines.Length)
                });
            }

            return content;
        }

        string WorkspaceLabel(LiveDisplayWorkspace workspace)
        {
            return workspaces.TryGetValue(workspace, out var state) && !string.IsNullOrEmpty(state.ShortcutText)
                ? $"{state.ShortcutText} {workspace.Title}"
                : workspace.Title;
        }

        IReadOnlyCollection<LiveDisplayPanel> DisplayedPanels()
        {
            if (activeWorkspace is null ||
                !workspaces.TryGetValue(activeWorkspace, out var state) ||
                state.HistoryOffset < 0)
            {
                return panels.Values;
            }

            return state.History[state.HistoryOffset].Panels;
        }

        bool TryNavigateWorkspace(ConsoleKey key)
        {
            if (activeWorkspace is null || !workspaces.TryGetValue(activeWorkspace, out var state))
                return false;

            if (key == ConsoleKey.LeftArrow)
            {
                var historyOffset = state.HistoryOffset < 0
                    ? state.History.Count - 1
                    : state.HistoryOffset - 1;
                if (historyOffset < 0)
                    return false;

                workspaces[activeWorkspace] = state with { HistoryOffset = historyOffset };
                return true;
            }

            if (key == ConsoleKey.RightArrow)
            {
                if (state.HistoryOffset < 0)
                    return false;

                var historyOffset = state.HistoryOffset == state.History.Count - 1
                    ? -1
                    : state.HistoryOffset + 1;
                workspaces[activeWorkspace] = state with { HistoryOffset = historyOffset };
                return true;
            }

            if (lastViewportWidth <= 0 || lastViewportHeight <= 0)
                return false;

            var maxScroll = lastViewportMaxScroll;
            var currentOffset = Math.Clamp(state.ScrollOffset, 0, maxScroll);
            var nextOffset = key switch
            {
                ConsoleKey.UpArrow => Math.Min(maxScroll, currentOffset + 1),
                ConsoleKey.DownArrow => Math.Max(0, currentOffset - 1),
                ConsoleKey.PageUp => Math.Min(maxScroll, currentOffset + lastViewportHeight),
                ConsoleKey.PageDown => Math.Max(0, currentOffset - lastViewportHeight),
                ConsoleKey.Home => maxScroll,
                ConsoleKey.End => 0,
                _ => currentOffset
            };
            workspaces[activeWorkspace] = state with { ScrollOffset = nextOffset };
            return nextOffset != currentOffset;
        }

        void RunCommand(string command)
        {
            if (!command.StartsWith('/'))
                return;

            var body = command[1..].Trim();
            if (string.IsNullOrEmpty(body))
            {
                LogCommandWarning("命令为空。");
                return;
            }

            var (name, rest) = SplitCommand(body);
            if (string.Equals(name, "workspace", StringComparison.OrdinalIgnoreCase))
            {
                RunWorkspaceCommand(rest);
                return;
            }

            if (string.Equals(name, "plugin", StringComparison.OrdinalIgnoreCase))
            {
                RunPluginCommand(rest);
                return;
            }

            LogCommandWarning($"未知命令: /{name}");
        }

        void RunWorkspaceCommand(string arguments)
        {
            var (subcommand, rest) = SplitCommand(arguments);
            if (string.IsNullOrEmpty(subcommand))
            {
                ShowWorkspaceSwitcher();
                return;
            }

            if (string.Equals(subcommand, "list", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(rest))
                    KeyboardManager.ShowPopup(BuildWorkspaceList().Context);
                else
                    LogWorkspaceUsage();
                return;
            }

            if (string.Equals(subcommand, "switch", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(rest))
                    ShowWorkspaceSwitcher();
                else
                    SwitchWorkspaceByTitle(rest);
                return;
            }

            LogWorkspaceUsage();
        }

        void ShowWorkspaceSwitcher()
        {
            var list = BuildWorkspaceList();
            if (list.Entries.Count == 0)
            {
                KeyboardManager.ShowPopup(list.Context);
                return;
            }

            var selectedIndex = Math.Max(0, list.Entries.FindIndex(x => x.Workspace == activeWorkspace));
            var workspacesByLine = list.Entries.ToDictionary(x => x.LineIndex, x => x.Workspace);
            KeyboardManager.ShowPopup(list.Context.ToPopup(selection: new KeyboardPopupSelection(
                list.Entries.Select(x => x.LineIndex).ToArray(),
                selectedIndex,
                lineIndex =>
                {
                    if (workspacesByLine.TryGetValue(lineIndex, out var workspace))
                        SwitchWorkspace(workspace);
                    return Task.CompletedTask;
                })));
        }

        WorkspacePopupList BuildWorkspaceList()
        {
            var context = new KeyboardHandlerContext().WriteLine("Workspaces");
            var entries = new List<WorkspacePopupEntry>();
            if (workspaces.Count == 0)
            {
                context.WriteLine("（没有已注册 workspace）", ConsoleColor.DarkGray);
                return new WorkspacePopupList(context, entries);
            }

            foreach (var (workspace, state) in workspaces)
            {
                var lineIndex = context.LineCount;
                var marker = workspace == activeWorkspace ? "*" : " ";
                var shortcut = string.IsNullOrEmpty(state.ShortcutText) ? string.Empty : $" [{state.ShortcutText}]";
                context.WriteLine($"{marker} {workspace.Title}{shortcut}");
                entries.Add(new WorkspacePopupEntry(lineIndex, workspace));
            }

            return new WorkspacePopupList(context, entries);
        }

        void SwitchWorkspaceByTitle(string title)
        {
            title = ParseWorkspaceTitle(title);

            var workspace = workspaces.Keys.FirstOrDefault(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase));
            if (workspace is null)
            {
                LogCommandWarning($"workspace 不存在: {title}");
                return;
            }

            activeWorkspace = workspace;
            Volatile.Write(ref currentWorkspace, workspace);
        }

        void LogWorkspaceUsage()
        {
            LogCommandWarning("用法: /workspace | /workspace switch [<title>|\"<title>\"] | /workspace list");
        }

        void RunPluginCommand(string arguments)
        {
            var (subcommand, rest) = SplitCommand(arguments);
            if (string.IsNullOrEmpty(subcommand))
            {
                ShowPluginList();
                return;
            }

            if (string.Equals(subcommand, "list", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(rest))
                    ShowPluginList();
                else
                    LogPluginUsage();
                return;
            }

            var normalizedSubcommand = subcommand.ToLowerInvariant();
            if (normalizedSubcommand is not ("load" or "unload" or "reload"))
            {
                LogPluginUsage();
                return;
            }

            var (pluginName, extra) = SplitCommand(rest);
            if (string.IsNullOrEmpty(pluginName) || !string.IsNullOrEmpty(extra))
            {
                LogPluginUsage();
                return;
            }

            RunPluginLifecycleCommand(normalizedSubcommand, pluginName);
        }

        void ShowPluginList()
        {
            var context = new KeyboardHandlerContext().WriteLine("Plugins");
            var statuses = PluginManager.SnapshotPluginStatuses();
            if (statuses.Count == 0)
            {
                context.WriteLine("（没有已知插件）", ConsoleColor.DarkGray);
                KeyboardManager.ShowPopup(context);
                return;
            }

            foreach (var status in statuses)
            {
                var state = status.IsLoaded ? "loaded" : status.IsAvailable ? "unloaded" : "failed";
                var displayName = status.DisplayName == status.InternalName ? string.Empty : $" ({status.DisplayName})";
                var version = status.Version is null ? string.Empty : $" v{status.Version}";
                var author = string.IsNullOrWhiteSpace(status.Author) ? string.Empty : $" by {status.Author}";
                var host = status.LoadInHost ? " host" : string.Empty;
                context.WriteLine($"{state} {status.InternalName}{displayName}{version}{author}{host}");
            }

            KeyboardManager.ShowPopup(context);
        }

        void RunPluginLifecycleCommand(string subcommand, string pluginName)
        {
            var status = PluginManager.SnapshotPluginStatuses()
                .FirstOrDefault(x => string.Equals(x.InternalName, pluginName, StringComparison.OrdinalIgnoreCase));
            if (status is null)
            {
                LogCommandWarning($"插件不存在: {pluginName}");
                return;
            }

            var needRestart = subcommand switch
            {
                "load" => PluginManager.LoadPlugins(status.InternalName),
                "unload" => PluginManager.UnloadPlugins(status.InternalName),
                "reload" => PluginManager.ReloadPlugins(status.InternalName),
                _ => throw new InvalidOperationException($"未知 plugin 子命令: {subcommand}")
            };

            if (needRestart.Contains(status.InternalName, StringComparer.OrdinalIgnoreCase))
            {
                LogCommandWarning($"插件 {status.InternalName} 需要重启才能{subcommand}。");
                return;
            }

            var action = subcommand switch
            {
                "load" => "加载",
                "unload" => "卸载",
                "reload" => "重载",
                _ => subcommand
            };
            var context = new KeyboardHandlerContext()
                .WriteLine("Plugin command")
                .WriteLine($"{status.InternalName} 已{action}。", ConsoleColor.Green);
            KeyboardManager.ShowPopup(context);
            LogCommand($"插件 {status.InternalName} 已{action}。", LiveDisplaySeverity.Success);
        }

        void LogPluginUsage()
        {
            LogCommandWarning("用法: /plugin [list] | /plugin load <InternalName> | /plugin unload <InternalName> | /plugin reload <InternalName>");
        }

        void LogCommandWarning(string text)
        {
            LogCommand(text, LiveDisplaySeverity.Warning);
        }

        void LogCommand(string text, LiveDisplaySeverity severity)
        {
            AddLog(new LiveDisplayLogLine(null, "Command", text, severity));
        }

        static (string Command, string Remainder) SplitCommand(string value)
        {
            value = value.Trim();
            if (value.Length == 0)
                return (string.Empty, string.Empty);

            var index = value.IndexOf(' ');
            return index < 0
                ? (value, string.Empty)
                : (value[..index], value[(index + 1)..].Trim());
        }

        static string ParseWorkspaceTitle(string value)
        {
            if (!value.StartsWith('"'))
                return value;

            var title = new System.Text.StringBuilder(value.Length);
            for (var i = 1; i < value.Length; i++)
            {
                switch (value[i])
                {
                    case '"':
                        if (i != value.Length - 1)
                            throw new FormatException("Quoted workspace title 的结束双引号后不能有其它内容。");
                        if (string.IsNullOrWhiteSpace(title.ToString()))
                            throw new FormatException("Quoted workspace title 不能为空或仅包含空白。");
                        return title.ToString();
                    case '\\':
                        if (++i >= value.Length)
                            throw new FormatException("Quoted workspace title 不能以反斜杠结尾。");
                        if (value[i] is not ('"' or '\\'))
                            throw new FormatException($"Quoted workspace title 不支持转义 \\{value[i]}；仅支持 \\\" 与 \\\\。");
                        title.Append(value[i]);
                        break;
                    default:
                        title.Append(value[i]);
                        break;
                }
            }

            throw new FormatException("Quoted workspace title 缺少结束双引号。");
        }

        static Channel<T> CreateUiChannel<T>()
        {
            return Channel.CreateUnbounded<T>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        bool RemoveExpiredNotifications(DateTimeOffset now)
        {
            var expired = notifications.Where(x => x.ExpiresAt <= now).ToArray();
            foreach (var notification in expired)
                KeyboardManager.UnregisterNotificationShortcuts(notification.ShortcutRegistrationId);
            return notifications.RemoveAll(x => x.ExpiresAt <= now) > 0;
        }

        void AddLog(LiveDisplayLogLine line)
        {
            var bank = line.Workspace is null
                ? globalLogs
                : workspaceLogs.GetValueOrDefault(line.Workspace);
            if (bank is null)
            {
                bank = [];
                workspaceLogs.Add(line.Workspace!, bank);
            }

            bank.Add((++logSequence, line));
            if (bank.Count > MaxLogLines)
                bank.RemoveRange(0, bank.Count - MaxLogLines);
        }

        IReadOnlyList<LiveDisplayLogLine> VisibleLogs()
        {
            IEnumerable<(long Sequence, LiveDisplayLogLine Line)> visible = globalLogs;
            if (activeWorkspace is not null && workspaceLogs.TryGetValue(activeWorkspace, out var scopedLogs))
                visible = visible.Concat(scopedLogs);

            return visible
                .OrderBy(x => x.Sequence)
                .Select(x => x.Line)
                .ToArray();
        }

        List<LiveDisplayNotification> VisibleNotifications()
        {
            return notifications
                .Where(x => x.Workspace is null || ReferenceEquals(x.Workspace, activeWorkspace))
                .OrderByDescending(x => x.ExpiresAt)
                .ToList();
        }

        void RegisterKnownWorkspace(LiveDisplayWorkspace workspace, int historyCapacity)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            if (IsRemovedWorkspace(workspace))
                return;

            TrackWorkspaceRegistration(workspace, historyCapacity);
            if (workspaces.TryGetValue(workspace, out var existing))
            {
                if (!ReferenceEquals(existing.Workspace, workspace))
                    return;
                if (existing.HistoryCapacity != historyCapacity)
                    throw new InvalidOperationException($"Workspace '{workspace.Title}' 的 historyCapacity 不能在注册后更改。");
                if (activeWorkspace is null)
                {
                    activeWorkspace = workspace;
                    SetCurrentWorkspaceIfEmpty(workspace);
                }
                return;
            }

            workspaces[workspace] = new WorkspaceState(
                workspace,
                historyCapacity,
                History: [],
                HistoryOffset: -1,
                ScrollOffset: 0,
                ShortcutText: null,
                Hotkey: null);
            if (activeWorkspace is null)
            {
                activeWorkspace = workspace;
                SetCurrentWorkspaceIfEmpty(workspace);
            }

            RefreshWorkspaceCompletionTitles();
        }

        void SetCurrentWorkspaceIfEmpty(LiveDisplayWorkspace workspace)
        {
            Interlocked.CompareExchange(ref currentWorkspace, workspace, null);
        }

        void SetWorkspaceShortcut(
            LiveDisplayWorkspace workspace,
            ConsoleKey key,
            ConsoleModifiers modifiers,
            string shortcutText,
            KeyboardManager.HotkeyEntry entry)
        {
            if (IsRemovedWorkspace(workspace))
            {
                KeyboardManager.Unregister(key, modifiers, entry);
                return;
            }

            RegisterKnownWorkspace(workspace, HistoryCapacityOf(workspace));
            var state = workspaces[workspace];
            if (state.Hotkey is { } previous)
                KeyboardManager.Unregister(previous.Key, previous.Modifiers, previous.Entry);
            workspaces[workspace] = state with
            {
                ShortcutText = shortcutText,
                Hotkey = new(key, modifiers, entry)
            };
        }

        void ArmWorkspaceRemovalWhenAnotherPanelActivates(LiveDisplayWorkspace workspace, Action? removed)
        {
            if (IsRemovedWorkspace(workspace))
            {
                removed?.Invoke();
                return;
            }

            if (pendingWorkspaceRemovals.Any(x => ReferenceEquals(x.Workspace, workspace)))
                return;

            pendingWorkspaceRemovals.Add(new(workspace, removed));
        }

        void RemovePendingWorkspacesAfterPanelActivation(LiveDisplayWorkspace activatedWorkspace)
        {
            foreach (var pending in pendingWorkspaceRemovals.ToList())
            {
                if (ReferenceEquals(pending.Workspace, activatedWorkspace))
                    continue;

                TryTombstoneWorkspace(pending.Workspace, activatedWorkspace, queueRemoval: false, out var replacement);
                RemoveWorkspaceState(pending.Workspace, replacement, removed: null);
            }
        }

        void RemoveWorkspaceState(LiveDisplayWorkspace workspace, LiveDisplayWorkspace? replacement, Action? removed)
        {
            var callbacks = pendingWorkspaceRemovals
                .Where(x => ReferenceEquals(x.Workspace, workspace))
                .Select(x => x.Removed)
                .Where(x => x is not null)
                .Cast<Action>()
                .ToList();
            pendingWorkspaceRemovals.RemoveAll(x => ReferenceEquals(x.Workspace, workspace));
            if (removed is not null)
                callbacks.Add(removed);

            if (workspaces.TryGetValue(workspace, out var state) && ReferenceEquals(state.Workspace, workspace))
            {
                workspaces.Remove(workspace);
                UnbindWorkspaceHotkey(state);
                foreach (var key in panels.Keys.Where(x => ReferenceEquals(x.Workspace, workspace)).ToList())
                    panels.Remove(key);
                workspaceLogs.Remove(workspace);
                foreach (var notification in notifications.Where(x => ReferenceEquals(x.Workspace, workspace)))
                    KeyboardManager.UnregisterNotificationShortcuts(notification.ShortcutRegistrationId);
                notifications.RemoveAll(x => ReferenceEquals(x.Workspace, workspace));
            }

            if (ReferenceEquals(activeWorkspace, workspace))
                activeWorkspace = replacement is not null && workspaces.ContainsKey(replacement)
                    ? replacement
                    : workspaces.Keys.FirstOrDefault();

            if (ReferenceEquals(CurrentWorkspace, workspace))
                Volatile.Write(ref currentWorkspace, activeWorkspace);

            RefreshWorkspaceCompletionTitles();
            foreach (var callback in callbacks.Distinct())
                callback();
        }

        void CaptureWorkspaceSnapshotState(LiveDisplayWorkspace workspace)
        {
            if (IsRemovedWorkspace(workspace))
                return;

            RegisterKnownWorkspace(workspace, HistoryCapacityOf(workspace));
            if (!workspaces.TryGetValue(workspace, out var state) ||
                !ReferenceEquals(state.Workspace, workspace) ||
                state.HistoryCapacity == 0)
            {
                return;
            }

            state.History.Add(new WorkspaceSnapshot(
                panels.Values.Where(x => ReferenceEquals(x.Workspace, workspace)).ToArray()));
            var historyOffset = state.HistoryOffset;
            if (state.History.Count > state.HistoryCapacity)
            {
                state.History.RemoveAt(0);
                if (historyOffset >= 0)
                    historyOffset = Math.Max(0, historyOffset - 1);
            }
            workspaces[workspace] = state with
            {
                HistoryOffset = historyOffset
            };
        }

        bool IsBrowsingHistory()
        {
            return activeWorkspace is not null &&
                workspaces.TryGetValue(activeWorkspace, out var state) &&
                state.HistoryOffset >= 0;
        }

        void RefreshWorkspaceCompletionTitles()
        {
            workspaceCompletionTitles = workspaces.Keys
                .Select(x => x.Title)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        IReadOnlyList<string> CompleteWorkspaceCommand(string rest)
        {
            var subcommandSpaceIndex = rest.IndexOf(' ');
            if (subcommandSpaceIndex < 0)
                return CompleteByPrefix($"/workspace {rest}", ["/workspace list", "/workspace switch"]);

            var subcommand = rest[..subcommandSpaceIndex];
            var arguments = rest[(subcommandSpaceIndex + 1)..];
            return string.Equals(subcommand, "switch", StringComparison.OrdinalIgnoreCase)
                ? CompleteWorkspaceTitles(arguments)
                : [];
        }

        IReadOnlyList<string> CompleteWorkspaceTitles(string arguments)
        {
            var prefix = WorkspaceCompletionPrefix(arguments);
            return Volatile.Read(ref workspaceCompletionTitles)
                .Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(x => $"/workspace switch {QuoteWorkspaceTitleIfNeeded(x)}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        static string WorkspaceCompletionPrefix(string value)
        {
            if (!value.StartsWith('"'))
                return value;

            var prefix = new System.Text.StringBuilder(value.Length);
            for (var i = 1; i < value.Length; i++)
            {
                if (value[i] == '"')
                    break;

                if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is '"' or '\\')
                    i++;

                prefix.Append(value[i]);
            }
            return prefix.ToString();
        }

        static string QuoteWorkspaceTitleIfNeeded(string title)
        {
            if (!title.Any(char.IsWhiteSpace) && !title.Contains('"') && !title.Contains('\\'))
                return title;

            return $"\"{title.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        static IReadOnlyList<string> CompletePluginCommand(string rest)
        {
            var subcommandSpaceIndex = rest.IndexOf(' ');
            if (subcommandSpaceIndex < 0)
                return CompleteByPrefix($"/plugin {rest}", [
                    "/plugin list",
                    "/plugin load",
                    "/plugin unload",
                    "/plugin reload"
                ]);

            var subcommand = rest[..subcommandSpaceIndex];
            if (!subcommand.Equals("load", StringComparison.OrdinalIgnoreCase) &&
                !subcommand.Equals("unload", StringComparison.OrdinalIgnoreCase) &&
                !subcommand.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var arguments = rest[(subcommandSpaceIndex + 1)..];
            return CompleteByPrefix(
                $"/plugin {subcommand} {arguments}",
                PluginManager.SnapshotPluginStatuses().Select(x => $"/plugin {subcommand} {x.InternalName}"));
        }

        static IReadOnlyList<string> CompleteByPrefix(string prefix, IEnumerable<string> candidates)
        {
            return candidates
                .Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        bool IsRemovedWorkspace(LiveDisplayWorkspace workspace)
        {
            lock (removedWorkspaceGate)
                return removedWorkspaces.Contains(workspace);
        }

        bool TryTombstoneWorkspace(
            LiveDisplayWorkspace workspace,
            LiveDisplayWorkspace? preferredReplacement,
            bool queueRemoval,
            out LiveDisplayWorkspace? replacement)
        {
            lock (workspaceIdentityGate)
            {
                lock (removedWorkspaceGate)
                {
                    if (!removedWorkspaces.Add(workspace))
                    {
                        replacement = null;
                        return false;
                    }
                }

                if (workspaceRegistrations.TryGetValue(workspace.Title, out var existing) &&
                    ReferenceEquals(existing.Workspace, workspace))
                {
                    workspaceRegistrations.Remove(workspace.Title);
                }

                replacement = preferredReplacement is not null &&
                    workspaceRegistrations.Values.Any(x => ReferenceEquals(x.Workspace, preferredReplacement))
                        ? preferredReplacement
                        : workspaceRegistrations.Values.Select(x => x.Workspace).FirstOrDefault();

                if (queueRemoval)
                    Post(new UiEvent.RemoveWorkspace(workspace, replacement));
                if (ReferenceEquals(CurrentWorkspace, workspace))
                    Volatile.Write(ref currentWorkspace, replacement);
            }
            KeyboardManager.RemoveNotificationShortcuts(workspace);
            return true;
        }

        void UnbindWorkspaceHotkey(WorkspaceState state)
        {
            if (state.Hotkey is not { } hotkey)
                return;

            KeyboardManager.Unregister(hotkey.Key, hotkey.Modifiers, hotkey.Entry);
        }

        void TrackWorkspaceRegistration(LiveDisplayWorkspace workspace, int historyCapacity)
        {
            lock (workspaceIdentityGate)
            {
                if (workspaceRegistrations.TryGetValue(workspace.Title, out var existing))
                {
                    if (ReferenceEquals(existing.Workspace, workspace) && existing.HistoryCapacity != historyCapacity)
                        throw new InvalidOperationException($"Workspace '{workspace.Title}' 的 historyCapacity 不能在注册后更改。");
                    return;
                }

                workspaceRegistrations.Add(workspace.Title, new(workspace, historyCapacity));
            }
        }

        int HistoryCapacityOf(LiveDisplayWorkspace workspace)
        {
            lock (workspaceIdentityGate)
            {
                return workspaceRegistrations.TryGetValue(workspace.Title, out var registration) &&
                    ReferenceEquals(registration.Workspace, workspace)
                        ? registration.HistoryCapacity
                        : 0;
            }
        }

        static int GetConsoleWidth()
        {
            try { return Console.WindowWidth; }
            catch { return 120; }
        }

        static int GetConsoleHeight()
        {
            try { return Console.WindowHeight; }
            catch { return 35; }
        }

        static bool HasInteractiveConsole()
        {
            if (HasInteractiveConsoleOverrideForTests is { } overrideValue)
                return overrideValue;

            if (Console.IsOutputRedirected)
                return false;

            try
            {
                _ = Console.WindowWidth;
                _ = Console.WindowHeight;
                return true;
            }
            catch
            {
                return false;
            }
        }

        sealed record PendingWorkspaceRemoval(LiveDisplayWorkspace Workspace, Action? Removed);

        sealed record WorkspaceRegistration(LiveDisplayWorkspace Workspace, int HistoryCapacity);

        sealed record WorkspaceSnapshot(IReadOnlyList<LiveDisplayPanel> Panels);

        sealed record WorkspaceHotkey(
            ConsoleKey Key,
            ConsoleModifiers Modifiers,
            KeyboardManager.HotkeyEntry Entry);

        sealed record WorkspaceState(
            LiveDisplayWorkspace Workspace,
            int HistoryCapacity,
            List<WorkspaceSnapshot> History,
            int HistoryOffset,
            int ScrollOffset,
            string? ShortcutText,
            WorkspaceHotkey? Hotkey);

        sealed record WorkspacePopupEntry(int LineIndex, LiveDisplayWorkspace Workspace);

        sealed record WorkspacePopupList(KeyboardHandlerContext Context, List<WorkspacePopupEntry> Entries);

        sealed class ConsoleInteractionRequest(Func<Task> action)
        {
            readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Func<Task> Action => action;
            public Task Completion => completion.Task;

            public void SetResult()
            {
                completion.TrySetResult();
            }

            public void SetException(Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }
}
