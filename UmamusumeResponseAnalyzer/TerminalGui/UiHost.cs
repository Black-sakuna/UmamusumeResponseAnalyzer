using System.Threading.Channels;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace UmamusumeResponseAnalyzer.TerminalGui;

internal sealed class UiHost : IUiInputSink
{
    const int MaxIngressBatch = 256;
    const int MaxLogLines = 300;
    const int StateCreated = 0;
    const int StateRunning = 1;
    const int StateStopping = 2;
    const int StateStopped = 3;

    [Flags]
    enum UiChange
    {
        None = 0,
        Workspace = 1,
        Overlays = 2,
        All = Workspace | Overlays
    }

    readonly object ingressGate = new();
    readonly WorkspaceRegistry registry = new();
    readonly HashSet<(Workspace Workspace, string Key)> admittedPanels = [];
    readonly Channel<AdmittedUiEvent> events = CreateUiChannel<AdmittedUiEvent>();
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<Workspace, WorkspaceState> workspaces =
        new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(Workspace Workspace, string Key), WorkspacePanel> panels = [];
    readonly List<UiLogLine> globalLogs = [];
    readonly Dictionary<Workspace, List<UiLogLine>> workspaceLogs =
        new(ReferenceEqualityComparer.Instance);
    readonly List<UiNotification> notifications = [];
    readonly CancellationTokenRegistration lifetimeRegistration;

    Workspace? activeWorkspace;
    HotkeyPopup? hotkeyPopup;
    int hotkeyPopupGeneration;
    int popupVisibleLineCount = 1;
    int pendingEvents;
    int state;
    long panelSequence;
    bool drainRunning;
    bool drainScheduled;
    bool runClaimed;
    bool shutdownRequested;
    Window? window;
    View? workspaceLayer;
    View? workspaceView;
    Label? notificationLayer;
    Label? hotkeyLayer;
    object? popupTimer;

    internal UiHost(
        IApplication application,
        SynchronizationContext ownerContext,
        CancellationToken lifetimeToken)
    {
        Application = application ?? throw new ArgumentNullException(nameof(application));
        OwnerContext = ownerContext ?? throw new ArgumentNullException(nameof(ownerContext));
        LifetimeToken = lifetimeToken;
        lifetimeRegistration = lifetimeToken.Register(
            static value => ((UiHost)value!).RequestShutdown(),
            this);
    }

    internal IApplication Application { get; }
    internal SynchronizationContext OwnerContext { get; }
    internal CancellationToken LifetimeToken { get; }
    internal Task Ready => ready.Task;
    internal event Action<UiLogLine>? LogAdded;

    internal void EnsureAvailable()
    {
        lock (ingressGate)
            EnsureAvailableLocked();
    }

    internal Workspace? GetCurrentWorkspace()
    {
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            return registry.Current;
        }
    }

    internal Workspace CreateWorkspace(string title)
    {
        bool schedule;
        Workspace workspace;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            (workspace, var created) = registry.Create(title);
            if (!created)
                return workspace;

            schedule = AdmitLocked(new RegisterWorkspaceIngress(workspace));
        }
        ScheduleDrain(schedule);
        return workspace;
    }

    internal void RemoveWorkspace(Workspace workspace)
    {
        bool schedule;
        Workspace? replacement;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            if (!registry.Remove(workspace, out replacement))
                return;

            admittedPanels.RemoveWhere(key => ReferenceEquals(key.Workspace, workspace));
            schedule = AdmitLocked(new RemoveWorkspaceIngress(workspace, replacement));
        }
        ScheduleDrain(schedule);
    }

    internal void SetPanel(
        Workspace workspace,
        string key,
        string title,
        WorkspaceContent content,
        bool fullBleed,
        bool switchToWorkspace)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            registry.EnsureLive(workspace);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(content);
            if (switchToWorkspace)
                registry.SwitchTo(workspace);
            admittedPanels.Add((workspace, key));
            schedule = AdmitLocked(new SetPanelIngress(
                new(
                    workspace,
                    key,
                    title,
                    content,
                    unchecked(++panelSequence),
                    fullBleed),
                switchToWorkspace));
        }
        ScheduleDrain(schedule);
    }

    internal bool RemovePanel(Workspace workspace, string key)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            registry.EnsureLive(workspace);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (!admittedPanels.Remove((workspace, key)))
                return false;

            schedule = AdmitLocked(new RemovePanelIngress(workspace, key));
        }
        ScheduleDrain(schedule);
        return true;
    }

    internal void Log(
        Workspace? workspace,
        string text,
        UiSeverity severity,
        string? exceptionDetails = null)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            if (workspace is not null)
                registry.EnsureLive(workspace);
            ArgumentNullException.ThrowIfNull(text);
            schedule = AdmitLocked(new LogIngress(
                new(workspace, text, severity, exceptionDetails)));
        }
        ScheduleDrain(schedule);
    }

    internal void Notify(
        Workspace? workspace,
        string text,
        UiSeverity severity,
        TimeSpan? ttl,
        UiShortcut[] shortcuts)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            if (workspace is not null)
                registry.EnsureLive(workspace);
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(shortcuts);
            if (shortcuts.Length > 0)
            {
                throw new InvalidOperationException(
                    "Notification shortcuts 的 owner-aware admission seam 尚未接入，当前请求未被接受。");
            }
            var expiresAt = UiNotification.ExpiresAtFromNow(severity, ttl);
            schedule = AdmitLocked(new NotifyIngress(
                workspace,
                text,
                severity,
                expiresAt));
        }
        ScheduleDrain(schedule);
    }

    internal void SwitchWorkspace(Workspace workspace)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            registry.SwitchTo(workspace);
            schedule = AdmitLocked(new SwitchWorkspaceIngress(workspace));
        }
        ScheduleDrain(schedule);
    }

    internal void BindWorkspaceHotkey(
        Workspace workspace,
        ConsoleKey key,
        ConsoleModifiers modifiers,
        string? description)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            registry.EnsureLive(workspace);
            if (modifiers.HasFlag(ConsoleModifiers.Control) &&
                key is ConsoleKey.S or ConsoleKey.Q or ConsoleKey.Z)
            {
                throw new InvalidOperationException($"Ctrl+{key} 由终端保留，不能注册为热键。");
            }
            schedule = AdmitLocked(new BindWorkspaceHotkeyIngress(
                workspace,
                key,
                modifiers,
                description ?? $"切换到 {workspace.Title}"));
        }
        ScheduleDrain(schedule);
    }

    internal Task FlushAsync()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            schedule = AdmitLocked(new FlushIngress(completion));
        }
        ScheduleDrain(schedule);
        return completion.Task;
    }

    internal void RequestShutdown()
    {
        bool schedule;
        lock (ingressGate)
        {
            if (state >= StateStopping)
                return;

            state = StateStopping;
            if (!events.Writer.TryWrite(new(new ShutdownIngress())))
                throw new InvalidOperationException("UiHost shutdown event 无法写入 ingress channel。");
            pendingEvents++;
            events.Writer.TryComplete();
            schedule = ArmDrainLocked();
        }
        ScheduleDrain(schedule);
    }

    internal async Task RunAsync()
    {
        bool schedule;
        lock (ingressGate)
        {
            if (runClaimed || state == StateStopped)
                throw new InvalidOperationException("UiHost.RunAsync 只能调用一次。");

            runClaimed = true;
            if (state == StateCreated)
                state = StateRunning;
            schedule = ArmDrainLocked();
        }

        try
        {
            CreateWindow();
            ScheduleDrain(schedule);
            popupTimer = Application.AddTimeout(
                TimeSpan.FromMilliseconds(250),
                RefreshExpiringOverlays);
            await Application.RunAsync(window!, LifetimeToken);
        }
        finally
        {
            if (popupTimer is not null)
            {
                Application.RemoveTimeout(popupTimer);
                popupTimer = null;
            }
            FinishRun();
            DisposeWindow();
        }
    }

    int IUiInputSink.PopupVisibleLineCount
    {
        get
        {
            EnsureAvailable();
            return Volatile.Read(ref popupVisibleLineCount);
        }
    }

    Task<bool> IUiInputSink.TryHandleWorkspaceCommandAsync(Command command)
    {
        EnsureAvailable();
        return Task.FromResult(false);
    }

    void IUiInputSink.ShowPopup(HotkeyPopup popup, int generation)
    {
        ArgumentNullException.ThrowIfNull(popup);
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            schedule = AdmitLocked(new ShowPopupIngress(popup, generation));
        }
        ScheduleDrain(schedule);
    }

    void IUiInputSink.HidePopup(int generation)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            schedule = AdmitLocked(new HidePopupIngress(generation));
        }
        ScheduleDrain(schedule);
    }

    void EnsureAvailableLocked()
    {
        if (state >= StateStopping || LifetimeToken.IsCancellationRequested)
        {
            var lifecycle = state == StateStopped ? "stopped" : "stopping";
            throw new InvalidOperationException(
                $"UiHost is {lifecycle}; new UI operations are not accepted.");
        }
    }

    bool AdmitLocked(IngressEvent uiEvent)
    {
        if (!events.Writer.TryWrite(new(uiEvent)))
            throw new InvalidOperationException("UiHost ingress channel 已关闭。");
        pendingEvents++;
        return ArmDrainLocked();
    }

    bool ArmDrainLocked()
    {
        if (state is not (StateRunning or StateStopping) ||
            drainRunning ||
            drainScheduled ||
            pendingEvents == 0)
        {
            return false;
        }

        drainScheduled = true;
        return true;
    }

    void ScheduleDrain(bool schedule)
    {
        if (schedule)
            Application.AddTimeout(TimeSpan.Zero, DrainPostedEvents);
    }

    bool DrainPostedEvents()
    {
        lock (ingressGate)
        {
            drainScheduled = false;
            if (drainRunning)
                return false;
            drainRunning = true;
        }

        try
        {
            var batch = DrainBatch();
            if (batch.Count == 0)
                return false;

            try
            {
                try
                {
                    Reconcile(batch.Changes);
                    foreach (var completion in batch.FlushCompletions)
                        completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    foreach (var completion in batch.FlushCompletions)
                        completion.TrySetException(ex);
                    throw;
                }
            }
            finally
            {
                if (batch.Shutdown)
                {
                    shutdownRequested = true;
                    if (window is not null)
                        Application.RequestStop(window);
                    else
                        Application.RequestStop();
                }
            }
        }
        finally
        {
            bool schedule;
            lock (ingressGate)
            {
                drainRunning = false;
                schedule = ArmDrainLocked();
            }
            ScheduleDrain(schedule);
        }
        return false;
    }

    DrainBatchResult DrainBatch()
    {
        var changes = UiChange.None;
        var flushCompletions = new List<TaskCompletionSource>();
        var shutdown = false;
        var admittedEvents = new List<AdmittedUiEvent>(MaxIngressBatch);
        while (admittedEvents.Count < MaxIngressBatch &&
               events.Reader.TryRead(out var admitted))
        {
            admittedEvents.Add(admitted);
            if (admitted.IsFlush)
                break;
        }
        lock (ingressGate)
        {
            pendingEvents -= admittedEvents.Count;
            if (pendingEvents < 0)
                throw new InvalidOperationException("UiHost ingress pending count 失衡。");
        }
        for (var index = 0; index < admittedEvents.Count; index++)
        {
            try
            {
                changes |= Apply(
                    admittedEvents[index].MarkApplied(),
                    flushCompletions,
                    ref shutdown);
            }
            catch (Exception ex)
            {
                foreach (var completion in flushCompletions)
                    completion.TrySetException(ex);
                for (var remaining = index + 1; remaining < admittedEvents.Count; remaining++)
                    Abandon(admittedEvents[remaining].MarkAbandoned());
                throw;
            }
        }
        return new(admittedEvents.Count, changes, flushCompletions, shutdown);
    }

    UiChange Apply(
        IngressEvent uiEvent,
        List<TaskCompletionSource> flushCompletions,
        ref bool shutdown)
    {
        switch (uiEvent)
        {
            case RegisterWorkspaceIngress register:
                RegisterWorkspaceState(register.Workspace);
                return UiChange.All;
            case RemoveWorkspaceIngress remove:
                RemoveWorkspaceState(remove.Workspace, remove.Replacement);
                return UiChange.All;
            case SetPanelIngress setPanel:
                RegisterWorkspaceState(setPanel.Panel.Workspace);
                panels[(setPanel.Panel.Workspace, setPanel.Panel.Key)] = setPanel.Panel;
                if (setPanel.SwitchToWorkspace)
                    activeWorkspace = setPanel.Panel.Workspace;
                return UiChange.All;
            case RemovePanelIngress removePanel:
                panels.Remove((removePanel.Workspace, removePanel.Key));
                return UiChange.Workspace;
            case LogIngress log:
                if (log.Line.Workspace is not null)
                    RegisterWorkspaceState(log.Line.Workspace);
                AddLog(log.Line);
                LogAdded?.Invoke(log.Line);
                return UiChange.None;
            case NotifyIngress notify:
                if (notify.Workspace is not null)
                    RegisterWorkspaceState(notify.Workspace);
                if (notify.ExpiresAt > DateTimeOffset.Now)
                {
                    notifications.Add(new(
                        notify.Workspace,
                        notify.Text,
                        notify.Severity,
                        notify.ExpiresAt));
                }
                return UiChange.Overlays;
            case SwitchWorkspaceIngress switchWorkspace:
                RegisterWorkspaceState(switchWorkspace.Workspace);
                activeWorkspace = switchWorkspace.Workspace;
                return UiChange.All;
            case BindWorkspaceHotkeyIngress bindHotkey:
                RegisterWorkspaceState(bindHotkey.Workspace);
                var entry = HotkeyManager.RegisterTracked(
                    bindHotkey.Key,
                    bindHotkey.Modifiers,
                    bindHotkey.Description,
                    () =>
                    {
                        bindHotkey.Workspace.SwitchTo();
                        return Task.CompletedTask;
                    });
                var shortcutText = HotkeyManager.FormatKeyCombo(
                    bindHotkey.Key,
                    bindHotkey.Modifiers);
                var state = workspaces[bindHotkey.Workspace];
                if (state.Hotkey is { } previous)
                {
                    HotkeyManager.Unregister(
                        previous.Key,
                        previous.Modifiers,
                        previous.Entry);
                }
                workspaces[bindHotkey.Workspace] = state with
                {
                    ShortcutText = shortcutText,
                    Hotkey = new(bindHotkey.Key, bindHotkey.Modifiers, entry)
                };
                return UiChange.All;
            case ShowPopupIngress showPopup:
                if (showPopup.Generation >= hotkeyPopupGeneration)
                {
                    hotkeyPopup = showPopup.Popup;
                    hotkeyPopupGeneration = showPopup.Generation;
                }
                return UiChange.Overlays;
            case HidePopupIngress hidePopup:
                if (hidePopup.Generation >= hotkeyPopupGeneration)
                {
                    hotkeyPopup = null;
                    hotkeyPopupGeneration = hidePopup.Generation;
                }
                return UiChange.Overlays;
            case FlushIngress flush:
                flushCompletions.Add(flush.Completion);
                return UiChange.None;
            case ShutdownIngress:
                shutdown = true;
                return UiChange.None;
            default:
                throw new InvalidOperationException(
                    $"未知 UiHost ingress event: {uiEvent.GetType().FullName}");
        }
    }

    void RegisterWorkspaceState(Workspace workspace)
    {
        if (workspaces.ContainsKey(workspace))
            return;

        workspaces.Add(workspace, new(null, null));
        activeWorkspace ??= workspace;
    }

    void RemoveWorkspaceState(Workspace workspace, Workspace? replacement)
    {
        if (workspaces.Remove(workspace, out var state) && state.Hotkey is { } hotkey)
            HotkeyManager.Unregister(hotkey.Key, hotkey.Modifiers, hotkey.Entry);
        foreach (var key in panels.Keys
            .Where(key => ReferenceEquals(key.Workspace, workspace))
            .ToArray())
        {
            panels.Remove(key);
        }
        workspaceLogs.Remove(workspace);
        notifications.RemoveAll(
            notification => ReferenceEquals(notification.Workspace, workspace));

        if (ReferenceEquals(activeWorkspace, workspace))
        {
            activeWorkspace = replacement is not null && workspaces.ContainsKey(replacement)
                ? replacement
                : workspaces.Keys.FirstOrDefault();
        }
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

    void Reconcile(UiChange changes)
    {
        if (changes.HasFlag(UiChange.Workspace))
            RebuildWorkspaceLayer();
        if (changes.HasFlag(UiChange.Overlays))
            RefreshOverlayLayers();
    }

    void CreateWindow()
    {
        window = new MainWindow(RequestShutdown)
        {
            Title = "UmamusumeResponseAnalyzer",
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = null
        };
        workspaceLayer = new View
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            TabStop = TabBehavior.TabGroup
        };
        notificationLayer = CreateOverlayLabel();
        hotkeyLayer = CreateOverlayLabel();
        window.Add(workspaceLayer, notificationLayer, hotkeyLayer);
        window.Initialized += WindowInitialized;
        window.ViewportChanged += WindowViewportChanged;
    }

    void WindowInitialized(object? sender, EventArgs e)
    {
        Reconcile(UiChange.All);
        ready.TrySetResult();
    }

    void WindowViewportChanged(object? sender, DrawEventArgs e)
        => Reconcile(UiChange.All);

    void RebuildWorkspaceLayer()
    {
        if (window is null || workspaceLayer is null)
            return;

        if (workspaceView is not null)
        {
            workspaceLayer.Remove(workspaceView);
            workspaceView.Dispose();
        }

        workspaceView = BuildWorkspaceView();
        workspaceLayer.Add(workspaceView);
        window.Title = activeWorkspace is null
            ? "UmamusumeResponseAnalyzer"
            : $"UmamusumeResponseAnalyzer - {WorkspaceLabel(activeWorkspace)}";
        Volatile.Write(ref popupVisibleLineCount, Math.Max(1, window.Viewport.Height - 2));
        workspaceLayer.SetNeedsLayout();
        workspaceLayer.SetNeedsDraw();
    }

    View BuildWorkspaceView()
    {
        if (activeWorkspace is null)
            return CreateMessageView("当前没有 workspace。");

        var activePanels = panels.Values
            .Where(panel => ReferenceEquals(panel.Workspace, activeWorkspace))
            .OrderBy(panel => panel.Key, StringComparer.Ordinal)
            .ToArray();
        var fullBleed = activePanels
            .Where(panel => panel.FullBleed)
            .MaxBy(panel => panel.Sequence);
        if (fullBleed is not null)
            activePanels = [fullBleed];
        if (activePanels.Length == 0)
            return CreateMessageView($"{activeWorkspace.Title} 还没有输出。");

        var root = new View
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            TabStop = TabBehavior.TabGroup
        };
        View? previous = null;
        for (var index = 0; index < activePanels.Length; index++)
        {
            var panel = activePanels[index];
            var content = panel.Content.CreateView();
            content.X = 0;
            content.Y = 0;
            content.Width = Dim.Fill();
            content.Height = Dim.Fill();
            if (activePanels.Length == 1 && panel.FullBleed)
            {
                root.Add(content);
                continue;
            }

            var frame = new FrameView
            {
                Title = panel.Title,
                X = 0,
                Y = previous is null ? 0 : Pos.Bottom(previous),
                Width = Dim.Fill(),
                Height = index == activePanels.Length - 1
                    ? Dim.Fill()
                    : Dim.Percent(Math.Max(1, 100 / activePanels.Length))
            };
            frame.Add(content);
            root.Add(frame);
            previous = frame;
        }
        return root;
    }

    string WorkspaceLabel(Workspace workspace)
    {
        return workspaces.TryGetValue(workspace, out var state) &&
               !string.IsNullOrEmpty(state.ShortcutText)
            ? $"{state.ShortcutText} {workspace.Title}"
            : workspace.Title;
    }

    static View CreateMessageView(string text)
        => new Label
        {
            Text = text,
            X = 1,
            Y = 1,
            Width = Dim.Fill(1),
            Height = 1
        };

    static Label CreateOverlayLabel()
        => new()
        {
            CanFocus = false,
            Enabled = false,
            Visible = false,
            ViewportSettings =
                ViewportSettingsFlags.Transparent |
                ViewportSettingsFlags.TransparentMouse
        };

    void RefreshOverlayLayers()
    {
        if (window is null || notificationLayer is null || hotkeyLayer is null)
            return;

        notificationLayer.Visible = false;
        hotkeyLayer.Visible = false;

        var visibleNotifications = notifications
            .Where(notification =>
                notification.Workspace is null ||
                ReferenceEquals(notification.Workspace, activeWorkspace))
            .OrderBy(notification => notification.ExpiresAt)
            .TakeLast(3)
            .ToArray();
        if (visibleNotifications.Length > 0)
        {
            var lines = visibleNotifications
                .Select(notification =>
                    $"{SeverityText(notification.Severity)} {notification.Text}")
                .ToArray();
            notificationLayer.Text = string.Join(Environment.NewLine, lines);
            notificationLayer.X = 1;
            notificationLayer.Y = 1;
            notificationLayer.Width = Dim.Fill(2);
            notificationLayer.Height = lines.Length;
            notificationLayer.Visible = true;
        }

        if (hotkeyPopup is not null)
        {
            var lines = hotkeyPopup.Lines
                .Select((line, index) =>
                    hotkeyPopup.Selection?.SelectedLineIndex == index
                        ? $"> {line.Text}"
                        : $"  {line.Text}")
                .Skip(hotkeyPopup.ScrollOffset)
                .Take(Math.Max(1, window.Viewport.Height - 2))
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

    bool RefreshExpiringOverlays()
    {
        if (Volatile.Read(ref state) != StateRunning)
            return false;

        var now = DateTimeOffset.Now;
        var expired = notifications
            .Where(notification => notification.ExpiresAt <= now)
            .ToArray();
        if (expired.Length > 0)
        {
            notifications.RemoveAll(notification => notification.ExpiresAt <= now);
            RefreshOverlayLayers();
        }
        return Volatile.Read(ref state) == StateRunning && !shutdownRequested;
    }

    static string SeverityText(UiSeverity severity)
        => severity switch
        {
            UiSeverity.Trace => "TRACE",
            UiSeverity.Info => "INFO",
            UiSeverity.Success => "OK",
            UiSeverity.Warning => "WARN",
            UiSeverity.Error => "ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null)
        };

    void FinishRun()
    {
        lock (ingressGate)
        {
            state = StateStopped;
            drainRunning = false;
            drainScheduled = false;
            pendingEvents = 0;
            events.Writer.TryComplete();
        }

        while (events.Reader.TryRead(out var admitted))
            Abandon(admitted.MarkAbandoned());
        foreach (var state in workspaces.Values)
        {
            if (state.Hotkey is { } hotkey)
                HotkeyManager.Unregister(hotkey.Key, hotkey.Modifiers, hotkey.Entry);
        }
        ready.TrySetCanceled();
        lifetimeRegistration.Dispose();
    }

    static void Abandon(IngressEvent uiEvent)
    {
        switch (uiEvent)
        {
            case FlushIngress flush:
                flush.Completion.TrySetException(
                    new InvalidOperationException(
                        "UiHost stopped before the accepted flush event was applied."));
                break;
        }
    }

    void DisposeWindow()
    {
        if (window is null)
            return;

        window.ViewportChanged -= WindowViewportChanged;
        window.Initialized -= WindowInitialized;
        window.Dispose();
        window = null;
        workspaceLayer = null;
        workspaceView = null;
        notificationLayer = null;
        hotkeyLayer = null;
    }

    static Channel<T> CreateUiChannel<T>()
        => Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    abstract record IngressEvent;
    sealed record RegisterWorkspaceIngress(Workspace Workspace) : IngressEvent;
    sealed record RemoveWorkspaceIngress(
        Workspace Workspace,
        Workspace? Replacement) : IngressEvent;
    sealed record SetPanelIngress(
        WorkspacePanel Panel,
        bool SwitchToWorkspace) : IngressEvent;
    sealed record RemovePanelIngress(
        Workspace Workspace,
        string Key) : IngressEvent;
    sealed record LogIngress(UiLogLine Line) : IngressEvent;
    sealed record NotifyIngress(
        Workspace? Workspace,
        string Text,
        UiSeverity Severity,
        DateTimeOffset ExpiresAt) : IngressEvent;
    sealed record SwitchWorkspaceIngress(Workspace Workspace) : IngressEvent;
    sealed record BindWorkspaceHotkeyIngress(
        Workspace Workspace,
        ConsoleKey Key,
        ConsoleModifiers Modifiers,
        string Description) : IngressEvent;
    sealed record ShowPopupIngress(
        HotkeyPopup Popup,
        int Generation) : IngressEvent;
    sealed record HidePopupIngress(int Generation) : IngressEvent;
    sealed record FlushIngress(TaskCompletionSource Completion) : IngressEvent;
    sealed record ShutdownIngress : IngressEvent;

    sealed class AdmittedUiEvent(IngressEvent uiEvent)
    {
        int disposition;

        internal bool IsFlush => uiEvent is FlushIngress;

        internal IngressEvent MarkApplied()
        {
            if (Interlocked.CompareExchange(ref disposition, 1, 0) != 0)
                throw new InvalidOperationException("UiHost ingress event 已处理。");
            return uiEvent;
        }

        internal IngressEvent MarkAbandoned()
        {
            if (Interlocked.CompareExchange(ref disposition, 2, 0) != 0)
                throw new InvalidOperationException("UiHost ingress event 已处理。");
            return uiEvent;
        }
    }

    sealed record WorkspaceHotkey(
        ConsoleKey Key,
        ConsoleModifiers Modifiers,
        HotkeyManager.HotkeyEntry Entry);

    sealed record WorkspaceState(
        string? ShortcutText,
        WorkspaceHotkey? Hotkey);

    sealed record DrainBatchResult(
        int Count,
        UiChange Changes,
        List<TaskCompletionSource> FlushCompletions,
        bool Shutdown);

    sealed class MainWindow : Window
    {
        internal MainWindow(Action shutdown)
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
