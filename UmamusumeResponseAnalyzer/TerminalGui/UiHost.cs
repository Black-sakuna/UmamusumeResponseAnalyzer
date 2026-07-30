using System.Threading.Channels;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Plugin;

namespace UmamusumeResponseAnalyzer.TerminalGui
{
    internal sealed class UiHost : IUiInputSink
    {
        const int MaxLogLines = 300;
        const int RunStateRunning = 1;
        const int RunStateStopped = 2;

        [Flags]
        enum UiChange
        {
            None = 0,
            WorkspaceStructure = 1,
            WorkspaceViewport = 2,
            Overlays = 4
        }

        readonly IApplication application;
        readonly Func<IReadOnlyList<string>> loadWorkspaceTaskbarOrder;
        readonly Action<IReadOnlyList<string>> saveWorkspaceTaskbarOrder;
        readonly Channel<UiEvent> events = CreateUiChannel<UiEvent>();
        readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly NotificationPopupFormatter popupFormatter = new();

        readonly Dictionary<Workspace, WorkspaceState> workspaces = [];
        readonly Dictionary<(Workspace Workspace, string PluginId, string Key), WorkspacePanel> panels = [];
        readonly object workspaceIdentityGate = new();
        readonly Dictionary<string, Workspace> workspaceRegistrations = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<Workspace, Workspace> workspaceAliases =
            new(ReferenceEqualityComparer.Instance);
        readonly object removedWorkspaceGate = new();
        readonly HashSet<Workspace> removedWorkspaces = new(ReferenceEqualityComparer.Instance);
        readonly List<UiLogLine> globalLogs = [];
        readonly Dictionary<Workspace, List<UiLogLine>> workspaceLogs =
            new(ReferenceEqualityComparer.Instance);
        readonly List<UiNotification> notifications = [];
        readonly Dictionary<(Workspace Workspace, string PluginId, string Key), CachedPanelView> panelViews = [];
        readonly object inputTasksGate = new();
        readonly HashSet<Task> inputTasks = [];

        Workspace? currentWorkspace;
        Workspace? activeWorkspace;
        string[] workspaceCompletionTitles = [];
        HotkeyPopup? hotkeyPopup;
        int hotkeyPopupGeneration;
        int lastViewportWidth;
        int lastViewportHeight;
        int lastViewportMaxScroll;
        int popupVisibleLineCount = 1;
        bool shutdownRequested;
        int runState;
        int drainScheduled;
        Window? window;
        View? workspaceLayer;
        WorkspaceTaskbarView? workspaceTaskbarLayer;
        Label? notificationLayer;
        Label? hotkeyLayer;
        CommandModeView? commandMode;
        WorkspaceLayoutBuilder.WorkspaceSurface? workspaceSurface;
        object? popupTimer;
        int acceptingEvents = 1;
        bool workspaceStructurePending;

        public UiHost(
            IApplication application,
            Func<IReadOnlyList<string>> loadWorkspaceTaskbarOrder,
            Action<IReadOnlyList<string>> saveWorkspaceTaskbarOrder)
        {
            this.application = application ?? throw new ArgumentNullException(nameof(application));
            this.loadWorkspaceTaskbarOrder = loadWorkspaceTaskbarOrder;
            this.saveWorkspaceTaskbarOrder = saveWorkspaceTaskbarOrder;
        }

        internal event Action<UiLogLine>? LogAdded;

        public IWorkspaceOutput ForPlugin(string pluginId) => new PluginWorkspaceOutput(pluginId, this);
        public Workspace? CurrentWorkspace => Volatile.Read(ref currentWorkspace);
        internal Task Ready => ready.Task;
        internal IReadOnlyList<UiLogLine> GetLogsForTests(Workspace? workspace)
        {
            var source = workspace is null
                ? globalLogs
                : workspaceLogs.GetValueOrDefault(workspace) ?? [];
            return source.ToArray();
        }

        internal IReadOnlyList<UiNotification> GetNotificationsForTests(Workspace? workspace)
        {
            return notifications
                .Where(x => ReferenceEquals(x.Workspace, workspace))
                .ToArray();
        }

        internal HotkeyPopup? GetHotkeyPopupForTests()
        {
            DrainEvents();
            return hotkeyPopup;
        }

        internal bool ShouldRefreshPopupCountdownForTests(DateTimeOffset now)
        {
            return popupFormatter.ShouldRefreshPopupCountdown(VisibleNotifications(), hotkeyPopup, now);
        }

        public Workspace CreateWorkspace(string title)
        {
            var workspace = Workspace.Create(title);
            lock (workspaceIdentityGate)
            {
                if (workspaceRegistrations.TryGetValue(workspace.Title, out var existing))
                    return existing;

                workspaceRegistrations[workspace.Title] = workspace;
                workspaceAliases[workspace] = workspace;
                SetCurrentWorkspaceIfEmpty(workspace);
            }

            Post(new UiEvent.RegisterWorkspace(workspace));
            return workspace;
        }

        public void RegisterWorkspace(Workspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            workspace = CanonicalizeWorkspace(workspace) ?? workspace;
            if (IsRemovedWorkspace(workspace))
                return;

            SetCurrentWorkspaceIfEmpty(workspace);
            Post(new UiEvent.RegisterWorkspace(workspace));
        }

        public void RemoveWorkspace(Workspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            workspace = CanonicalizeWorkspace(workspace, createIfMissing: false) ?? workspace;
            if (!TryTombstoneWorkspace(workspace, out var replacement))
                return;

            Post(new UiEvent.RemoveWorkspace(workspace, replacement));
            HotkeyManager.RemoveNotificationShortcuts(workspace);
        }

        public void SetPanel(WorkspacePanel panel, bool switchToWorkspace = true)
        {
            var workspace = CanonicalizeWorkspace(panel.Workspace);
            if (workspace is not null)
                Post(new UiEvent.SetPanel(panel with { Workspace = workspace }, switchToWorkspace));
        }

        public void Log(UiLogLine line)
        {
            if (line.Workspace is null)
            {
                Post(new UiEvent.Log(line));
                return;
            }

            var workspace = CanonicalizeWorkspace(line.Workspace);
            if (workspace is not null)
                Post(new UiEvent.Log(line with { Workspace = workspace }));
        }

        public void Notify(UiNotification notification)
        {
            if (notification.Workspace is not null)
            {
                var workspace = CanonicalizeWorkspace(notification.Workspace);
                if (workspace is null)
                    return;
                notification = notification with { Workspace = workspace };
            }

            var registrationId = HotkeyManager.RegisterNotificationShortcuts(
                notification.Workspace,
                notification.ExpiresAt,
                notification.Shortcuts);
            if (notification.Workspace is not null && IsRemovedWorkspace(notification.Workspace))
            {
                HotkeyManager.UnregisterNotificationShortcuts(registrationId);
                return;
            }

            if (!Post(new UiEvent.Notify(notification with
            {
                Shortcuts = [],
                ShortcutRegistrationId = registrationId
            })))
            {
                HotkeyManager.UnregisterNotificationShortcuts(registrationId);
            }
        }
        public void SwitchWorkspace(Workspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            workspace = CanonicalizeWorkspace(workspace) ?? workspace;
            if (IsRemovedWorkspace(workspace))
                return;

            Volatile.Write(ref currentWorkspace, workspace);
            Post(new UiEvent.SwitchWorkspace(workspace));
        }
        public void BindWorkspaceHotkey(
            Workspace workspace,
            ConsoleKey key,
            ConsoleModifiers modifiers = 0,
            string? description = null)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            workspace = CanonicalizeWorkspace(workspace) ?? workspace;
            if (IsRemovedWorkspace(workspace))
                return;

            var shortcutText = HotkeyManager.FormatKeyCombo(key, modifiers);
            var entry = HotkeyManager.RegisterTracked(
                key,
                modifiers,
                description ?? $"切换到 {workspace.Title}",
                () =>
                {
                    SwitchWorkspace(workspace);
                    return Task.CompletedTask;
            });
            RegisterWorkspace(workspace);
            Post(new UiEvent.SetWorkspaceShortcut(workspace, key, modifiers, shortcutText, entry));
        }

        public void RequestShutdown() => Post(new UiEvent.Shutdown());
        internal async Task HandleCommandAsync(string command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!command.StartsWith('/'))
                return;

            try
            {
                await RunCommandAsync(command);
                RebuildWorkspaceLayer();
                RefreshOverlayLayers();
            }
            catch (Exception ex)
            {
                TerminalUi.LogException("Command", ex);
            }
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

        void IUiInputSink.ShowPopup(HotkeyPopup popup, int generation) => Post(new UiEvent.ShowPopup(popup, generation));
        void IUiInputSink.HidePopup(int generation) => Post(new UiEvent.HidePopup(generation));
        int IUiInputSink.PopupVisibleLineCount => Volatile.Read(ref popupVisibleLineCount);
        Task<bool> IUiInputSink.TryHandleWorkspaceCommandAsync(Command command)
        {
            if (command is not (
                Command.Up or Command.Down or Command.PageUp or Command.PageDown or
                Command.Start or Command.End))
            {
                return Task.FromResult(false);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Post(new UiEvent.NavigateWorkspace(command, completion)))
                completion.TrySetResult(false);
            else if (!IsRunning)
                DrainEvents();

            return completion.Task;
        }
        internal bool IsRunning => Volatile.Read(ref runState) == RunStateRunning;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref runState, RunStateRunning, 0) != 0)
                throw new InvalidOperationException("UiHost.RunAsync 已在运行中或已停止。");

            DrainEvents();
            if (shutdownRequested)
            {
                CompleteRun();
                return;
            }

            window = new MainWindow(() =>
            {
                RequestShutdown();
                application.RequestStop();
            })
            {
                Title = "UmamusumeResponseAnalyzer",
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                BorderStyle = null
            };
            workspaceLayer = CreateLayer(transparent: false, canFocus: true);
            notificationLayer = CreateOverlayLabel();
            hotkeyLayer = CreateOverlayLabel();
            commandMode = new(
                command => TrackInputTask(HandleCommandAsync(command)),
                CompleteCommand);
            workspaceTaskbarLayer = new(
                () => commandMode is { IsOpen: true },
                SwitchWorkspace,
                loadWorkspaceTaskbarOrder(),
                SaveWorkspaceTaskbarOrder);
            workspaceLayer.Add(workspaceTaskbarLayer.BottomEdgeTrigger);
            window.Add(workspaceLayer, workspaceTaskbarLayer, notificationLayer, hotkeyLayer, commandMode);
            window.Initialized += WindowInitialized;
            window.ViewportChanged += WindowViewportChanged;
            window.KeyDownNotHandled += WindowKeyDownNotHandled;
            window.MouseEvent += WindowMouseEvent;
            commandMode.VisibleChanged += CommandModeVisibleChanged;
            application.Keyboard.KeyDown += ApplicationKeyDown;
            application.LayoutAndDrawComplete += ApplicationLayoutAndDrawComplete;

            popupTimer = application.AddTimeout(TimeSpan.FromMilliseconds(250), RefreshExpiringOverlays);
            try
            {
                await application.RunAsync(window, cancellationToken);
            }
            finally
            {
                if (popupTimer is not null)
                {
                    application.RemoveTimeout(popupTimer);
                    popupTimer = null;
                }

                application.LayoutAndDrawComplete -= ApplicationLayoutAndDrawComplete;
                application.Keyboard.KeyDown -= ApplicationKeyDown;
                commandMode.VisibleChanged -= CommandModeVisibleChanged;
                window.MouseEvent -= WindowMouseEvent;
                window.KeyDownNotHandled -= WindowKeyDownNotHandled;
                window.ViewportChanged -= WindowViewportChanged;
                window.Initialized -= WindowInitialized;
                Volatile.Write(ref acceptingEvents, 0);
                Task[] pendingInputTasks;
                lock (inputTasksGate)
                    pendingInputTasks = inputTasks.ToArray();
                if (pendingInputTasks.Length > 0)
                {
                    var completion = Task.WhenAll(pendingInputTasks);
                    await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMilliseconds(250)));
                    if (!completion.IsCompleted)
                    {
                        _ = completion.ContinueWith(
                            task => TerminalUi.LogException("URA", task.Exception!),
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
                CompleteRun();
                window.Dispose();
                window = null;
                workspaceLayer = null;
                workspaceTaskbarLayer = null;
                notificationLayer = null;
                hotkeyLayer = null;
                commandMode = null;
                workspaceSurface = null;
            }
        }

        static View CreateLayer(bool transparent, bool canFocus = false) => new()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = canFocus,
            TabStop = canFocus ? TabBehavior.TabGroup : TabBehavior.NoStop,
            ViewportSettings = transparent
                ? ViewportSettingsFlags.Transparent | ViewportSettingsFlags.TransparentMouse
                : ViewportSettingsFlags.None
        };

        static Label CreateOverlayLabel() => new()
        {
            CanFocus = false,
            Enabled = false,
            Visible = false,
            ViewportSettings = ViewportSettingsFlags.Transparent | ViewportSettingsFlags.TransparentMouse
        };

        void WindowInitialized(object? sender, EventArgs e)
        {
            RebuildWorkspaceLayer();
            RefreshOverlayLayers();
        }

        void ApplicationLayoutAndDrawComplete(object? sender, EventArgs e)
        {
            if (window is null || !ReferenceEquals(application.TopRunnableView, window))
                return;

            application.LayoutAndDrawComplete -= ApplicationLayoutAndDrawComplete;
            ready.TrySetResult();
        }

        void WindowViewportChanged(object? sender, DrawEventArgs e)
        {
            RebuildWorkspaceLayer();
            RefreshOverlayLayers();
        }

        void CommandModeVisibleChanged(object? sender, EventArgs e)
            => workspaceTaskbarLayer?.CommandModeVisibilityChanged();

        void SaveWorkspaceTaskbarOrder(IReadOnlyList<string> titles)
        {
            try
            {
                saveWorkspaceTaskbarOrder(titles);
            }
            catch (Exception ex)
            {
                TerminalUi.LogException("URA", ex);
            }
        }

        void ApplicationKeyDown(object? sender, Key key)
        {
            if (key.Handled)
                return;
            if (key.KeyCode == Key.C.WithCtrl.KeyCode)
                return;

            if (window is null ||
                !ReferenceEquals(application.TopRunnableView, window) ||
                !HotkeyManager.HasPriorityPopup)
            {
                return;
            }

            key.Handled = true;
            TrackInputTask(DispatchKeyAsync(key));
        }

        void WindowKeyDownNotHandled(object? sender, Key key)
        {
            if (key.KeyCode == Key.C.WithCtrl.KeyCode)
                return;
            if (key.KeyCode == Key.Tab.KeyCode ||
                key.KeyCode == Key.Tab.WithShift.KeyCode ||
                key.KeyCode == Key.F6.KeyCode ||
                key.KeyCode == Key.F6.WithShift.KeyCode)
            {
                return;
            }

            if (commandMode is { IsOpen: true })
            {
                key.Handled = true;
                return;
            }

            var opensCommandMode = commandMode is not null &&
                ((!key.IsCtrl && !key.IsAlt && key.TryGetPrintableRune(out var rune) && rune.Value == '/') ||
                 key.KeyCode == Key.Enter.KeyCode);
            var inputKey = new Key(key);
            key.Handled = true;
            TrackInputTask(opensCommandMode
                ? DispatchKeyOrOpenCommandAsync(
                    inputKey,
                    inputKey.KeyCode == Key.Enter.KeyCode ? string.Empty : "/")
                : DispatchKeyAsync(inputKey));
        }

        void WindowMouseEvent(object? sender, Mouse mouse)
        {
            workspaceTaskbarLayer?.HandleMousePosition(mouse);
            var flags = mouse.Flags;
            var verticalDelta = flags.HasFlag(MouseFlags.WheeledUp)
                ? 1
                : flags.HasFlag(MouseFlags.WheeledDown) ? -1 : 0;
            var horizontalDelta = flags.HasFlag(MouseFlags.WheeledLeft)
                ? 1
                : flags.HasFlag(MouseFlags.WheeledRight) ? -1 : 0;
            if (verticalDelta == 0 && horizontalDelta == 0)
                return;
            if (commandMode is { IsOpen: true })
            {
                mouse.Handled = true;
                return;
            }

            var hasModifiers =
                flags.HasFlag(MouseFlags.Shift) ||
                flags.HasFlag(MouseFlags.Ctrl) ||
                flags.HasFlag(MouseFlags.Alt);
            mouse.Handled = true;
            TrackInputTask(DispatchMouseWheelAsync(
                horizontalDelta != 0 ? horizontalDelta : verticalDelta,
                hasModifiers,
                horizontalDelta != 0));
        }

        void TrackInputTask(Task task)
        {
            lock (inputTasksGate)
                inputTasks.Add(task);
            _ = task.ContinueWith(
                completed =>
                {
                    lock (inputTasksGate)
                        inputTasks.Remove(completed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        async Task DispatchKeyOrOpenCommandAsync(Key key, string initialText)
        {
            if (await DispatchKeyAsync(key))
                return;

            application.Invoke(() =>
            {
                if (window is not null &&
                    commandMode is not null &&
                    ReferenceEquals(application.TopRunnableView, window))
                {
                    commandMode.Open(initialText, window.MostFocused);
                }
            });
        }

        static async Task<bool> DispatchKeyAsync(Key key)
        {
            try
            {
                return await HotkeyManager.HandleKeyAsync(key);
            }
            catch (Exception ex)
            {
                TerminalUi.LogException("URA", ex);
                return true;
            }
        }

        static async Task DispatchMouseWheelAsync(
            int delta,
            bool hasModifiers,
            bool horizontal)
        {
            try
            {
                await HotkeyManager.HandleMouseWheelAsync(delta, hasModifiers, horizontal);
            }
            catch (Exception ex)
            {
                TerminalUi.LogException("URA", ex);
            }
        }

        bool RefreshExpiringOverlays()
        {
            if (!IsRunning)
                return false;

            var now = DateTimeOffset.Now;
            if (workspaceStructurePending &&
                window is not null &&
                ReferenceEquals(application.TopRunnableView, window))
            {
                workspaceStructurePending = false;
                RebuildWorkspaceLayer();
            }
            if (RemoveExpiredNotifications(now) ||
                popupFormatter.ShouldRefreshPopupCountdown(VisibleNotifications(), hotkeyPopup, now))
            {
                RefreshOverlayLayers();
            }
            return IsRunning && !shutdownRequested;
        }

        void CompleteRun()
        {
            ready.TrySetCanceled();
            Volatile.Write(ref runState, RunStateStopped);
            Volatile.Write(ref acceptingEvents, 0);
            events.Writer.TryComplete();
            ReleasePendingUiEvents();
            DisposePanelViews();
        }

        bool Post(UiEvent uiEvent)
        {
            if (Volatile.Read(ref acceptingEvents) == 0)
                return false;

            if (!events.Writer.TryWrite(uiEvent))
                return false;

            ScheduleDrain();
            return true;
        }

        void ScheduleDrain()
        {
            if (!IsRunning || Interlocked.Exchange(ref drainScheduled, 1) != 0)
                return;

            application.Invoke(DrainPostedEvents);
        }

        void DrainPostedEvents()
        {
            Interlocked.Exchange(ref drainScheduled, 0);
            if (!IsRunning)
                return;

            var changes = DrainEvents();
            if (changes.HasFlag(UiChange.WorkspaceStructure))
            {
                if (window is not null && ReferenceEquals(application.TopRunnableView, window))
                {
                    workspaceStructurePending = false;
                    RebuildWorkspaceLayer();
                }
                else
                {
                    workspaceStructurePending = true;
                }
            }
            else if (changes.HasFlag(UiChange.WorkspaceViewport) && !workspaceStructurePending)
            {
                RefreshWorkspaceSurface();
            }

            if (changes.HasFlag(UiChange.Overlays))
                RefreshOverlayLayers();

            if (shutdownRequested)
            {
                if (application.TopRunnableView is not null)
                    application.RequestStop();
                if (window is not null)
                    application.RequestStop(window);
                return;
            }

            if (events.Reader.TryPeek(out _))
                ScheduleDrain();
        }

        void ReleasePendingUiEvents()
        {
            foreach (var notification in notifications)
                HotkeyManager.UnregisterNotificationShortcuts(notification.ShortcutRegistrationId);
            while (events.Reader.TryRead(out var uiEvent))
            {
                switch (uiEvent)
                {
                    case UiEvent.Notify notify:
                        HotkeyManager.UnregisterNotificationShortcuts(notify.Notification.ShortcutRegistrationId);
                        break;
                    case UiEvent.NavigateWorkspace navigation:
                        navigation.Completion.TrySetResult(false);
                        break;
                }
            }
        }

        UiChange DrainEvents()
        {
            var changes = UiChange.None;
            while (events.Reader.TryRead(out var uiEvent))
            {
                var previousActiveWorkspace = activeWorkspace;
                var previousWorkspaceCount = workspaces.Count;
                Apply(uiEvent);
                changes |= uiEvent switch
                {
                    UiEvent.RegisterWorkspace or
                    UiEvent.RemoveWorkspace or
                    UiEvent.SetWorkspaceShortcut or
                    UiEvent.SwitchWorkspace or
                    UiEvent.RunCommand => UiChange.WorkspaceStructure | UiChange.Overlays,
                    UiEvent.SetPanel setPanel when
                        ReferenceEquals(setPanel.Panel.Workspace, activeWorkspace) ||
                        workspaces.Count != previousWorkspaceCount
                        => UiChange.WorkspaceStructure | UiChange.Overlays,
                    UiEvent.Notify or
                    UiEvent.ShowPopup or
                    UiEvent.HidePopup => UiChange.Overlays,
                    UiEvent.NavigateWorkspace => UiChange.WorkspaceViewport,
                    _ => UiChange.None
                };
                if (!ReferenceEquals(previousActiveWorkspace, activeWorkspace))
                    changes |= UiChange.WorkspaceStructure | UiChange.Overlays;
            }
            return changes;
        }

        void Apply(UiEvent uiEvent)
        {
            switch (uiEvent)
            {
                case UiEvent.RegisterWorkspace registerWorkspace:
                    RegisterKnownWorkspace(registerWorkspace.Workspace);
                    break;
                case UiEvent.RemoveWorkspace removeWorkspace:
                    RemoveWorkspaceState(
                        removeWorkspace.Workspace,
                        removeWorkspace.Replacement);
                    break;
                case UiEvent.SetWorkspaceShortcut setWorkspaceShortcut:
                    SetWorkspaceShortcut(
                        setWorkspaceShortcut.Workspace,
                        setWorkspaceShortcut.Key,
                        setWorkspaceShortcut.Modifiers,
                        setWorkspaceShortcut.ShortcutText,
                        setWorkspaceShortcut.Entry);
                    break;
                case UiEvent.SetPanel setPanel:
                    if (IsRemovedWorkspace(setPanel.Panel.Workspace))
                        break;

                    RegisterKnownWorkspace(setPanel.Panel.Workspace);
                    var panelKey = (setPanel.Panel.Workspace, setPanel.Panel.PluginId, setPanel.Panel.Key);
                    if (panels.TryGetValue(panelKey, out var previousPanel) &&
                        !ReferenceEquals(previousPanel.Content, setPanel.Panel.Content) &&
                        panelViews.Remove(panelKey, out var previousView))
                    {
                        previousView.View.SuperView?.Remove(previousView.View);
                        previousView.View.Dispose();
                    }
                    panels[panelKey] = setPanel.Panel;
                    if (setPanel.SwitchToWorkspace)
                    {
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
                        RegisterKnownWorkspace(log.Line.Workspace);
                    AddLog(log.Line);
                    LogAdded?.Invoke(log.Line);
                    break;
                case UiEvent.Notify notify:
                    if (notify.Notification.Workspace is not null && IsRemovedWorkspace(notify.Notification.Workspace))
                    {
                        HotkeyManager.UnregisterNotificationShortcuts(notify.Notification.ShortcutRegistrationId);
                        break;
                    }

                    if (notify.Notification.Workspace is not null)
                        RegisterKnownWorkspace(notify.Notification.Workspace);
                    notifications.Add(notify.Notification);
                    break;
                case UiEvent.SwitchWorkspace switchWorkspace:
                    if (IsRemovedWorkspace(switchWorkspace.Workspace))
                        break;

                    RegisterKnownWorkspace(switchWorkspace.Workspace);
                    activeWorkspace = switchWorkspace.Workspace;
                    Volatile.Write(ref currentWorkspace, switchWorkspace.Workspace);
                    break;
                case UiEvent.NavigateWorkspace navigateWorkspace:
                    navigateWorkspace.Completion.TrySetResult(TryNavigateWorkspace(navigateWorkspace.Command));
                    break;
                case UiEvent.RunCommand runCommand:
                    TrackInputTask(HandleCommandAsync(runCommand.Command));
                    break;
                case UiEvent.ShowPopup showPopup:
                    if (showPopup.Generation >= hotkeyPopupGeneration)
                    {
                        hotkeyPopup = showPopup.Popup;
                        hotkeyPopupGeneration = showPopup.Generation;
                    }
                    break;
                case UiEvent.HidePopup hidePopup:
                    if (hidePopup.Generation >= hotkeyPopupGeneration)
                    {
                        hotkeyPopup = null;
                        hotkeyPopupGeneration = hidePopup.Generation;
                    }
                    break;
                case UiEvent.Shutdown:
                    shutdownRequested = true;
                    break;
            }
        }

        void RebuildWorkspaceLayer()
        {
            if (window is null || workspaceLayer is null)
                return;

            var width = window.Viewport.Width;
            var height = window.Viewport.Height;
            if (width <= 0 || height <= 0)
                return;
            Volatile.Write(ref popupVisibleLineCount, Math.Max(1, height - 2));

            var focused = ReferenceEquals(application.TopRunnableView, window)
                ? window.MostFocused
                : null;
            DetachCachedPanelViews();
            if (workspaceSurface is not null)
            {
                workspaceLayer.Remove(workspaceSurface.View);
                workspaceSurface.View.Dispose();
                workspaceSurface = null;
            }

            var state = activeWorkspace is not null && workspaces.TryGetValue(activeWorkspace, out var activeState)
                ? activeState
                : null;
            workspaceSurface = WorkspaceLayoutBuilder.BuildWorkspaceLayout(
                activeWorkspace,
                panels.Values,
                WorkspaceLabel,
                width,
                height,
                state?.ScrollOffset ?? 0,
                GetPanelView);
            workspaceLayer.Add(workspaceSurface.View);
            lastViewportWidth = width;
            lastViewportHeight = height;
            lastViewportMaxScroll = workspaceSurface.MaxScroll;
            if (activeWorkspace is { } workspace && state is not null && state.ScrollOffset > workspaceSurface.MaxScroll)
                workspaces[workspace] = state with { ScrollOffset = workspaceSurface.MaxScroll };
            workspaceLayer.SetNeedsLayout();
            workspaceLayer.SetNeedsDraw();
            workspaceTaskbarLayer?.Refresh(workspaces.Keys, activeWorkspace);
            if (focused is not null && IsAttachedTo(focused, window))
                focused.SetFocus();
        }

        void RefreshWorkspaceSurface()
        {
            if (window is null || workspaceSurface is null)
                return;

            var width = window.Viewport.Width;
            var height = window.Viewport.Height;
            if (width <= 0 || height <= 0)
                return;

            var state = activeWorkspace is not null && workspaces.TryGetValue(activeWorkspace, out var activeState)
                ? activeState
                : null;
            var focused = ReferenceEquals(application.TopRunnableView, window)
                ? window.MostFocused
                : null;
            workspaceSurface.Update(width, height, state?.ScrollOffset ?? 0);
            lastViewportWidth = width;
            lastViewportHeight = height;
            lastViewportMaxScroll = workspaceSurface.MaxScroll;
            if (activeWorkspace is { } workspace && state is not null && state.ScrollOffset > workspaceSurface.MaxScroll)
                workspaces[workspace] = state with { ScrollOffset = workspaceSurface.MaxScroll };
            if (focused is not null && IsAttachedTo(focused, window))
                focused.SetFocus();
        }

        static bool IsAttachedTo(View view, View ancestor)
        {
            for (View? current = view; current is not null; current = current.SuperView)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
            }
            return false;
        }

        void RefreshOverlayLayers()
        {
            if (window is null ||
                notificationLayer is null ||
                hotkeyLayer is null)
            {
                return;
            }

            var width = window.Viewport.Width;
            var height = window.Viewport.Height;
            if (width <= 0 || height <= 0)
                return;

            ResetOverlay(notificationLayer);
            ResetOverlay(hotkeyLayer);

            var popupWidth = NotificationPopupFormatter.GetPopupWidth(width);
            var now = DateTimeOffset.Now;
            if (popupWidth > 0)
            {
                var activeNotifications = VisibleNotifications();
                if (activeNotifications.Count > 0)
                {
                    var lines = popupFormatter.BuildLines(
                        activeNotifications,
                        popupWidth,
                        Math.Max(0, height - 1),
                        now,
                        WorkspaceLabel);
                    notificationLayer.Text = string.Join(Environment.NewLine, lines);
                    notificationLayer.X = Pos.AnchorEnd(popupWidth + 1);
                    notificationLayer.Y = 1;
                    notificationLayer.Width = popupWidth;
                    notificationLayer.Height = Math.Min(lines.Count, Math.Max(1, height - 1));
                    notificationLayer.Visible = true;
                }
            }

            if (hotkeyPopup is not null)
            {
                var lines = hotkeyPopup.Lines
                    .Select((line, index) => hotkeyPopup.Selection?.SelectedLineIndex == index ? $"> {line.Text}" : $"  {line.Text}")
                    .Skip(hotkeyPopup.ScrollOffset)
                    .Take(Math.Max(1, height - 2))
                    .ToArray();
                hotkeyLayer.Text = string.Join(Environment.NewLine, lines);
                hotkeyLayer.X = 0;
                hotkeyLayer.Y = Pos.AnchorEnd(Math.Max(1, lines.Length));
                hotkeyLayer.Width = Dim.Fill();
                hotkeyLayer.Height = Math.Max(1, lines.Length);
                hotkeyLayer.Visible = true;
            }

            notificationLayer.SetNeedsDraw();
            hotkeyLayer.SetNeedsDraw();
        }

        View GetPanelView(WorkspacePanel panel)
        {
            var key = (panel.Workspace, panel.PluginId, panel.Key);
            if (panelViews.TryGetValue(key, out var cached))
            {
                if (ReferenceEquals(cached.Content, panel.Content))
                    return cached.View;

                cached.View.SuperView?.Remove(cached.View);
                cached.View.Dispose();
            }

            var view = panel.Content.CreateView();
            panelViews[key] = new(panel.Content, view);
            return view;
        }

        void DetachCachedPanelViews()
        {
            foreach (var cached in panelViews.Values)
                cached.View.SuperView?.Remove(cached.View);
        }

        static void ResetOverlay(Label overlay)
        {
            overlay.Visible = false;
            overlay.Text = string.Empty;
        }

        void DisposePanelViews(Workspace? workspace = null)
        {
            foreach (var (key, cached) in panelViews
                .Where(x => workspace is null || ReferenceEquals(x.Key.Workspace, workspace))
                .ToArray())
            {
                cached.View.SuperView?.Remove(cached.View);
                cached.View.Dispose();
                panelViews.Remove(key);
            }
        }

        string WorkspaceLabel(Workspace workspace)
        {
            return workspaces.TryGetValue(workspace, out var state) && !string.IsNullOrEmpty(state.ShortcutText)
                ? $"{state.ShortcutText} {workspace.Title}"
                : workspace.Title;
        }

        bool TryNavigateWorkspace(Command command)
        {
            if (activeWorkspace is null || !workspaces.TryGetValue(activeWorkspace, out var state))
                return false;

            if (lastViewportWidth <= 0 || lastViewportHeight <= 0)
                return false;

            var maxScroll = lastViewportMaxScroll;
            var currentOffset = Math.Clamp(state.ScrollOffset, 0, maxScroll);
            var nextOffset = command switch
            {
                Command.Up => Math.Min(maxScroll, currentOffset + 1),
                Command.Down => Math.Max(0, currentOffset - 1),
                Command.PageUp => Math.Min(maxScroll, currentOffset + lastViewportHeight),
                Command.PageDown => Math.Max(0, currentOffset - lastViewportHeight),
                Command.Start => maxScroll,
                Command.End => 0,
                _ => currentOffset
            };
            workspaces[activeWorkspace] = state with { ScrollOffset = nextOffset };
            return nextOffset != currentOffset;
        }

        async Task RunCommandAsync(string command)
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
                await RunPluginCommandAsync(rest);
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
                    HotkeyManager.ShowPopup(BuildWorkspaceList().Context);
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
                HotkeyManager.ShowPopup(list.Context);
                return;
            }

            var selectedIndex = Math.Max(0, list.Entries.FindIndex(x => x.Workspace == activeWorkspace));
            var workspacesByLine = list.Entries.ToDictionary(x => x.LineIndex, x => x.Workspace);
            HotkeyManager.ShowPopup(list.Context.ToPopup(selection: new HotkeyPopupSelection(
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
            var context = new HotkeyContext().AddLine("Workspaces");
            var entries = new List<WorkspacePopupEntry>();
            if (workspaces.Count == 0)
            {
                context.AddLine("（没有已注册 workspace）");
                return new WorkspacePopupList(context, entries);
            }

            foreach (var (workspace, state) in workspaces)
            {
                var lineIndex = context.LineCount;
                var marker = workspace == activeWorkspace ? "*" : " ";
                var shortcut = string.IsNullOrEmpty(state.ShortcutText) ? string.Empty : $" [{state.ShortcutText}]";
                context.AddLine($"{marker} {workspace.Title}{shortcut}");
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

        async Task RunPluginCommandAsync(string arguments)
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

            await RunPluginLifecycleCommandAsync(normalizedSubcommand, pluginName);
        }

        void ShowPluginList()
        {
            var context = new HotkeyContext().AddLine("Plugins");
            var statuses = PluginManager.SnapshotPluginStatuses();
            if (statuses.Count == 0)
            {
                context.AddLine("（没有已知插件）");
                HotkeyManager.ShowPopup(context);
                return;
            }

            foreach (var status in statuses)
            {
                var state = status.IsLoaded ? "loaded" : status.IsAvailable ? "unloaded" : "failed";
                var displayName = status.DisplayName == status.InternalName ? string.Empty : $" ({status.DisplayName})";
                var version = status.Version is null ? string.Empty : $" v{status.Version}";
                var author = string.IsNullOrWhiteSpace(status.Author) ? string.Empty : $" by {status.Author}";
                var host = status.LoadInHost ? " host" : string.Empty;
                context.AddLine($"{state} {status.InternalName}{displayName}{version}{author}{host}");
            }

            HotkeyManager.ShowPopup(context);
        }

        async Task RunPluginLifecycleCommandAsync(string subcommand, string pluginName)
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
                "load" => await PluginManager.LoadPluginsAsync(status.InternalName),
                "unload" => await PluginManager.UnloadPluginsAsync(status.InternalName),
                "reload" => await PluginManager.ReloadPluginsAsync(status.InternalName),
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
            var context = new HotkeyContext()
                .AddLine("Plugin command")
                .AddLine($"{status.InternalName} 已{action}。");
            HotkeyManager.ShowPopup(context);
            LogCommand($"插件 {status.InternalName} 已{action}。", UiSeverity.Success);
        }

        void LogPluginUsage()
        {
            LogCommandWarning("用法: /plugin [list] | /plugin load <InternalName> | /plugin unload <InternalName> | /plugin reload <InternalName>");
        }

        void LogCommandWarning(string text)
        {
            LogCommand(text, UiSeverity.Warning);
        }

        void LogCommand(string text, UiSeverity severity)
        {
            AddLog(new UiLogLine(null, "Command", text, severity));
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
                HotkeyManager.UnregisterNotificationShortcuts(notification.ShortcutRegistrationId);
            return notifications.RemoveAll(x => x.ExpiresAt <= now) > 0;
        }

        void AddLog(UiLogLine line)
        {
            var bank = line.Workspace is null
                ? globalLogs
                : workspaceLogs.GetValueOrDefault(line.Workspace);
            if (bank is null)
            {
                bank = [];
                workspaceLogs.Add(line.Workspace!, bank);
            }

            bank.Add(line);
            if (bank.Count > MaxLogLines)
                bank.RemoveRange(0, bank.Count - MaxLogLines);
        }

        List<UiNotification> VisibleNotifications()
        {
            return notifications
                .Where(x => x.Workspace is null || ReferenceEquals(x.Workspace, activeWorkspace))
                .OrderByDescending(x => x.ExpiresAt)
                .ToList();
        }

        void RegisterKnownWorkspace(Workspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);

            lock (workspaceIdentityGate)
            {
                lock (removedWorkspaceGate)
                {
                    if (removedWorkspaces.Contains(workspace))
                        return;
                }

                if (!workspaceRegistrations.ContainsKey(workspace.Title))
                {
                    workspaceRegistrations.Add(workspace.Title, workspace);
                    workspaceAliases[workspace] = workspace;
                }
            }
            if (workspaces.TryGetValue(workspace, out var existing))
            {
                if (!ReferenceEquals(existing.Workspace, workspace))
                    return;
                if (activeWorkspace is null)
                {
                    activeWorkspace = workspace;
                    SetCurrentWorkspaceIfEmpty(workspace);
                }
                return;
            }

            workspaces[workspace] = new WorkspaceState(
                workspace,
                ScrollOffset: 0,
                ShortcutText: null,
                Hotkey: null);
            if (activeWorkspace is null || ReferenceEquals(CurrentWorkspace, workspace))
            {
                activeWorkspace = workspace;
                SetCurrentWorkspaceIfEmpty(workspace);
            }

            RefreshWorkspaceCompletionTitles();
        }

        void SetCurrentWorkspaceIfEmpty(Workspace workspace)
        {
            Interlocked.CompareExchange(ref currentWorkspace, workspace, null);
        }

        void SetWorkspaceShortcut(
            Workspace workspace,
            ConsoleKey key,
            ConsoleModifiers modifiers,
            string shortcutText,
            HotkeyManager.HotkeyEntry entry)
        {
            if (IsRemovedWorkspace(workspace))
            {
                HotkeyManager.Unregister(key, modifiers, entry);
                return;
            }

            RegisterKnownWorkspace(workspace);
            var state = workspaces[workspace];
            if (state.Hotkey is { } previous)
                HotkeyManager.Unregister(previous.Key, previous.Modifiers, previous.Entry);
            workspaces[workspace] = state with
            {
                ShortcutText = shortcutText,
                Hotkey = new(key, modifiers, entry)
            };
        }

        void RemoveWorkspaceState(Workspace workspace, Workspace? replacement)
        {
            if (workspaces.TryGetValue(workspace, out var state) && ReferenceEquals(state.Workspace, workspace))
            {
                workspaces.Remove(workspace);
                UnbindWorkspaceHotkey(state);
                foreach (var key in panels.Keys.Where(x => ReferenceEquals(x.Workspace, workspace)).ToList())
                    panels.Remove(key);
                DisposePanelViews(workspace);
                workspaceLogs.Remove(workspace);
                foreach (var notification in notifications.Where(x => ReferenceEquals(x.Workspace, workspace)))
                    HotkeyManager.UnregisterNotificationShortcuts(notification.ShortcutRegistrationId);
                notifications.RemoveAll(x => ReferenceEquals(x.Workspace, workspace));
            }

            if (ReferenceEquals(activeWorkspace, workspace))
                activeWorkspace = replacement is not null && workspaces.ContainsKey(replacement)
                    ? replacement
                    : workspaces.Keys.FirstOrDefault();

            if (ReferenceEquals(CurrentWorkspace, workspace))
                Volatile.Write(ref currentWorkspace, activeWorkspace);

            RefreshWorkspaceCompletionTitles();
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

        bool IsRemovedWorkspace(Workspace workspace)
        {
            lock (removedWorkspaceGate)
                return removedWorkspaces.Contains(workspace);
        }

        bool TryTombstoneWorkspace(
            Workspace workspace,
            out Workspace? replacement)
        {
            lock (workspaceIdentityGate)
            {
                var aliases = workspaceAliases
                    .Where(x => ReferenceEquals(x.Value, workspace))
                    .Select(x => x.Key)
                    .ToList();
                if (!aliases.Any(x => ReferenceEquals(x, workspace)))
                    aliases.Add(workspace);
                lock (removedWorkspaceGate)
                {
                    if (removedWorkspaces.Contains(workspace))
                    {
                        replacement = null;
                        return false;
                    }
                    foreach (var alias in aliases)
                        removedWorkspaces.Add(alias);
                }

                foreach (var alias in aliases)
                    workspaceAliases.Remove(alias);
                if (workspaceRegistrations.TryGetValue(workspace.Title, out var existing) &&
                    ReferenceEquals(existing, workspace))
                {
                    workspaceRegistrations.Remove(workspace.Title);
                }

                replacement = workspaceRegistrations.Values.FirstOrDefault();

                if (ReferenceEquals(CurrentWorkspace, workspace))
                    Volatile.Write(ref currentWorkspace, replacement);
            }
            return true;
        }

        void UnbindWorkspaceHotkey(WorkspaceState state)
        {
            if (state.Hotkey is not { } hotkey)
                return;

            HotkeyManager.Unregister(hotkey.Key, hotkey.Modifiers, hotkey.Entry);
        }

        Workspace? CanonicalizeWorkspace(
            Workspace workspace,
            bool createIfMissing = true)
        {
            lock (workspaceIdentityGate)
            {
                lock (removedWorkspaceGate)
                {
                    if (removedWorkspaces.Contains(workspace))
                        return null;
                }

                if (workspaceAliases.TryGetValue(workspace, out var canonical))
                    return canonical;

                if (workspaceRegistrations.TryGetValue(workspace.Title, out var registration))
                {
                    workspaceAliases[workspace] = registration;
                    return registration;
                }

                if (!createIfMissing)
                    return null;

                workspaceRegistrations.Add(workspace.Title, workspace);
                workspaceAliases[workspace] = workspace;
                return workspace;
            }
        }

        sealed record WorkspaceHotkey(
            ConsoleKey Key,
            ConsoleModifiers Modifiers,
            HotkeyManager.HotkeyEntry Entry);

        sealed record WorkspaceState(
            Workspace Workspace,
            int ScrollOffset,
            string? ShortcutText,
            WorkspaceHotkey? Hotkey);

        sealed record WorkspacePopupEntry(int LineIndex, Workspace Workspace);

        sealed record WorkspacePopupList(HotkeyContext Context, List<WorkspacePopupEntry> Entries);

        sealed record CachedPanelView(WorkspaceContent Content, View View);

        sealed class MainWindow : Window
        {
            public MainWindow(Action shutdown)
            {
                KeyBindings.Remove(Key.Enter);
                AddCommand(Command.Quit, () =>
                {
                    shutdown();
                    return true;
                });
            }
        }
    }
}
