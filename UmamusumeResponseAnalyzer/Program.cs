using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Views;
using UmamusumeResponseAnalyzer.LiveDisplay;
using UmamusumeResponseAnalyzer.Plugin;
using configI18n = UmamusumeResponseAnalyzer.Localization.Config;
using static UmamusumeResponseAnalyzer.Localization.LaunchMenu;

namespace UmamusumeResponseAnalyzer
{
    public static class UmamusumeResponseAnalyzer
    {
        internal static Task _database_initialize_task = null!;
        internal static Task _plugin_initialize_task = null!;
        public static bool Started => Server.IsRunning;
        const string PORTABLE_WORKING_DIRECTORY = "./.portable";
        public readonly static string WORKING_DIRECTORY = Directory.Exists(PORTABLE_WORKING_DIRECTORY) ? PORTABLE_WORKING_DIRECTORY : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UmamusumeResponseAnalyzer");
        public static async Task Main(string[] args)
        {
            if (args is ["-v" or "--version"])
            {
                Console.Write(Assembly.GetExecutingAssembly().GetName().Version);
                return;
            }

            Console.Title = $"UmamusumeResponseAnalyzer v{Assembly.GetExecutingAssembly().GetName().Version}";
            Console.OutputEncoding = Encoding.UTF8;
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_DISABLEIPV6", "true");
            if (!Directory.Exists(WORKING_DIRECTORY)) Directory.CreateDirectory(WORKING_DIRECTORY);
            Directory.SetCurrentDirectory(WORKING_DIRECTORY);
            if (await TryHandleCliOnlyArgumentsAsync(args))
                return;

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(
                    "无法启动交互界面：stdin 或 stdout 已被重定向。请在 Windows Terminal 等交互式终端中直接运行 URA。");
                Environment.ExitCode = 1;
                return;
            }

            try
            {
                await RunInteractiveOnDedicatedThreadAsync();
            }
            catch (PostShutdownProcessRequestedException ex)
            {
                Process.Start(ex.StartInfo);
            }
        }

        static Task RunInteractiveOnDedicatedThreadAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var uiThread = new Thread(() =>
            {
                var previousContext = SynchronizationContext.Current;
                IApplication? application = null;
                SingleThreadSynchronizationContext? context = null;
                Exception? failure = null;
                try
                {
                    Application.MaximumIterationsPerSecond = 50;
                    application = Application.Create();
                    application.Init();
                    context = new SingleThreadSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(context);
                    context.Bind(application);
                    var workflow = RunInteractiveAsync(application, context);
                    try
                    {
                        context.Run(workflow);
                    }
                    finally
                    {
                        if (workflow.IsCompleted)
                            application = null;
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    try
                    {
                        application?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                    }
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                    try
                    {
                        context?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                    }
                }
                if (failure is null)
                    completion.TrySetResult();
                else
                    completion.TrySetException(failure);
            })
            {
                IsBackground = false,
                Name = "Terminal.Gui UI"
            };
            uiThread.Start();
            return completion.Task;
        }

        static async Task RunInteractiveAsync(
            IApplication application,
            SingleThreadSynchronizationContext synchronizationContext)
        {
            var lifetimeCts = new CancellationTokenSource();
            UiHost? uiHost = null;
            BootstrapWorkspace? bootstrap = null;
            ShutdownCommandTarget? shutdownTarget = null;
            var pluginInitialization = Task.CompletedTask;
            var pluginUpdateCheck = Task.CompletedTask;
            var shutdownBindingAdded = false;
            var liveDisplayBound = false;
            ExceptionDispatchInfo? workflowFailure = null;
            try
            {
                uiHost = new(application);
                bootstrap = new(uiHost);
                shutdownTarget = new(() =>
                {
                    lifetimeCts.Cancel();
                    uiHost.RequestShutdown();
                    if (application.TopRunnableView is not null)
                        application.RequestStop();
                });
                application.Keyboard.KeyBindings.AddApp(
                    Terminal.Gui.Input.Key.C.WithCtrl,
                    shutdownTarget,
                    Terminal.Gui.Input.Command.Quit);
                shutdownBindingAdded = true;
                LiveDisplayConsole.Bind(uiHost, application, lifetimeCts.Token);
                liveDisplayBound = true;
                LiveDisplayConsole.DefaultLogWorkspace = bootstrap.Workspace;
                KeyboardManager.OverlaySink = uiHost;
                PluginManager.BindLiveDisplay(application, plugin => uiHost.ForPlugin(plugin.Name));

                Config.Initialize();
                await ResourceUpdater.TryUpdateProgram(cancellationToken: lifetimeCts.Token);
                if (Config.Core.ShowFirstRunPrompt)
                {
                    try
                    {
                        ShowFirstLaunchPrompt(lifetimeCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    Config.Core.ShowFirstRunPrompt = false;
                    Config.Save();
                }

                    bootstrap.SetSettings(
                    [
                        ("版本", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"),
                        ("工作目录", Directory.GetCurrentDirectory()),
                        ("监听", $"http://{Config.Core.ListenAddress}:{Config.Core.ListenPort}"),
                        ("服务器目标", Config.Repository.Targets.Count == 0 ? "未限制" : string.Join(", ", Config.Repository.Targets)),
                        ("数据语言", Config.Updater.DatabaseLanguage),
                        ("训练员性别", Config.Updater.TrainerIsMale ? "男" : "女")
                    ]);
                    bootstrap.SetPhase("config", "配置", LiveDisplaySeverity.Success, "已读取 config.yaml");

                    _plugin_initialize_task = pluginInitialization = StartPluginInitializationAsync(bootstrap);
                    await _plugin_initialize_task;
                    try
                    {
                        await ShowMenu(application, lifetimeCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    bootstrap.SetPhase("database", "数据文件", LiveDisplaySeverity.Info, "正在加载事件、技能、名称等数据。");
                    _database_initialize_task = Database.Initialize();
                    await Task.WhenAll(_database_initialize_task, _plugin_initialize_task);
                    bootstrap.SetPhase("database", "数据文件", LiveDisplaySeverity.Success, "加载完成；缺失或损坏项见日志。");

                    bootstrap.SetPhase("plugin-init", "插件初始化", LiveDisplaySeverity.Info, "正在调用插件 Initialize。");
                    PluginManager.InitializeLoadedPlugins();
                    var loadedPluginCount = PluginManager.LoadedPlugins.Count;
                    var failedPluginCount = PluginManager.FailedPlugins.Count;
                    bootstrap.SetPhase(
                        "plugin-init",
                        "插件初始化",
                        failedPluginCount == 0 ? LiveDisplaySeverity.Success : LiveDisplaySeverity.Warning,
                        failedPluginCount == 0
                            ? $"已初始化 {loadedPluginCount} 个插件。"
                            : $"已初始化 {loadedPluginCount} 个插件，{failedPluginCount} 个插件失败。");

                    KeyboardManager.Register(ConsoleKey.P, "插件列表", ctx =>
                    {
                        var plugins = PluginManager.SnapshotLoadedPlugins();
                        foreach (var i in plugins)
                            ctx.WriteLine($"{i.Name} v{i.Version}  by {i.Author}");
                        if (plugins.Count == 0)
                            ctx.WriteLine("（没有加载任何插件）", ConsoleColor.DarkGray);
                        return Task.CompletedTask;
                    });
                    KeyboardManager.SetCommandHandler(uiHost.HandleCommandAsync, uiHost.CompleteCommand);

                    bootstrap.SetPhase("server", "HTTP server", LiveDisplaySeverity.Info, "正在启动监听。");
                    try
                    {
                        Server.Start(lifetimeCts.Token); //启动HTTP服务器
                        bootstrap.SetPhase("server", "HTTP server", LiveDisplaySeverity.Success, $"监听 http://{Config.Core.ListenAddress}:{Config.Core.ListenPort}");
                    }
                    catch (Exception ex)
                    {
                        bootstrap.SetPhase("server", "HTTP server", LiveDisplaySeverity.Error, ex.Message);
                        throw;
                    }

                    bootstrap.Log(
                        "Plugin",
                        loadedPluginCount == 0
                            ? "没有加载任何插件。可从插件仓库安装插件。"
                            : $"已加载 {loadedPluginCount} 个插件。按 P 查看插件列表。",
                        loadedPluginCount == 0 ? LiveDisplaySeverity.Warning : LiveDisplaySeverity.Success);
                    foreach (var plugin in PluginManager.FailedPlugins)
                    {
                        var message = $"插件 {Path.GetFileName(plugin)} 加载失败";
                        bootstrap.Log("Plugin", message, LiveDisplaySeverity.Warning);
                    }

                    bootstrap.Log("Server", $"监听 http://{Config.Core.ListenAddress}:{Config.Core.ListenPort}", LiveDisplaySeverity.Success);
                    if (Config.Core.ListenAddress == "0.0.0.0")
                    {
                        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                               .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                               .SelectMany(x => x.GetIPProperties().UnicastAddresses)
                               .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                               .Select(x => x.Address.ToString());
                        foreach (var i in interfaces)
                        {
                            bootstrap.Log("Server", string.Format(Localization.Server.I18N_AvailableEndpointTip, i, Config.Core.ListenPort));
                        }
                    }

                    for (var i = 0; i < 30; i++)
                    {
                        if (Server.IsRunning) break;
                        await Task.Delay(100, lifetimeCts.Token);
                    }
                    if (!Server.IsRunning)
                    {
                        bootstrap.SetPhase("server", "HTTP server", LiveDisplaySeverity.Error, I18N_LaunchFail);
                        Console.Error.WriteLine(I18N_LaunchFail);
                        Environment.ExitCode = 1;
                        return;
                    }

                    var startedMessage = I18N_Start_Started;
                    bootstrap.Log("URA", startedMessage, LiveDisplaySeverity.Success);
                    bootstrap.SetPhase("started", "宿主", LiveDisplaySeverity.Success, startedMessage);

                    await PluginManager.TriggerStartedAsync(lifetimeCts.Token);
                    pluginUpdateCheck = CheckPluginUpdatesAsync(uiHost, lifetimeCts.Token);

                try
                {
                    await uiHost.RunAsync(lifetimeCts.Token);
                }
                catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested) { }
            }
            catch (Exception ex)
            {
                workflowFailure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                var serverShutdown = Task.CompletedTask;
                await RunCleanupAsync(
                    workflowFailure,
                    [
                        () =>
                        {
                            lifetimeCts.Cancel();
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            uiHost?.RequestShutdown();
                            return ValueTask.CompletedTask;
                        },
                        async () =>
                        {
                            try
                            {
                                await pluginUpdateCheck;
                            }
                            catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
                            {
                            }
                        },
                        async () =>
                        {
                            try
                            {
                                await pluginInitialization;
                            }
                            catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
                            {
                            }
                        },
                        () =>
                        {
                            serverShutdown = Server.StopAsync();
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            KeyboardManager.UnregisterAll();
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            KeyboardManager.OverlaySink = null;
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            KeyboardManager.SetCommandHandler(null);
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            if (liveDisplayBound)
                                LiveDisplayConsole.Unbind(uiHost!);
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            if (shutdownBindingAdded)
                                application.Keyboard.KeyBindings.Remove(Terminal.Gui.Input.Key.C.WithCtrl);
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            synchronizationContext.Unbind(application);
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            shutdownTarget?.Dispose();
                            return ValueTask.CompletedTask;
                        },
                        () =>
                        {
                            application.Dispose();
                            return ValueTask.CompletedTask;
                        },
                        async () => await serverShutdown,
                        async () => await PluginManager.ShutdownAsync(),
                        () =>
                        {
                            lifetimeCts.Dispose();
                            return ValueTask.CompletedTask;
                        },
                    ]);
            }
        }

        internal static async Task RunCleanupAsync(
            ExceptionDispatchInfo? workflowFailure,
            IReadOnlyList<Func<ValueTask>> cleanupActions)
        {
            List<Exception>? cleanupFailures = null;
            foreach (var cleanup in cleanupActions)
            {
                try
                {
                    await cleanup();
                }
                catch (Exception ex)
                {
                    (cleanupFailures ??= []).Add(ex);
                }
            }

            workflowFailure?.Throw();
            if (cleanupFailures is [var cleanupFailure])
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            if (cleanupFailures is { Count: > 1 })
                throw new AggregateException("Host cleanup 失败。", cleanupFailures);
        }

        static Task StartPluginInitializationAsync(BootstrapWorkspace bootstrap)
        {
            return Task.Run(() =>
            {
                bootstrap.SetPhase("plugin-scan", "插件扫描", LiveDisplaySeverity.Info, "正在扫描 Plugins/。");
                try
                {
                    PluginManager.Init();
                }
                catch (Exception ex)
                {
                    bootstrap.SetPhase("plugin-scan", "插件扫描", LiveDisplaySeverity.Error, ex.Message);
                    LiveDisplayConsole.LogException("Plugin", ex);
                    throw;
                }

                var loadedPluginCount = PluginManager.LoadedPlugins.Count;
                var failedPluginCount = PluginManager.FailedPlugins.Count;
                bootstrap.SetPhase(
                    "plugin-scan",
                    "插件扫描",
                    failedPluginCount == 0 ? LiveDisplaySeverity.Success : LiveDisplaySeverity.Warning,
                    failedPluginCount == 0
                        ? $"发现 {loadedPluginCount} 个可用插件。"
                        : $"发现 {loadedPluginCount} 个可用插件，{failedPluginCount} 个插件失败。");
            });
        }

        static async Task CheckPluginUpdatesAsync(UiHost uiHost, CancellationToken cancellationToken)
        {
            try
            {
                var updates = await PluginRepository.CheckForUpdatesAsync(cancellationToken);
                if (updates.Count == 0)
                    return;

                uiHost.Notify(new LiveDisplayNotification(
                    Workspace: null,
                    "URA",
                    FormatPluginUpdateNotification(updates),
                    LiveDisplaySeverity.Info,
                    DateTimeOffset.Now.AddSeconds(12),
                    []));

                foreach (var update in updates)
                {
                    uiHost.Log(new LiveDisplayLogLine(
                        Workspace: null,
                        "URA",
                        $"插件 {update.DisplayName} 有新版本可用: {update.CurrentVersion} -> {update.LatestVersion}",
                        LiveDisplaySeverity.Info));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                uiHost.Notify(new LiveDisplayNotification(
                    Workspace: null,
                    "URA",
                    $"插件更新检查失败: {ex.Message}",
                    LiveDisplaySeverity.Warning,
                    LiveDisplayNotification.ExpiresAtFromNow(LiveDisplaySeverity.Warning),
                    []));
            }
        }

        static string FormatPluginUpdateNotification(IReadOnlyList<PluginUpdateInfo> updates)
        {
            if (updates.Count == 1)
            {
                var update = updates[0];
                return $"插件 {update.DisplayName} 有新版本: {update.CurrentVersion} -> {update.LatestVersion}";
            }

            var names = string.Join("、", updates.Take(3).Select(x => x.DisplayName));
            var more = updates.Count > 3 ? " 等" : string.Empty;
            return $"{updates.Count} 个插件可更新：{names}{more}。到「插件仓库」菜单里手动安装。";
        }

        static void ShowFirstLaunchPrompt(CancellationToken cancellationToken)
        {
            var mobileOrPc = LiveDisplayConsole.Select(
                "首次设置：请选择运行 UM:PD 的设备。推荐使用 Windows Terminal，并将启动大小设置为 120 列、35 行。",
                new[] { "手机/模拟器以及此计算机", "此计算机" },
                cancellationToken: cancellationToken);
            string networkNotice;
            if (mobileOrPc == "手机/模拟器以及此计算机")
            {
                Config.Core.ListenAddress = "0.0.0.0";
                networkNotice = "URA 将接受其它设备的请求；首次监听时请允许 Windows 防火墙放行。";
            }
            else
            {
                networkNotice = "URA 将仅接受本机请求；模拟器接入时需在「选项 → 核心」改为 0.0.0.0 并放行防火墙。";
            }

            var targets = LiveDisplayConsole.MultiSelect(
                $"{networkNotice} 请选择所使用的 UM:PD 版本。",
                new[] { "日服(Cygames)", "繁中服(Komoe)" },
                cancellationToken: cancellationToken);
            foreach (var target in targets)
            {
                switch (target)
                {
                    case "日服(Cygames)":
                        Config.Repository.Targets.Add("Cygames");
                        break;
                    case "繁中服(Komoe)":
                        Config.Repository.Targets.Add("Komoe");
                        break;
                }
            }

            var dbLang = LiveDisplayConsole.Select(
                "请选择事件数据语言，选择繁中等将会使用对应客户端已实装的内容翻译。不会影响实际效果及数据库总大小。",
                new[] { "日文", "繁中" },
                cancellationToken: cancellationToken);
            Config.Updater.DatabaseLanguage = dbLang == "繁中" ? "zh-TW" : "ja-JP";

            var trainerGender = LiveDisplayConsole.Select(
                "请选择训练员性别，用于精确显示事件选项。",
                new[] { "男", "女" },
                cancellationToken: cancellationToken);
            Config.Updater.TrainerIsMale = trainerGender == "男";

            LiveDisplayConsole.Acknowledge(
                "首次设置完成。启动前请更新数据文件，并从「插件仓库」安装所需插件。",
                cancellationToken);
        }
        enum MenuLocation
        {
            Root,
            Options,
            Core,
            Repository,
            Plugin,
            Updater,
            DatabaseLanguage,
            Language,
            InstallUraCore
        }

        static async Task ShowMenu(
            IApplication application,
            CancellationToken cancellationToken)
        {
            var location = MenuLocation.Root;
            while (true)
            {
                Func<Task>? nextAction = null;
                var start = false;

                MenuItem Leaf(string title, MenuLocation reopenAt, Func<Task> action) => new()
                {
                    Title = title,
                    Action = () =>
                    {
                        location = reopenAt;
                        nextAction = action;
                    }
                };

                MenuItem Toggle(
                    string title,
                    bool value,
                    MenuLocation reopenAt,
                    Action<bool> update)
                {
                    var checkBox = new CheckBox
                    {
                        Title = title,
                        CanFocus = false,
                        Value = value ? CheckState.Checked : CheckState.UnChecked
                    };
                    return new MenuItem
                    {
                        Title = title,
                        CommandView = checkBox,
                        Action = () =>
                        {
                            var selected = checkBox.Value == CheckState.Checked;
                            location = reopenAt;
                            nextAction = () =>
                            {
                                update(selected);
                                Config.Save();
                                return Task.CompletedTask;
                            };
                        }
                    };
                }

                static MenuItem Branch(string title, params MenuItem[] items) => new()
                {
                    Title = title,
                    SubMenu = new Menu(items),
                    Action = null
                };

                static string Label(string resourceName, string fallback) =>
                    configI18n.ResourceManager.GetString(resourceName, configI18n.Culture) ?? fallback;

                Task EditListenAddress()
                {
                    while (true)
                    {
                        var address = LiveDisplayConsole.Ask(
                            configI18n.Tabs_Core_ListenAddressPrompt,
                            Config.Core.ListenAddress,
                            cancellationToken: cancellationToken);
                        if (!IPAddress.TryParse(address, out _))
                            continue;

                        Config.Core.ListenAddress = address;
                        Config.Save();
                        return Task.CompletedTask;
                    }
                }

                Task EditListenPort()
                {
                    while (true)
                    {
                        var port = LiveDisplayConsole.Ask(
                            configI18n.Tabs_Core_ListenPortPrompt,
                            Config.Core.ListenPort.ToString(),
                            cancellationToken: cancellationToken);
                        if (!int.TryParse(port, out var parsed))
                            continue;

                        Config.Core.ListenPort = parsed;
                        Config.Save();
                        return Task.CompletedTask;
                    }
                }

                Task EditTargets()
                {
                    var input = LiveDisplayConsole.Ask(
                        configI18n.Tabs_Repository_TargetsPrompt,
                        string.Join(',', Config.Repository.Targets),
                        allowEmpty: true,
                        cancellationToken: cancellationToken);
                    Config.Repository.Targets = string.IsNullOrEmpty(input)
                        ? []
                        : [.. input.Replace('，', ',').Split(',')];
                    Config.Save();
                    return Task.CompletedTask;
                }

                Task EditCustomDatabaseRepository()
                {
                    while (true)
                    {
                        var url = LiveDisplayConsole.Ask(
                            configI18n.Tabs_Updater_CustomDatabaseRepositoryPrompt,
                            Config.Updater.CustomDatabaseRepository,
                            allowEmpty: true,
                            cancellationToken: cancellationToken);
                        if (!string.IsNullOrEmpty(url) &&
                            !Uri.TryCreate(url, UriKind.Absolute, out _))
                            continue;

                        Config.Updater.CustomDatabaseRepository = url;
                        Config.Save();
                        return Task.CompletedTask;
                    }
                }

                var listenAddressItem = Leaf(
                    $"{configI18n.Tabs_Core_ListenAddress}: {Config.Core.ListenAddress}",
                    MenuLocation.Core,
                    EditListenAddress);
                var listenPortItem = Leaf(
                    $"{configI18n.Tabs_Core_ListenPort}: {Config.Core.ListenPort}",
                    MenuLocation.Core,
                    EditListenPort);
                var firstRunItem = Toggle(
                    Label("Tabs_Core_ShowFirstRunPrompt", nameof(CoreConfig.ShowFirstRunPrompt)),
                    Config.Core.ShowFirstRunPrompt,
                    MenuLocation.Core,
                    value => Config.Core.ShowFirstRunPrompt = value);
                var coreItem = Branch(
                    configI18n.Tabs_Core_Title,
                    listenAddressItem,
                    listenPortItem,
                    firstRunItem);

                var targetsItem = Leaf(
                    $"{configI18n.Tabs_Repository_Targets}: {string.Join(',', Config.Repository.Targets)}",
                    MenuLocation.Repository,
                    EditTargets);
                var repositoryItem = Branch(configI18n.Tabs_Repository_Title, targetsItem);

                var pluginChoices = PluginConfig.BuildPluginChoices(PluginManager.SnapshotLoadedPlugins());
                var pluginItems = pluginChoices
                    .Select(pair => Leaf(
                        pair.Key,
                        MenuLocation.Plugin,
                        () => PluginConfigPrompt.RunAsync(pair.Value, cancellationToken)))
                    .ToArray();
                MenuItem? firstPluginItem = pluginItems.FirstOrDefault();
                if (pluginItems.Length == 0)
                {
                    pluginItems =
                    [
                        new MenuItem
                        {
                            Title = "（没有可配置的插件）",
                            Enabled = false
                        }
                    ];
                }
                var pluginItem = Branch(configI18n.Tabs_Plugin_Title, pluginItems);

                var trainerGenderItem = Toggle(
                    Label("Tabs_Updater_TrainerIsMale", nameof(UpdaterConfig.TrainerIsMale)),
                    Config.Updater.TrainerIsMale,
                    MenuLocation.Updater,
                    value => Config.Updater.TrainerIsMale = value);
                var databaseLanguageItems = new[] { "ja-JP", "zh-TW", "zh-CN" }
                    .Select(language => Leaf(
                        language,
                        MenuLocation.DatabaseLanguage,
                        () =>
                        {
                            Config.Updater.DatabaseLanguage = language;
                            Config.Save();
                            return Task.CompletedTask;
                        }))
                    .ToArray();
                var databaseLanguageItem = Branch(
                    $"{nameof(UpdaterConfig.DatabaseLanguage)}: {Config.Updater.DatabaseLanguage}",
                    databaseLanguageItems);
                var customDatabaseRepositoryItem = Leaf(
                    $"{nameof(UpdaterConfig.CustomDatabaseRepository)}: {Config.Updater.CustomDatabaseRepository}",
                    MenuLocation.Updater,
                    EditCustomDatabaseRepository);
                var forceGithubItem = Toggle(
                    configI18n.Tabs_Updater_ForceUseGithubToUpdate,
                    Config.Updater.ForceUseGithubToUpdate,
                    MenuLocation.Updater,
                    value => Config.Updater.ForceUseGithubToUpdate = value);
                var updaterItem = Branch(
                    configI18n.Tabs_Updater_Title,
                    trainerGenderItem,
                    databaseLanguageItem,
                    customDatabaseRepositoryItem,
                    forceGithubItem);

                var languageItems = Enum.GetValues<LanguageConfig.Language>()
                    .Select(language => Leaf(
                        Label($"Tabs_Language_{language}", language.ToString()),
                        MenuLocation.Language,
                        () =>
                        {
                            Config.Language.Selected = language;
                            Config.Save();
                            Restart();
                            return Task.CompletedTask;
                        }))
                    .ToArray();
                var languageItem = Branch(configI18n.Tabs_Language_Title, languageItems);

                var miscItem = Leaf(
                    configI18n.Tabs_Misc_Title,
                    MenuLocation.Options,
                    () =>
                    {
                        Config.Misc.Prompt(cancellationToken);
                        return Task.CompletedTask;
                    });
                var optionsItem = Branch(
                    I18N_Options,
                    coreItem,
                    repositoryItem,
                    pluginItem,
                    updaterItem,
                    languageItem,
                    miscItem);

                var startItem = Leaf(
                    I18N_Start,
                    MenuLocation.Root,
                    () =>
                    {
                        start = true;
                        return Task.CompletedTask;
                    });
                var mainItems = new List<MenuItem>
                {
                    startItem,
                    optionsItem,
                    Leaf(
                        "插件仓库",
                        MenuLocation.Root,
                        () => PluginRepository.ShowMenuAsync(cancellationToken)),
                    Leaf(
                        I18N_UpdateAssets,
                        MenuLocation.Root,
                        () => ResourceUpdater.UpdateAssets(cancellationToken)),
                    Leaf(
                        I18N_UpdateProgram,
                        MenuLocation.Root,
                        () => ResourceUpdater.UpdateProgram(cancellationToken)),
                    Leaf(
                        "加入QQ群（号被封过之后在频道里说话会概率被夹",
                        MenuLocation.Root,
                        () =>
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "https://qm.qq.com/q/4z6xHQ908w",
                                UseShellExecute = true
                            });
                            LiveDisplayConsole.Acknowledge(
                                "已打开 QQ 群链接：https://qm.qq.com/q/4z6xHQ908w",
                                cancellationToken);
                            return Task.CompletedTask;
                        })
                };

                MenuItem? installUraCoreItem = null;
                MenuItem? hachimiItem = null;
                if (OperatingSystem.IsWindows())
                {
                    hachimiItem = Leaf(
                        "Hachimi",
                        MenuLocation.InstallUraCore,
                        () => InstallUraCoreAsync("Hachimi", cancellationToken));
                    installUraCoreItem = Branch(
                        I18N_InstallUraCore,
                        hachimiItem,
                        Leaf(
                            "umamusume-localify",
                            MenuLocation.InstallUraCore,
                            () => InstallUraCoreAsync("umamusume-localify", cancellationToken)));
                    mainItems.Add(installUraCoreItem);
                }

                var root = new Menu(mainItems);
                IReadOnlyList<MenuItem> focusPath = location switch
                {
                    MenuLocation.Options => [optionsItem, miscItem],
                    MenuLocation.Core => [optionsItem, coreItem, listenAddressItem],
                    MenuLocation.Repository => [optionsItem, repositoryItem, targetsItem],
                    MenuLocation.Plugin when firstPluginItem is not null =>
                        [optionsItem, pluginItem, firstPluginItem],
                    MenuLocation.Plugin => [optionsItem, pluginItem],
                    MenuLocation.Updater => [optionsItem, updaterItem, trainerGenderItem],
                    MenuLocation.DatabaseLanguage =>
                        [
                            optionsItem,
                            updaterItem,
                            databaseLanguageItem,
                            databaseLanguageItems.First(x => x.Title == Config.Updater.DatabaseLanguage)
                        ],
                    MenuLocation.Language =>
                        [
                            optionsItem,
                            languageItem,
                            languageItems[(int)Config.Language.Selected]
                        ],
                    MenuLocation.InstallUraCore when installUraCoreItem is not null && hachimiItem is not null =>
                        [installUraCoreItem, hachimiItem],
                    _ => [startItem]
                };

                TerminalGuiDialogs.StartupMenu(
                    application,
                    I18N_Instruction,
                    root,
                    focusPath,
                    cancellationToken);
                if (nextAction is null)
                    throw new OperationCanceledException("启动菜单已取消。");

                try
                {
                    await nextAction();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (start)
                    return;
            }
        }

        static async Task InstallUraCoreAsync(
            string target,
            CancellationToken cancellationToken)
        {
            if (UraCoreHelper.GamePaths.Count == 0)
            {
                LiveDisplayConsole.Acknowledge(
                    "没有找到可安装 Mod 的游戏目录。",
                    cancellationToken);
                return;
            }

            var results = new List<string>();
            foreach (var path in UraCoreHelper.GamePaths)
            {
                var confirm = LiveDisplayConsole.Confirm(
                    $"是否将 {target} 安装到 {path}，并把注册表 " +
                    @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\DevOverrideEnable " +
                    "设为 1？该操作需要管理员权限并会影响系统 DLL redirection。",
                    cancellationToken: cancellationToken);
                if (!confirm)
                    continue;

                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Environment.ProcessPath,
                        Arguments = "--enable-dll-redirection --confirmed",
                        CreateNoWindow = true,
                        UseShellExecute = true,
                        Verb = "runas"
                    }
                };
                proc.Start();
                await proc.WaitForExitAsync(cancellationToken);
                if (proc.ExitCode != 0)
                {
                    results.Add($"{path}: 注册表操作失败（exit {proc.ExitCode}）");
                    continue;
                }

                var url = (target == "Hachimi"
                    ? "https://github.com/UmamusumeResponseAnalyzer/Hachimi/releases/latest/download/Hachimi.zip"
                    : "https://github.com/UmamusumeResponseAnalyzer/Hachimi/releases/latest/download/UmamusumeLocalify.zip")
                    .AllowMirror();
                using var stream = await ResourceUpdater.HttpClient.GetStreamAsync(url, cancellationToken);
                using var archive = new ZipArchive(stream);
                archive.ExtractToDirectory(path, true);
                results.Add(string.Format(I18N_UraCoreHelper_InstallSuccess, path));
            }

            LiveDisplayConsole.Acknowledge(
                results.Count == 0 ? "未安装 Mod。" : string.Join(Environment.NewLine, results),
                cancellationToken);
        }
        static async Task<bool> TryHandleCliOnlyArgumentsAsync(string[] args)
        {
            switch (args)
            {
                case ["-v" or "--version"]:
                    Console.Write(Assembly.GetExecutingAssembly().GetName().Version);
                    return true;
                case ["--update", var savePath]:
                    await ResourceUpdater.TryUpdateProgram(savePath);
                    return true;
                case ["--update-data", var archivePath]:
                    ZipFile.ExtractToDirectory(archivePath, "./");
                    return true;
                case ["--enable-dll-redirection", "--confirmed"]:
                    UraCoreHelper.EnableDllRedirection();
                    return true;
                case ["--enable-dll-redirection"]:
                    Console.Error.WriteLine(
                        "拒绝修改注册表：缺少确认参数。请从 URA 的 Mod 安装流程发起该操作。");
                    Environment.ExitCode = 1;
                    return true;
                default:
                    return false;
            }
        }
        internal static void ApplyCultureInfo()
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(LanguageConfig.GetCulture());
            Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(LanguageConfig.GetCulture());
            foreach (var i in Assembly.GetExecutingAssembly().GetTypes().Where(x => x.IsClass && x.Namespace?.StartsWith("UmamusumeResponseAnalyzer.Localization") == true))
            {
                var rc = i?.GetField("resourceCulture", BindingFlags.NonPublic | BindingFlags.Static);
                if (rc == null) continue;
                rc.SetValue(null, Thread.CurrentThread.CurrentUICulture);
            }
        }
        internal static void Restart()
        {
            var exePath = Environment.ProcessPath!;
            StartAfterTerminalCleanup(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)!
            });
        }

        internal static void StartAfterTerminalCleanup(ProcessStartInfo startInfo)
            => throw new PostShutdownProcessRequestedException(startInfo);

        internal sealed class PostShutdownProcessRequestedException(ProcessStartInfo startInfo) : Exception
        {
            public ProcessStartInfo StartInfo { get; } = startInfo;
        }

        sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
        {
            readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> workItems = [];
            readonly object lifecycleGate = new();
            readonly int ownerThreadId = Environment.CurrentManagedThreadId;
            IApplication? application;
            int applicationDrainScheduled;
            int closing;

            public void Bind(IApplication value)
            {
                ArgumentNullException.ThrowIfNull(value);
                if (Environment.CurrentManagedThreadId != ownerThreadId)
                    throw new InvalidOperationException("Terminal.Gui application 必须在 owner thread 绑定。");
                if (application is not null)
                    throw new InvalidOperationException("Terminal.Gui application 已绑定。");
                if (value.MainThreadId != ownerThreadId)
                    throw new InvalidOperationException("Terminal.Gui application 的 MainThreadId 与 UI owner thread 不一致。");

                application = value;
                value.Iteration += ApplicationIteration;
            }

            public void Unbind(IApplication value)
            {
                if (Environment.CurrentManagedThreadId != ownerThreadId)
                    throw new InvalidOperationException("Terminal.Gui application 必须在 owner thread 解绑。");
                if (!ReferenceEquals(application, value))
                    throw new InvalidOperationException("Terminal.Gui application 绑定状态不一致。");

                value.Iteration -= ApplicationIteration;
                application = null;
                Interlocked.Exchange(ref applicationDrainScheduled, 0);
            }

            public override void Post(SendOrPostCallback callback, object? state)
            {
                ArgumentNullException.ThrowIfNull(callback);
                lock (lifecycleGate)
                {
                    if (closing != 0 || !workItems.TryAdd((callback, state)))
                        throw new InvalidOperationException("Terminal.Gui UI synchronization context 已停止。");
                }
                WakeApplicationLoop();
            }

            public override void Send(SendOrPostCallback callback, object? state)
            {
                ArgumentNullException.ThrowIfNull(callback);
                if (Environment.CurrentManagedThreadId == ownerThreadId)
                {
                    callback(state);
                    return;
                }

                ExceptionDispatchInfo? failure = null;
                using var completed = new ManualResetEventSlim();
                Post(_ =>
                {
                    try
                    {
                        callback(state);
                    }
                    catch (Exception ex)
                    {
                        failure = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        completed.Set();
                    }
                }, null);
                completed.Wait();
                failure?.Throw();
            }

            public override SynchronizationContext CreateCopy() => this;

            void WakeApplicationLoop()
            {
                var app = Volatile.Read(ref application);
                if (app is null || Interlocked.Exchange(ref applicationDrainScheduled, 1) != 0)
                {
                    return;
                }

                try
                {
                    app.Invoke(static () => { });
                }
                catch (NotInitializedException)
                {
                    Interlocked.Exchange(ref applicationDrainScheduled, 0);
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Exchange(ref applicationDrainScheduled, 0);
                }
            }

            void ApplicationIteration(
                object? sender,
                Terminal.Gui.App.EventArgs<IApplication?> e)
                => DrainAvailableOnOwner();

            void DrainAvailableOnOwner()
            {
                if (Environment.CurrentManagedThreadId != ownerThreadId)
                    throw new InvalidOperationException("SynchronizationContext 必须在 owner thread drain。");

                while (workItems.TryTake(out var workItem))
                    workItem.Callback(workItem.State);
                Interlocked.Exchange(ref applicationDrainScheduled, 0);
                if (workItems.Count > 0)
                    WakeApplicationLoop();
            }

            public void Run(Task task)
            {
                ArgumentNullException.ThrowIfNull(task);
                if (Environment.CurrentManagedThreadId != ownerThreadId)
                    throw new InvalidOperationException("SynchronizationContext pump 必须在 owner thread 运行。");

                _ = task.ContinueWith(
                    static (_, state) =>
                    {
                        var context = (SingleThreadSynchronizationContext)state!;
                        lock (context.lifecycleGate)
                        {
                            if (context.closing != 0)
                                return;
                            context.closing = 1;
                            context.workItems.CompleteAdding();
                        }
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                foreach (var workItem in workItems.GetConsumingEnumerable())
                {
                    workItem.Callback(workItem.State);
                    DrainAvailableOnOwner();
                }

                task.GetAwaiter().GetResult();
            }

            public void Dispose()
            {
                lock (lifecycleGate)
                {
                    if (closing == 0)
                    {
                        closing = 1;
                        workItems.CompleteAdding();
                    }
                }
                workItems.Dispose();
            }
        }

        sealed class ShutdownCommandTarget : Terminal.Gui.ViewBase.View
        {
            public ShutdownCommandTarget(Action shutdown)
            {
                AddCommand(Terminal.Gui.Input.Command.Quit, () =>
                {
                    shutdown();
                    return true;
                });
            }
        }
    }
}
