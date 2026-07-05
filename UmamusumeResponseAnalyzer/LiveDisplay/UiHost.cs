using System.Threading.Channels;
using Spectre.Console;
using Spectre.Console.Rendering;
using UmamusumeResponseAnalyzer.Plugin;

namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    public sealed class UiHost : IKeyboardOverlaySink
    {
        const int MaxLogLines = 300;

        readonly Channel<UiEvent> events = CreateUiChannel<UiEvent>();
        readonly UiRefreshSignal refreshSignal = new();
        readonly NotificationPopupRenderer popupRenderer = new();
        readonly WorkspaceLayoutBuilder layoutBuilder = new();

        readonly Dictionary<LiveDisplayWorkspace, WorkspaceState> workspaces = [];
        readonly Dictionary<(LiveDisplayWorkspace Workspace, string PluginId, string Key), LiveDisplayPanel> panels = [];
        readonly Dictionary<(ConsoleKey Key, ConsoleModifiers Modifiers), LiveDisplayWorkspace> workspaceHotkeyOwners = [];
        readonly List<PendingWorkspaceRemoval> pendingWorkspaceRemovals = [];
        readonly object workspaceIdentityGate = new();
        readonly Dictionary<string, LiveDisplayWorkspace> workspaceIdentities = new(StringComparer.OrdinalIgnoreCase);
        readonly object removedWorkspaceGate = new();
        readonly HashSet<LiveDisplayWorkspace> removedWorkspaces = new(ReferenceEqualityComparer.Instance);
        readonly List<LiveDisplayLogLine> logs = [];
        readonly List<LiveDisplayNotification> notifications = [];
        readonly Channel<ConsoleInteractionRequest> consoleInteractions = CreateUiChannel<ConsoleInteractionRequest>();

        LiveDisplayWorkspace? currentWorkspace;
        LiveDisplayWorkspace? activeWorkspace;
        string[] workspaceCompletionTitles = [];
        KeyboardPopup? keyboardPopup;
        KeyboardCommandInput? commandInput;
        bool shutdownRequested;
        int runState;
        internal static bool? HasInteractiveConsoleOverrideForTests { get; set; }
        internal static Action? ClearConsoleOverrideForTests { get; set; }
        internal static Func<CancellationToken, Task>? RunLiveDisplayUntilConsoleInteractionOverrideForTests { get; set; }
        int acceptingEvents = 1;

        public ILiveDisplayOutput ForPlugin(string pluginId) => new PluginLiveDisplayOutput(pluginId, this);
        public LiveDisplayWorkspace? CurrentWorkspace => Volatile.Read(ref currentWorkspace);

        public LiveDisplayWorkspace CreateWorkspace(string title)
        {
            var workspace = LiveDisplayWorkspace.Create(title);
            lock (workspaceIdentityGate)
            {
                if (workspaceIdentities.TryGetValue(workspace.Title, out var existing))
                    return existing;

                workspaceIdentities[workspace.Title] = workspace;
            }

            RegisterWorkspace(workspace);
            return workspace;
        }

        public void RegisterWorkspace(LiveDisplayWorkspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            if (IsRemovedWorkspace(workspace))
                return;

            SetCurrentWorkspaceIfEmpty(workspace);
            Post(new UiEvent.RegisterWorkspace(workspace));
        }

        internal void RemoveWorkspaceWhenAnotherPanelActivates(LiveDisplayWorkspace workspace, Action? removed = null)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            Post(new UiEvent.RemoveWorkspaceWhenAnotherPanelActivates(workspace, removed));
        }

        public void SetPanel(LiveDisplayPanel panel, bool switchToWorkspace = true) => Post(new UiEvent.SetPanel(panel, switchToWorkspace));
        public void Log(LiveDisplayLogLine line) => Post(new UiEvent.Log(line));
        public void Notify(LiveDisplayNotification notification) => Post(new UiEvent.Notify(notification));
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

            var shortcutText = KeyboardManager.FormatKeyCombo(key, modifiers);
            KeyboardManager.Register(
                key,
                modifiers,
                description ?? $"切换到 {workspace.Title}",
                () =>
                {
                    SwitchWorkspace(workspace);
                    return Task.CompletedTask;
            });
            RegisterWorkspace(workspace);
            Post(new UiEvent.SetWorkspaceShortcut(workspace, key, modifiers, shortcutText));
        }

        public void RequestShutdown() => Post(new UiEvent.Shutdown());
        public void HidePopup() => Post(new UiEvent.HidePopup());
        internal Task HandleCommandAsync(string command)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!command.StartsWith('/'))
                return Task.CompletedTask;

            Post(new UiEvent.RunCommand(command));
            return Task.CompletedTask;
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

        void IKeyboardOverlaySink.ShowPopup(KeyboardPopup popup) => Post(new UiEvent.ShowPopup(popup));
        void IKeyboardOverlaySink.ShowCommandInput(KeyboardCommandInput input) => Post(new UiEvent.ShowCommandInput(input));
        void IKeyboardOverlaySink.HideCommandInput() => Post(new UiEvent.HideCommandInput());
        internal bool IsRunning => Volatile.Read(ref runState) != 0;

        internal void RenderSnapshot(IAnsiConsole console)
        {
            if (IsRunning)
                throw new InvalidOperationException("RenderSnapshot 只能在 UiHost 未运行时用于测试或诊断。");

            DrainEvents();
            RemoveExpiredNotifications(DateTimeOffset.Now);
            console.Write(BuildLayout(console.Profile.Width, console.Profile.Height));
        }

        public Task RunAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken, firstRenderGate: null);

        internal async Task RunAsync(CancellationToken cancellationToken, Task? firstRenderGate)
        {
            if (Interlocked.Exchange(ref runState, 1) != 0)
                throw new InvalidOperationException("UiHost.RunAsync 已在运行中。");

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

                    await ExecuteConsoleInteractionAsync(request, clearConsole: true);
                }
            }
            finally
            {
                Volatile.Write(ref runState, 0);
                Volatile.Write(ref acceptingEvents, 0);
                events.Writer.TryComplete();
                consoleInteractions.Writer.TryComplete();
                FailPendingConsoleInteractions();
            }
        }

        async Task DrainConsoleInteractionsBeforeFirstRenderAsync(Task firstRenderGate, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !shutdownRequested)
            {
                while (consoleInteractions.Reader.TryRead(out var interaction))
                    await ExecuteConsoleInteractionAsync(interaction, clearConsole: true);

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

            ConsoleInteractionRequest? pendingInteraction = null;
            var width = GetConsoleWidth();
            var height = GetConsoleHeight();
            await AnsiConsole.Live(BuildLayout(width, height))
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!cancellationToken.IsCancellationRequested && !shutdownRequested)
                    {
                        if (consoleInteractions.Reader.TryRead(out pendingInteraction))
                            break;

                        var changed = DrainEvents();
                        var now = DateTimeOffset.Now;
                        if (RemoveExpiredNotifications(now))
                            changed = true;
                        if (popupRenderer.ShouldRefreshPopupCountdown(notifications, keyboardPopup, now))
                            changed = true;

                        var currentWidth = GetConsoleWidth();
                        var currentHeight = GetConsoleHeight();
                        if (currentWidth != width || currentHeight != height)
                        {
                            width = currentWidth;
                            height = currentHeight;
                            changed = true;
                        }

                        if (changed)
                            ctx.UpdateTarget(BuildLayout(width, height));

                        try
                        {
                            await refreshSignal.WaitForAsync(cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                });

            return pendingInteraction;
        }

        async Task RunHeadlessAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !shutdownRequested)
            {
                DrainEvents();
                var now = DateTimeOffset.Now;
                RemoveExpiredNotifications(now);
                while (consoleInteractions.Reader.TryRead(out var interaction))
                    await ExecuteConsoleInteractionAsync(interaction, clearConsole: false);

                try { await refreshSignal.WaitForAsync(cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        internal Task RunConsoleInteractionAsync(Func<Task> action)
        {
            if (!IsRunning)
                return action();

            var request = new ConsoleInteractionRequest(action);
            if (!consoleInteractions.Writer.TryWrite(request))
                throw new InvalidOperationException("Console interaction queue is closed.");

            refreshSignal.Signal();
            return request.Completion;
        }

        static async Task ExecuteConsoleInteractionAsync(ConsoleInteractionRequest request, bool clearConsole)
        {
            using var inputSuspension = KeyboardManager.SuspendInput();
            using var interactionScope = LiveDisplayConsole.EnterConsoleInteraction();
            try
            {
                if (clearConsole)
                    ClearConsole();

                await request.Action();
                request.SetResult();
            }
            catch (Exception ex)
            {
                request.SetException(ex);
            }
            finally
            {
                if (clearConsole)
                    ClearConsole();
            }
        }

        static void ClearConsole() => (ClearConsoleOverrideForTests ?? AnsiConsole.Clear)();

        void FailPendingConsoleInteractions()
        {
            while (consoleInteractions.Reader.TryRead(out var interaction))
                interaction.SetException(new OperationCanceledException("LiveDisplay 已停止，无法执行 console interaction。"));
        }

        void Post(UiEvent uiEvent)
        {
            if (Volatile.Read(ref acceptingEvents) == 0)
                return;

            if (!events.Writer.TryWrite(uiEvent))
                return;

            refreshSignal.Signal();
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
                    RegisterKnownWorkspace(registerWorkspace.Workspace);
                    break;
                case UiEvent.SetWorkspaceShortcut setWorkspaceShortcut:
                    SetWorkspaceShortcut(
                        setWorkspaceShortcut.Workspace,
                        setWorkspaceShortcut.Key,
                        setWorkspaceShortcut.Modifiers,
                        setWorkspaceShortcut.ShortcutText);
                    break;
                case UiEvent.RemoveWorkspaceWhenAnotherPanelActivates removeWorkspace:
                    ArmWorkspaceRemovalWhenAnotherPanelActivates(removeWorkspace.Workspace, removeWorkspace.Removed);
                    break;
                case UiEvent.SetPanel setPanel:
                    if (IsRemovedWorkspace(setPanel.Panel.Workspace))
                        break;

                    RegisterKnownWorkspace(setPanel.Panel.Workspace);
                    panels[(setPanel.Panel.Workspace, setPanel.Panel.PluginId, setPanel.Panel.Key)] = setPanel.Panel;
                    if (setPanel.SwitchToWorkspace)
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
                        RegisterKnownWorkspace(log.Line.Workspace);
                    logs.Add(log.Line);
                    if (logs.Count > MaxLogLines)
                        logs.RemoveRange(0, logs.Count - MaxLogLines);
                    break;
                case UiEvent.Notify notify:
                    if (notify.Notification.Workspace is not null && IsRemovedWorkspace(notify.Notification.Workspace))
                        break;

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
                    keyboardPopup = showPopup.Popup;
                    break;
                case UiEvent.HidePopup:
                    keyboardPopup = null;
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

        IRenderable BuildLayout(int width, int height)
        {
            if (width <= 0)
                width = 120;
            if (height <= 0)
                height = 35;

            IRenderable content = layoutBuilder.BuildWorkspaceLayout(
                new WorkspaceLayoutBuilder.State(activeWorkspace, panels.Values, logs, WorkspaceLabel),
                width,
                height);
            var popupWidth = NotificationPopupRenderer.GetPopupWidth(width);
            var now = DateTimeOffset.Now;
            if (popupWidth > 0)
            {
                var activeNotifications = GetActiveNotifications();
                if (activeNotifications.Count > 0)
                {
                    content = new NotificationOverlayRenderable(
                        content,
                        popupRenderer.BuildLines(activeNotifications, popupWidth, Math.Max(0, height - 1), now, WorkspaceLabel),
                        popupWidth);
                }
            }

            if (keyboardPopup is not null)
                content = new KeyboardPopupOverlayRenderable(content, keyboardPopup, width, height, bottomInset: 0, now: now);

            if (commandInput is not null)
                content = new KeyboardCommandInputOverlayRenderable(content, commandInput, width, height);

            return content;
        }

        List<LiveDisplayNotification> GetActiveNotifications()
        {
            return notifications
                .OrderByDescending(x => x.ExpiresAt)
                .ToList();
        }

        internal IReadOnlyList<string> BuildNotificationPopupPreview(int width)
        {
            DrainEvents();
            var now = DateTimeOffset.Now;
            RemoveExpiredNotifications(now);
            var activeNotifications = GetActiveNotifications();
            var popupWidth = NotificationPopupRenderer.GetPopupWidth(width);
            if (popupWidth == 0 || activeNotifications.Count == 0)
                return [];

            return popupRenderer.BuildLines(activeNotifications, popupWidth, int.MaxValue, now, WorkspaceLabel);
        }

        string WorkspaceLabel(LiveDisplayWorkspace workspace)
        {
            return workspaces.TryGetValue(workspace, out var state) && !string.IsNullOrEmpty(state.ShortcutText)
                ? $"{state.ShortcutText} {workspace.Title}"
                : workspace.Title;
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
                    ShowWorkspaceList();
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

        void ShowWorkspaceList()
        {
            KeyboardManager.ShowPopup(BuildWorkspaceList().Context);
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
            title = title.Trim();
            if (string.IsNullOrEmpty(title))
            {
                LogWorkspaceUsage();
                return;
            }

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
            LogCommandWarning("用法: /workspace | /workspace switch [<title>] | /workspace list");
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
            ShowPluginLifecyclePopup(status.InternalName, action);
            LogCommand($"插件 {status.InternalName} 已{action}。", LiveDisplaySeverity.Success);
        }

        void ShowPluginLifecyclePopup(string internalName, string action)
        {
            var context = new KeyboardHandlerContext()
                .WriteLine("Plugin command")
                .WriteLine($"{internalName} 已{action}。", ConsoleColor.Green);
            KeyboardManager.ShowPopup(context);
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
            logs.Add(new LiveDisplayLogLine(null, "Command", text, severity, IsMarkup: false, DateTimeOffset.Now));
            if (logs.Count > MaxLogLines)
                logs.RemoveRange(0, logs.Count - MaxLogLines);
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
            return notifications.RemoveAll(x => x.ExpiresAt <= now) > 0;
        }

        void RegisterKnownWorkspace(LiveDisplayWorkspace workspace)
        {
            ArgumentNullException.ThrowIfNull(workspace);
            if (IsRemovedWorkspace(workspace))
                return;

            TrackWorkspaceIdentity(workspace);
            if (workspaces.ContainsKey(workspace))
            {
                if (activeWorkspace is null)
                {
                    activeWorkspace = workspace;
                    SetCurrentWorkspaceIfEmpty(workspace);
                }
                return;
            }

            workspaces[workspace] = new WorkspaceState(ShortcutText: null, Hotkey: null);
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
            string shortcutText)
        {
            RegisterKnownWorkspace(workspace);
            var state = workspaces[workspace];
            var hotkey = (key, modifiers);
            workspaceHotkeyOwners[hotkey] = workspace;
            workspaces[workspace] = state with { ShortcutText = shortcutText, Hotkey = hotkey };
        }

        void ArmWorkspaceRemovalWhenAnotherPanelActivates(LiveDisplayWorkspace workspace, Action? removed)
        {
            if (pendingWorkspaceRemovals.Any(x => Equals(x.Workspace, workspace)))
                return;

            pendingWorkspaceRemovals.Add(new(workspace, removed));
        }

        void RemovePendingWorkspacesAfterPanelActivation(LiveDisplayWorkspace activatedWorkspace)
        {
            foreach (var pending in pendingWorkspaceRemovals.ToList())
            {
                if (Equals(pending.Workspace, activatedWorkspace))
                    continue;

                RemoveWorkspace(pending.Workspace, activatedWorkspace, pending.Removed);
                pendingWorkspaceRemovals.Remove(pending);
            }
        }

        void RemoveWorkspace(LiveDisplayWorkspace workspace, LiveDisplayWorkspace? replacement, Action? removed)
        {
            MarkWorkspaceRemoved(workspace);
            RemoveWorkspaceIdentity(workspace);
            if (!workspaces.Remove(workspace, out var state))
            {
                removed?.Invoke();
                return;
            }

            UnbindWorkspaceHotkey(workspace, state);
            foreach (var key in panels.Keys.Where(x => x.Workspace == workspace).ToList())
                panels.Remove(key);
            logs.RemoveAll(x => x.Workspace == workspace);
            notifications.RemoveAll(x => x.Workspace == workspace);

            if (activeWorkspace == workspace)
                activeWorkspace = replacement is not null && workspaces.ContainsKey(replacement)
                    ? replacement
                    : workspaces.Keys.FirstOrDefault();

            if (CurrentWorkspace == workspace)
                Volatile.Write(ref currentWorkspace, activeWorkspace);

            RefreshWorkspaceCompletionTitles();
            removed?.Invoke();
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
                ? CompleteByPrefix(
                    $"/workspace switch {arguments}",
                    Volatile.Read(ref workspaceCompletionTitles).Select(x => $"/workspace switch {x}"))
                : [];
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

        void MarkWorkspaceRemoved(LiveDisplayWorkspace workspace)
        {
            lock (removedWorkspaceGate)
                removedWorkspaces.Add(workspace);
        }

        void UnbindWorkspaceHotkey(LiveDisplayWorkspace workspace, WorkspaceState state)
        {
            if (state.Hotkey is not { } hotkey)
                return;

            if (!workspaceHotkeyOwners.TryGetValue(hotkey, out var owner) || owner != workspace)
                return;

            KeyboardManager.Unregister(hotkey.Key, hotkey.Modifiers);
            workspaceHotkeyOwners.Remove(hotkey);
        }

        void TrackWorkspaceIdentity(LiveDisplayWorkspace workspace)
        {
            lock (workspaceIdentityGate)
            {
                workspaceIdentities.TryAdd(workspace.Title, workspace);
            }
        }

        void RemoveWorkspaceIdentity(LiveDisplayWorkspace workspace)
        {
            lock (workspaceIdentityGate)
            {
                if (workspaceIdentities.TryGetValue(workspace.Title, out var existing) && ReferenceEquals(existing, workspace))
                    workspaceIdentities.Remove(workspace.Title);
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

        sealed record WorkspaceState(string? ShortcutText, (ConsoleKey Key, ConsoleModifiers Modifiers)? Hotkey);

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
