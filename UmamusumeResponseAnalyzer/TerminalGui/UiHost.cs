using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.Commands;
using UmamusumeResponseAnalyzer.Plugin;

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
        Notifications = 2,
        Hotkey = 4,
        All = Workspace | Notifications | Hotkey
    }

    readonly object ingressGate = new();
    readonly WorkspaceRegistry registry = new();
    readonly HashSet<(Workspace Workspace, string Key)> admittedPanels = [];
    readonly HashSet<HotkeyManager.HotkeyEntry> pendingWorkspaceHotkeys =
        new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Workspace, WorkspaceHotkey> workspaceHotkeys =
        new(ReferenceEqualityComparer.Instance);
    readonly Channel<AdmittedUiEvent> events = Channel.CreateUnbounded<AdmittedUiEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly List<UiLogLine> globalLogs = [];
    readonly Dictionary<Workspace, List<UiLogLine>> workspaceLogs =
        new(ReferenceEqualityComparer.Instance);
    readonly List<NotificationState> notifications = [];
    readonly CancellationTokenSource stopping = new();
    readonly SemaphoreSlim commandExecution = new(1, 1);
    readonly CancellationTokenRegistration lifetimeRegistration;

    HostCommands.Snapshot commandSnapshot = HostCommands.Snapshot.Empty;
    Workspace[] renderedWorkspaces = [];
    Workspace? renderedWorkspace;
    HotkeyPopup? hotkeyPopup;
    int hotkeyPopupGeneration;
    int popupVisibleLineCount = 1;
    int pendingEvents;
    int state;
    long panelSequence;
    bool drainRunning;
    bool drainScheduled;
    bool runClaimed;
    bool rootCreated;
    bool shutdownRequested;
    bool applicationStopRequested;
    int shutdownStarted;
    Exception? ingressFailure;
    Exception? rootCreationFailure;
    Window? window;
    WorkspaceViewport? workspaceViewport;
    WorkspaceTaskbarView? workspaceTaskbar;
    NotificationOverlayView? notificationOverlay;
    Label? hotkeyLayer;
    CommandModeView? commandMode;
    object? overlayTimer;

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
    internal event Action? ShutdownStarting;

    internal IReadOnlyList<UiLogLine> GetLogsForTests(Workspace? workspace)
    {
        var source = workspace is null
            ? globalLogs
            : workspaceLogs.GetValueOrDefault(workspace) ?? [];
        return source.ToArray();
    }

    internal IReadOnlyList<UiNotification> GetNotificationsForTests(Workspace? workspace)
        => notifications
            .Where(notification => ReferenceEquals(notification.Workspace, workspace))
            .Select(notification => new UiNotification(
                notification.Workspace,
                notification.Text,
                notification.Severity,
                notification.ExpiresAt))
            .ToArray();

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

            var registrationOrder = registry.SnapshotRegistrationOrder();
            CaptureCommandSnapshotLocked(registrationOrder);
            schedule = AdmitLocked(new RegisterWorkspaceIngress(
                workspace,
                registrationOrder,
                registry.Current));
        }
        ScheduleDrain(schedule);
        return workspace;
    }

    internal void RemoveWorkspace(Workspace workspace)
    {
        bool schedule;
        Workspace? replacement;
        Workspace[] registrationOrder;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            if (!registry.Remove(workspace, out replacement))
                return;

            admittedPanels.RemoveWhere(key => ReferenceEquals(key.Workspace, workspace));
            registrationOrder = registry.SnapshotRegistrationOrder();
            CaptureCommandSnapshotLocked(registrationOrder);
            schedule = AdmitLocked(new RemoveWorkspaceIngress(
                workspace,
                replacement,
                registrationOrder));
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
            {
                registry.SwitchTo(workspace);
                commandSnapshot = commandSnapshot with { CurrentWorkspace = workspace };
            }
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
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(shortcuts);
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            if (workspace is not null)
                registry.EnsureLive(workspace);
        }

        var expiresAt = UiNotification.ExpiresAtFromNow(severity, ttl);
        var shortcutRegistrationId =
            HotkeyManager.RegisterNotificationShortcuts(expiresAt, shortcuts);
        try
        {
            bool schedule;
            lock (ingressGate)
            {
                EnsureAvailableLocked();
                if (workspace is not null)
                    registry.EnsureLive(workspace);
                schedule = AdmitLocked(new NotifyIngress(
                    workspace,
                    text,
                    severity,
                    expiresAt,
                    shortcutRegistrationId));
            }
            ScheduleDrain(schedule);
        }
        catch
        {
            HotkeyManager.UnregisterNotificationShortcuts(shortcutRegistrationId);
            throw;
        }
    }

    internal void SwitchWorkspace(Workspace workspace)
    {
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            registry.SwitchTo(workspace);
            commandSnapshot = commandSnapshot with { CurrentWorkspace = workspace };
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
        var entry = HotkeyManager.CaptureTracked(
            description ?? $"切换到 {workspace.Title}",
            () =>
            {
                workspace.SwitchTo();
                return Task.CompletedTask;
            });
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
            pendingWorkspaceHotkeys.Add(entry);
            try
            {
                schedule = AdmitLocked(new BindWorkspaceHotkeyIngress(
                    workspace,
                    key,
                    modifiers,
                    entry));
            }
            catch
            {
                pendingWorkspaceHotkeys.Remove(entry);
                throw;
            }
        }
        ScheduleDrain(schedule);
    }

    internal int RemoveWorkspaceHotkeysByOwner(object owner)
    {
        lock (ingressGate)
        {
            var canceled = pendingWorkspaceHotkeys.RemoveWhere(
                entry => ReferenceEquals(entry.Owner, owner));
            foreach (var workspace in workspaceHotkeys
                .Where(pair => ReferenceEquals(pair.Value.Entry.Owner, owner))
                .Select(pair => pair.Key)
                .ToArray())
            {
                workspaceHotkeys.Remove(workspace);
            }
            CaptureCommandSnapshotLocked();
            return canceled;
        }
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

    internal Task HandleCommandAsync(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (ingressGate)
            EnsureAvailableLocked();
        return QueueCommandAsync(command);
    }

    async Task QueueCommandAsync(string command)
    {
        await commandExecution.WaitAsync(stopping.Token).ConfigureAwait(false);
        try
        {
            HostCommands.Snapshot snapshot;
            lock (ingressGate)
            {
                EnsureAvailableLocked();
                snapshot = commandSnapshot;
            }
            await Task.Run(() => ExecuteCommandAsync(command, snapshot)).ConfigureAwait(false);
        }
        finally
        {
            commandExecution.Release();
        }
    }

    internal IReadOnlyList<string> CompleteCommand(string input)
    {
        HostCommands.Snapshot snapshot;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            snapshot = commandSnapshot;
        }
        return HostCommands.Complete(input, snapshot);
    }

    internal void RequestShutdown()
    {
        if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            ShutdownStarting?.Invoke();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        var pluginShutdown = PluginManager.ShutdownAsync();
        _ = OrchestrateShutdownAsync(pluginShutdown, failure);
    }

    async Task OrchestrateShutdownAsync(Task pluginShutdown, Exception? failure)
    {
        try
        {
            await pluginShutdown;
        }
        catch (Exception ex)
        {
            failure = CombineFailure(failure, ex);
        }

        try
        {
            await BeginHostShutdownAsync();
        }
        catch (Exception ex)
        {
            failure = CombineFailure(failure, ex);
        }

        if (failure is null)
            shutdownCompletion.TrySetResult();
        else
            shutdownCompletion.TrySetException(failure);
    }

    async Task BeginHostShutdownAsync()
    {
        bool schedule;
        lock (ingressGate)
        {
            state = StateStopping;
            if (!events.Writer.TryWrite(new(new ShutdownIngress())))
                throw new InvalidOperationException("UiHost shutdown event 无法写入 ingress channel。");
            pendingEvents++;
            events.Writer.TryComplete();
            schedule = ArmDrainLocked();
        }

        try
        {
            await Task.Run(DisconnectHotkeys);
        }
        finally
        {
            try
            {
                stopping.Cancel();
            }
            finally
            {
                ScheduleDrain(schedule);
                OwnerContext.Post(
                    static state => ((UiHost)state!).RequestApplicationStop(),
                    this);
            }
        }
    }

    internal async Task RunAsync()
    {
        lock (ingressGate)
        {
            if (runClaimed || state == StateStopped)
                throw new InvalidOperationException("UiHost.RunAsync 只能调用一次。");

            runClaimed = true;
            if (state == StateCreated)
                state = StateRunning;
        }

        ExceptionDispatchInfo? primaryFailure = null;
        try
        {
            try
            {
                var plugins = await Task.Run(PluginManager.SnapshotPluginStatuses);
                lock (ingressGate)
                    CaptureCommandSnapshotLocked(plugins: plugins);
                CreateWindow();
            }
            catch (Exception ex)
            {
                var rollbackFailure = FailRootCreation(ex);
                if (rollbackFailure is not null)
                    throw new AggregateException(ex, rollbackFailure);
                throw;
            }

            bool schedule;
            lock (ingressGate)
            {
                rootCreated = true;
                schedule = ArmDrainLocked();
            }
            ScheduleDrain(schedule);
            if (!shutdownRequested)
            {
                overlayTimer = Application.AddTimeout(
                    TimeSpan.FromMilliseconds(250),
                    RefreshExpiringOverlays);
                await Application.RunAsync(window!, CancellationToken.None);
            }

            if (ingressFailure is not null)
                ExceptionDispatchInfo.Capture(ingressFailure).Throw();
        }
        catch (Exception ex)
        {
            var failure = ingressFailure is null || ReferenceEquals(ingressFailure, ex)
                ? ex
                : CombineFailure(ingressFailure, ex);
            primaryFailure = ExceptionDispatchInfo.Capture(failure);
        }

        RequestShutdown();
        Exception? cleanupFailure = null;
        try
        {
            await shutdownCompletion.Task;
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
        }

        void CaptureCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                cleanupFailure = CombineFailure(cleanupFailure, ex);
            }
        }

        CaptureCleanup(() =>
        {
            if (overlayTimer is null)
                return;
            Application.RemoveTimeout(overlayTimer);
            overlayTimer = null;
        });
        CaptureCleanup(DisconnectHotkeys);
        CaptureCleanup(FinishRun);
        CaptureCleanup(DisposeWindow);

        if (primaryFailure is not null)
        {
            if (cleanupFailure is not null)
                throw new AggregateException(primaryFailure.SourceException, cleanupFailure);
            primaryFailure.Throw();
        }
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
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
        if (command is not (
            Command.Up or Command.Down or Command.PageUp or Command.PageDown or
            Command.Start or Command.End))
        {
            return Task.FromResult(false);
        }

        if (Environment.CurrentManagedThreadId == Application.MainThreadId)
        {
            EnsureAvailable();
            var viewport = workspaceViewport
                ?? throw new InvalidOperationException("UiHost workspace viewport 尚未创建。");
            return Task.FromResult(viewport.Navigate(command));
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool schedule;
        lock (ingressGate)
        {
            EnsureAvailableLocked();
            schedule = AdmitLocked(new NavigateWorkspaceIngress(command, completion));
        }
        ScheduleDrain(schedule);
        return completion.Task;
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
            if (state >= StateStopping || rootCreationFailure is not null)
                return;
            schedule = AdmitLocked(new HidePopupIngress(generation));
        }
        ScheduleDrain(schedule);
    }

    void EnsureAvailableLocked()
    {
        if (state >= StateStopping)
        {
            var lifecycle = state == StateStopped ? "stopped" : "stopping";
            throw new InvalidOperationException(
                $"UiHost is {lifecycle}; new UI operations are not accepted.");
        }
        if (rootCreationFailure is not null)
        {
            throw new InvalidOperationException(
                "UiHost root creation failed; new UI operations are not accepted.",
                rootCreationFailure);
        }
    }

    void CaptureCommandSnapshotLocked(
        Workspace[]? registrationOrder = null,
        IReadOnlyList<PluginManager.PluginRuntimeStatus>? plugins = null)
    {
        registrationOrder ??= registry.SnapshotRegistrationOrder();
        commandSnapshot = new(
            registrationOrder.Select(workspace => new HostCommands.WorkspaceItem(
                workspace,
                workspaceHotkeys.GetValueOrDefault(workspace)?.ShortcutText)).ToArray(),
            registry.Current,
            plugins ?? commandSnapshot.Plugins);
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
            !rootCreated ||
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
        {
            OwnerContext.Post(
                static state => _ = ((UiHost)state!).DrainPostedEvents(),
                this);
        }
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

            var failure = batch.Failure;
            try
            {
                Reconcile(batch.Changes);
            }
            catch (Exception ex)
            {
                failure = CombineFailure(failure, ex);
            }

            foreach (var completion in batch.FlushCompletions)
            {
                if (failure is null)
                    completion.TrySetResult();
                else
                    completion.TrySetException(failure);
            }

            if (failure is not null)
            {
                ingressFailure = CombineFailure(ingressFailure, failure);
                RequestShutdown();
            }

            if (batch.Shutdown)
            {
                shutdownRequested = true;
                RequestApplicationStop();
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
        var shutdown = admittedEvents.Any(admitted => admitted.Event is ShutdownIngress);
        Exception? failure = null;
        for (var index = 0; index < admittedEvents.Count; index++)
        {
            try
            {
                changes |= Apply(admittedEvents[index].Event, flushCompletions, ref shutdown);
                admittedEvents[index].MarkApplied();
            }
            catch (Exception ex)
            {
                failure = AbandonAll(
                    admittedEvents,
                    index,
                    ex,
                    flushCompletions)!;
                break;
            }
        }
        return new(admittedEvents.Count, changes, flushCompletions, shutdown, failure);
    }

    UiChange Apply(
        IngressEvent uiEvent,
        List<TaskCompletionSource> flushCompletions,
        ref bool shutdown)
    {
        var viewport = workspaceViewport
            ?? throw new InvalidOperationException("UiHost workspace viewport 尚未创建。");
        switch (uiEvent)
        {
            case RegisterWorkspaceIngress register:
                renderedWorkspaces = register.RegistrationOrder;
                renderedWorkspace = register.CurrentWorkspace;
                viewport.SetActiveWorkspace(renderedWorkspace);
                return UiChange.Workspace;
            case RemoveWorkspaceIngress remove:
                viewport.RemoveWorkspace(remove.Workspace);
                workspaceLogs.Remove(remove.Workspace);
                renderedWorkspaces = remove.RegistrationOrder;
                renderedWorkspace = remove.Replacement;
                viewport.SetActiveWorkspace(renderedWorkspace);
                RemoveWorkspaceNotifications(remove.Workspace);
                WorkspaceHotkey? removedHotkey;
                lock (ingressGate)
                {
                    workspaceHotkeys.Remove(remove.Workspace, out removedHotkey);
                    CaptureCommandSnapshotLocked();
                }
                if (removedHotkey is not null)
                {
                    HotkeyManager.Unregister(
                        removedHotkey.Key,
                        removedHotkey.Modifiers,
                        removedHotkey.Entry);
                }
                return UiChange.All;
            case SetPanelIngress setPanel:
                viewport.SetPanel(setPanel.Panel);
                if (setPanel.SwitchToWorkspace)
                {
                    renderedWorkspace = setPanel.Panel.Workspace;
                    viewport.SetActiveWorkspace(renderedWorkspace);
                    return UiChange.All;
                }
                return UiChange.Workspace;
            case RemovePanelIngress removePanel:
                viewport.RemovePanel(removePanel.Workspace, removePanel.Key);
                return UiChange.Workspace;
            case LogIngress log:
                var trimmedLog = AddLog(log.Line);
                try
                {
                    LogAdded?.Invoke(log.Line);
                }
                catch
                {
                    RollbackLog(log.Line, trimmedLog);
                    throw;
                }
                return UiChange.None;
            case NotifyIngress notify:
                if (notify.ExpiresAt <= DateTimeOffset.Now)
                {
                    HotkeyManager.UnregisterNotificationShortcuts(
                        notify.ShortcutRegistrationId);
                }
                else
                {
                    notifications.Add(new(
                        notify.Workspace,
                        notify.Text,
                        notify.Severity,
                        notify.ExpiresAt,
                        notify.ShortcutRegistrationId));
                }
                return UiChange.Notifications;
            case SwitchWorkspaceIngress switchWorkspace:
                renderedWorkspace = switchWorkspace.Workspace;
                viewport.SetActiveWorkspace(renderedWorkspace);
                return UiChange.All;
            case BindWorkspaceHotkeyIngress bindHotkey:
                using (var registration = bindHotkey.Entry.Owner is IPlugin plugin
                    ? PluginManager.TryEnterPluginRegistration(plugin)
                    : null)
                {
                    if (bindHotkey.Entry.Owner is IPlugin && registration is null)
                    {
                        lock (ingressGate)
                            pendingWorkspaceHotkeys.Remove(bindHotkey.Entry);
                        return UiChange.None;
                    }

                    lock (ingressGate)
                        if (!pendingWorkspaceHotkeys.Contains(bindHotkey.Entry))
                            return UiChange.None;

                    var replaced = HotkeyManager.RegisterTracked(
                        bindHotkey.Key,
                        bindHotkey.Modifiers,
                        bindHotkey.Entry);
                    WorkspaceHotkey? previous = null;
                    var canceled = false;
                    try
                    {
                        var shortcut = HotkeyManager.FormatKeyCombo(
                            bindHotkey.Key,
                            bindHotkey.Modifiers);
                        lock (ingressGate)
                        {
                            if (!pendingWorkspaceHotkeys.Remove(bindHotkey.Entry))
                            {
                                canceled = true;
                            }
                            else
                            {
                                previous = workspaceHotkeys.GetValueOrDefault(
                                    bindHotkey.Workspace);
                                var displacedMetadata = replaced is null
                                    ? []
                                    : workspaceHotkeys
                                        .Where(pair => ReferenceEquals(
                                            pair.Value.Entry,
                                            replaced))
                                        .ToArray();
                                workspaceHotkeys.Remove(bindHotkey.Workspace);
                                foreach (var displaced in displacedMetadata)
                                    workspaceHotkeys.Remove(displaced.Key);
                                workspaceHotkeys.Add(bindHotkey.Workspace, new(
                                    bindHotkey.Key,
                                    bindHotkey.Modifiers,
                                    shortcut,
                                    bindHotkey.Entry));
                                try
                                {
                                    CaptureCommandSnapshotLocked();
                                }
                                catch
                                {
                                    workspaceHotkeys.Remove(bindHotkey.Workspace);
                                    foreach (var displaced in displacedMetadata)
                                        workspaceHotkeys[displaced.Key] = displaced.Value;
                                    if (previous is not null)
                                        workspaceHotkeys[bindHotkey.Workspace] = previous;
                                    throw;
                                }
                            }
                        }
                        if (canceled)
                        {
                            HotkeyManager.RestoreTracked(
                                bindHotkey.Key,
                                bindHotkey.Modifiers,
                                bindHotkey.Entry,
                                replaced);
                            return UiChange.None;
                        }
                        if (previous is not null)
                        {
                            HotkeyManager.Unregister(
                                previous.Key,
                                previous.Modifiers,
                                previous.Entry);
                        }
                    }
                    catch
                    {
                        HotkeyManager.RestoreTracked(
                            bindHotkey.Key,
                            bindHotkey.Modifiers,
                            bindHotkey.Entry,
                            replaced);
                        throw;
                    }
                }
                return UiChange.None;
            case NavigateWorkspaceIngress navigate:
                navigate.Completion.TrySetResult(viewport.Navigate(navigate.Command));
                return UiChange.None;
            case ShowPopupIngress showPopup:
                if (showPopup.Generation >= hotkeyPopupGeneration)
                {
                    hotkeyPopup = showPopup.Popup;
                    hotkeyPopupGeneration = showPopup.Generation;
                }
                return UiChange.Hotkey;
            case HidePopupIngress hidePopup:
                if (hidePopup.Generation >= hotkeyPopupGeneration)
                {
                    hotkeyPopup = null;
                    hotkeyPopupGeneration = hidePopup.Generation;
                }
                return UiChange.Hotkey;
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

    void Reconcile(UiChange changes)
    {
        if (changes.HasFlag(UiChange.Workspace))
        {
            workspaceViewport?.Reconcile();
            workspaceTaskbar?.Refresh(renderedWorkspaces, renderedWorkspace);
            if (window is not null)
            {
                window.Title = renderedWorkspace is null
                    ? "UmamusumeResponseAnalyzer"
                    : $"UmamusumeResponseAnalyzer - {renderedWorkspace.Title}";
                Volatile.Write(
                    ref popupVisibleLineCount,
                    Math.Max(1, window.Viewport.Height - 2));
            }
        }
        if (changes.HasFlag(UiChange.Notifications))
            RefreshNotificationOverlay();
        if (changes.HasFlag(UiChange.Hotkey))
            RefreshHotkeyOverlay();
    }

    UiLogLine? AddLog(UiLogLine line)
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
        if (bank.Count <= MaxLogLines)
            return null;

        var trimmed = bank[0];
        bank.RemoveAt(0);
        return trimmed;
    }

    void RollbackLog(UiLogLine line, UiLogLine? trimmed)
    {
        var bank = line.Workspace is null
            ? globalLogs
            : workspaceLogs[line.Workspace];
        if (!ReferenceEquals(bank[^1], line))
            throw new InvalidOperationException("UiHost log bank rollback 顺序失衡。");

        bank.RemoveAt(bank.Count - 1);
        if (trimmed is not null)
            bank.Insert(0, trimmed);
        if (line.Workspace is not null && bank.Count == 0)
            workspaceLogs.Remove(line.Workspace);
    }

    void CreateWindow()
    {
        var savedTaskbarTitleOrder = Config.WorkspaceTaskbarTitleOrder?.ToArray()
            ?? throw new InvalidOperationException(
                "Config.WorkspaceTaskbarTitleOrder must not be null.");
        window = new MainWindow(RequestShutdown)
        {
            Title = "UmamusumeResponseAnalyzer",
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = null
        };
        workspaceViewport = new();
        commandMode = new(
            command => TrackInputTask(HandleCommandAsync(command)),
            CompleteCommand);
        workspaceTaskbar = new(
            () => commandMode.IsOpen,
            SwitchWorkspace,
            savedTaskbarTitleOrder,
            titleOrder =>
            {
                Config.WorkspaceTaskbarTitleOrder = [.. titleOrder];
                Config.Save();
            });
        notificationOverlay = new();
        hotkeyLayer = CreateOverlayLabel();
        window.Add(
            workspaceViewport,
            workspaceTaskbar.BottomEdgeTrigger,
            workspaceTaskbar,
            notificationOverlay,
            hotkeyLayer,
            commandMode);
        window.Initialized += WindowInitialized;
        window.ViewportChanged += WindowViewportChanged;
        window.KeyDownNotHandled += WindowKeyDownNotHandled;
        window.MouseEvent += WindowMouseEvent;
        commandMode.VisibleChanged += CommandModeVisibleChanged;
        Application.Keyboard.KeyDown += ApplicationKeyDown;
        Application.LayoutAndDrawComplete += ApplicationLayoutAndDrawComplete;
    }

    void WindowInitialized(object? sender, EventArgs e)
        => Reconcile(UiChange.All);

    void ApplicationLayoutAndDrawComplete(object? sender, EventArgs e)
    {
        if (window is null || !ReferenceEquals(Application.TopRunnableView, window))
            return;

        Application.LayoutAndDrawComplete -= ApplicationLayoutAndDrawComplete;
        ready.TrySetResult();
    }

    void WindowViewportChanged(object? sender, DrawEventArgs e)
        => Reconcile(UiChange.All);

    void CommandModeVisibleChanged(object? sender, EventArgs e)
        => workspaceTaskbar?.CommandModeVisibilityChanged();

    void ApplicationKeyDown(object? sender, Key key)
    {
        if (key.Handled || key.KeyCode == Key.C.WithCtrl.KeyCode)
            return;
        if (window is null ||
            !ReferenceEquals(Application.TopRunnableView, window) ||
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
            ((!key.IsCtrl && !key.IsAlt &&
              key.TryGetPrintableRune(out var rune) && rune.Value == '/') ||
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
        workspaceTaskbar?.HandleMousePosition(mouse);
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

    static void TrackInputTask(Task task)
        => _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    async Task DispatchKeyOrOpenCommandAsync(Key key, string initialText)
    {
        if (await DispatchKeyAsync(key))
            return;

        Application.Invoke(() =>
        {
            if (window is not null &&
                commandMode is not null &&
                ReferenceEquals(Application.TopRunnableView, window))
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

    async Task ExecuteCommandAsync(
        string command,
        HostCommands.Snapshot snapshot)
    {
        HostCommands.Result? result = null;
        Exception? failure = null;
        try
        {
            snapshot = snapshot with
            {
                Plugins = PluginManager.SnapshotPluginStatuses()
            };
            result = await HostCommands.ExecuteAsync(
                command,
                snapshot,
                stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (stopping.IsCancellationRequested && failure is null)
            return;

        IReadOnlyList<PluginManager.PluginRuntimeStatus>? plugins = null;
        Exception? refreshFailure = null;
        if (!stopping.IsCancellationRequested)
        {
            try
            {
                plugins = PluginManager.SnapshotPluginStatuses();
            }
            catch (Exception ex)
            {
                refreshFailure = ex;
            }
        }

        Exception? postFailure = null;
        if (failure is not null)
        {
            try
            {
                TerminalUi.LogException("Command", failure);
            }
            catch (Exception ex)
            {
                postFailure = CombineFailure(failure, ex);
            }
        }

        if (stopping.IsCancellationRequested)
        {
            if (postFailure is not null)
                ExceptionDispatchInfo.Capture(postFailure).Throw();
            if (failure is not null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        Exception? ownerFailure = null;
        try
        {
            await InvokeOnOwnerAsync(() =>
            {
                EnsureAvailable();
                try
                {
                    if (result is not null)
                        ApplyCommandResult(result);
                }
                finally
                {
                    if (plugins is not null)
                    {
                        lock (ingressGate)
                        {
                            EnsureAvailableLocked();
                            CaptureCommandSnapshotLocked(plugins: plugins);
                        }
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ownerFailure = ex;
        }

        if (stopping.IsCancellationRequested &&
            postFailure is null &&
            failure is null &&
            refreshFailure is null)
            return;

        if (postFailure is null &&
            failure is not null &&
            (refreshFailure is not null ||
             ownerFailure is not null ||
             stopping.IsCancellationRequested))
        {
            postFailure = failure;
        }
        if (refreshFailure is not null)
            postFailure = CombineFailure(postFailure, refreshFailure);
        if (ownerFailure is not null)
            postFailure = CombineFailure(postFailure, ownerFailure);
        if (postFailure is not null)
            ExceptionDispatchInfo.Capture(postFailure).Throw();
    }

    Task InvokeOnOwnerAsync(Action action)
    {
        if (Environment.CurrentManagedThreadId == Application.MainThreadId)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        OwnerContext.Post(_ =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, null);
        return completion.Task;
    }

    void ApplyCommandResult(HostCommands.Result result)
    {
        if (!string.IsNullOrEmpty(result.Message))
        {
            Log(
                null,
                $"[Command] {result.Message}",
                result.Severity);
        }
        if (result.Display is not null)
            ShowCommandDisplay(result.Display);
        if (result.SwitchWorkspace is not null)
            SwitchWorkspace(result.SwitchWorkspace);
    }

    void ShowCommandDisplay(HostCommands.Display display)
    {
        var lines = new List<HotkeyPopupLine> { new(display.Title) };
        lines.AddRange(display.Items.Select(item => new HotkeyPopupLine(item.Text)));
        HotkeyPopupSelection? selection = null;
        if (display.SelectedIndex is { } selectedIndex &&
            selectedIndex >= 0 &&
            selectedIndex < display.Items.Count)
        {
            var selectable = display.Items
                .Select((item, index) => (item, index))
                .Where(entry => entry.item.Workspace is not null)
                .Select(entry => entry.index)
                .ToArray();
            var selected = Array.IndexOf(selectable, selectedIndex);
            if (selected >= 0)
            {
                selection = new(
                    selectable.Select(index => index + 1).ToArray(),
                    selected,
                    lineIndex =>
                    {
                        display.Items[lineIndex - 1].Workspace?.SwitchTo();
                        return Task.CompletedTask;
                    });
            }
        }
        HotkeyManager.ShowPopup(new HotkeyPopup(lines, Selection: selection));
    }

    void RefreshNotificationOverlay()
    {
        if (window is null || notificationOverlay is null)
            return;

        var now = DateTimeOffset.Now;
        notificationOverlay.UpdateSnapshot(new(
            notifications
                .Where(notification =>
                    notification.Workspace is null ||
                    ReferenceEquals(notification.Workspace, renderedWorkspace))
                .Select(notification => new NotificationOverlayItem(
                    notification.Text,
                    notification.Severity,
                    notification.ExpiresAt))
                .ToImmutableArray(),
            now,
            window.Viewport));
    }

    void RefreshHotkeyOverlay()
    {
        if (window is null || hotkeyLayer is null)
            return;

        hotkeyLayer.Visible = false;
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
        hotkeyLayer.SetNeedsDraw();
    }

    bool RefreshExpiringOverlays()
    {
        if (Volatile.Read(ref state) != StateRunning)
            return false;

        var now = DateTimeOffset.Now;
        foreach (var notification in notifications
            .Where(notification => notification.ExpiresAt <= now)
            .ToArray())
        {
            HotkeyManager.UnregisterNotificationShortcuts(
                notification.ShortcutRegistrationId);
        }
        notifications.RemoveAll(notification => notification.ExpiresAt <= now);
        RefreshNotificationOverlay();
        return Volatile.Read(ref state) == StateRunning && !shutdownRequested;
    }

    void RemoveWorkspaceNotifications(Workspace workspace)
    {
        foreach (var notification in notifications
            .Where(notification => ReferenceEquals(notification.Workspace, workspace))
            .ToArray())
        {
            HotkeyManager.UnregisterNotificationShortcuts(
                notification.ShortcutRegistrationId);
        }
        notifications.RemoveAll(
            notification => ReferenceEquals(notification.Workspace, workspace));
    }

    void DisconnectHotkeys()
    {
        if (!ReferenceEquals(HotkeyManager.OverlaySink, this))
            return;

        HotkeyManager.OverlaySink = null;
        HotkeyManager.UnregisterAll();
    }

    void RequestApplicationStop()
    {
        if (!shutdownRequested ||
            !stopping.IsCancellationRequested ||
            applicationStopRequested)
            return;

        applicationStopRequested = true;
        Application.RequestStop(window!);
    }

    Exception? FailRootCreation(Exception failure)
    {
        List<AdmittedUiEvent> abandoned = [];
        lock (ingressGate)
        {
            rootCreationFailure = failure;
            while (events.Reader.TryRead(out var admitted))
            {
                pendingEvents--;
                abandoned.Add(admitted);
            }
        }

        return AbandonAll(abandoned, 0);
    }

    static Exception CombineFailure(Exception? failure, Exception next)
        => failure is null ? next : new AggregateException(failure, next);

    void FinishRun()
    {
        WorkspaceHotkey[] hotkeys;
        lock (ingressGate)
        {
            state = StateStopped;
            drainRunning = false;
            drainScheduled = false;
            pendingEvents = 0;
            events.Writer.TryComplete();
            hotkeys = [.. workspaceHotkeys.Values];
            workspaceHotkeys.Clear();
            pendingWorkspaceHotkeys.Clear();
        }

        if (!stopping.IsCancellationRequested)
            stopping.Cancel();
        List<AdmittedUiEvent> abandoned = [];
        while (events.Reader.TryRead(out var admitted))
            abandoned.Add(admitted);
        var abandonFailure = AbandonAll(abandoned, 0);
        foreach (var notification in notifications)
        {
            HotkeyManager.UnregisterNotificationShortcuts(
                notification.ShortcutRegistrationId);
        }
        notifications.Clear();
        globalLogs.Clear();
        workspaceLogs.Clear();
        foreach (var hotkey in hotkeys)
            HotkeyManager.Unregister(hotkey.Key, hotkey.Modifiers, hotkey.Entry);
        ready.TrySetCanceled();
        lifetimeRegistration.Dispose();
        stopping.Dispose();
        if (abandonFailure is not null)
            ExceptionDispatchInfo.Capture(abandonFailure).Throw();
    }

    Exception? AbandonAll(
        IReadOnlyList<AdmittedUiEvent> admittedEvents,
        int startIndex,
        Exception? failure = null,
        List<TaskCompletionSource>? deferredFlushCompletions = null)
    {
        var completionFailure = failure;
        for (var index = admittedEvents.Count - 1; index >= startIndex; index--)
        {
            try
            {
                var uiEvent = admittedEvents[index].MarkAbandoned();
                if (deferredFlushCompletions is not null &&
                    uiEvent is FlushIngress flush)
                {
                    deferredFlushCompletions.Add(flush.Completion);
                    continue;
                }
                Abandon(uiEvent, completionFailure);
            }
            catch (Exception ex)
            {
                failure = CombineFailure(failure, ex);
            }
        }
        return failure;
    }

    void Abandon(IngressEvent uiEvent, Exception? failure)
    {
        switch (uiEvent)
        {
            case NotifyIngress notify:
                HotkeyManager.UnregisterNotificationShortcuts(
                    notify.ShortcutRegistrationId);
                break;
            case NavigateWorkspaceIngress navigate:
                navigate.Completion.TrySetException(failure ?? new InvalidOperationException(
                    "UiHost stopped before the accepted navigation event was applied."));
                break;
            case FlushIngress flush:
                flush.Completion.TrySetException(failure ?? new InvalidOperationException(
                    "UiHost stopped before the accepted flush event was applied."));
                break;
            case BindWorkspaceHotkeyIngress bindHotkey:
                lock (ingressGate)
                    pendingWorkspaceHotkeys.Remove(bindHotkey.Entry);
                HotkeyManager.Unregister(
                    bindHotkey.Key,
                    bindHotkey.Modifiers,
                    bindHotkey.Entry);
                break;
        }
    }

    void DisposeWindow()
    {
        if (window is null)
            return;

        Application.LayoutAndDrawComplete -= ApplicationLayoutAndDrawComplete;
        Application.Keyboard.KeyDown -= ApplicationKeyDown;
        if (commandMode is not null)
            commandMode.VisibleChanged -= CommandModeVisibleChanged;
        window.MouseEvent -= WindowMouseEvent;
        window.KeyDownNotHandled -= WindowKeyDownNotHandled;
        window.ViewportChanged -= WindowViewportChanged;
        window.Initialized -= WindowInitialized;
        window.Dispose();
        window = null;
        workspaceViewport = null;
        workspaceTaskbar = null;
        notificationOverlay = null;
        hotkeyLayer = null;
        commandMode = null;
    }

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

    abstract record IngressEvent;
    sealed record RegisterWorkspaceIngress(
        Workspace Workspace,
        Workspace[] RegistrationOrder,
        Workspace? CurrentWorkspace) : IngressEvent;
    sealed record RemoveWorkspaceIngress(
        Workspace Workspace,
        Workspace? Replacement,
        Workspace[] RegistrationOrder) : IngressEvent;
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
        DateTimeOffset ExpiresAt,
        long ShortcutRegistrationId) : IngressEvent;
    sealed record SwitchWorkspaceIngress(Workspace Workspace) : IngressEvent;
    sealed record BindWorkspaceHotkeyIngress(
        Workspace Workspace,
        ConsoleKey Key,
        ConsoleModifiers Modifiers,
        HotkeyManager.HotkeyEntry Entry) : IngressEvent;
    sealed record NavigateWorkspaceIngress(
        Command Command,
        TaskCompletionSource<bool> Completion) : IngressEvent;
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
        internal IngressEvent Event => uiEvent;

        internal void MarkApplied()
        {
            if (Interlocked.CompareExchange(ref disposition, 1, 0) != 0)
                throw new InvalidOperationException("UiHost ingress event 已处理。");
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
        string ShortcutText,
        HotkeyManager.HotkeyEntry Entry);

    sealed record NotificationState(
        Workspace? Workspace,
        string Text,
        UiSeverity Severity,
        DateTimeOffset ExpiresAt,
        long ShortcutRegistrationId);

    sealed record DrainBatchResult(
        int Count,
        UiChange Changes,
        List<TaskCompletionSource> FlushCompletions,
        bool Shutdown,
        Exception? Failure);

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
