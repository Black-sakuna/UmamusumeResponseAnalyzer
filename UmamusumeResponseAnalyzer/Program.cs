using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Terminal.Gui.App;
using UmamusumeResponseAnalyzer.LiveDisplay;
using UmamusumeResponseAnalyzer.Plugin;
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
                    while (true)
                    {
                        string selection;
                        try
                        {
                            selection = await ShowMenu(application, lifetimeCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        if (selection == I18N_Start)
                            break;
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
        static async Task<string> ShowMenu(
            IApplication application,
            CancellationToken cancellationToken)
        {
            var selections = new List<string>
            {
                    I18N_Start,
                    I18N_Options,
                    "插件仓库",
                    I18N_UpdateAssets,
                    I18N_UpdateProgram,
                    "加入QQ群（号被封过之后在频道里说话会概率被夹"
            };
            #region 条件显示功能
            // Windows限定功能，其他平台不显示
            if (OperatingSystem.IsWindows())
            {
                selections.Add(I18N_InstallUraCore);
            }
            #endregion
            var prompt = TerminalGuiDialogs.StartupMenu(
                application,
                I18N_Instruction,
                selections,
                cancellationToken);
            try
            {
                if (prompt == I18N_Options)
                {
                    await Config.PromptAsync(cancellationToken);
                }
                else if (prompt == "插件仓库")
                {
                    try
                    {
                        await _plugin_initialize_task;
                    }
                    catch (Exception ex)
                    {
                        LiveDisplayConsole.Acknowledge(
                            $"插件扫描失败：{ex.Message}",
                            cancellationToken);
                        return prompt;
                    }
                    await PluginRepository.ShowMenuAsync(cancellationToken);
                }
                else if (prompt == I18N_UpdateAssets)
                {
                    await ResourceUpdater.UpdateAssets(cancellationToken);
                }
                else if (prompt == I18N_UpdateProgram)
                {
                    await ResourceUpdater.UpdateProgram(cancellationToken);
                }
                else if (prompt == I18N_InstallUraCore)
                {
                    if (UraCoreHelper.GamePaths.Count == 0)
                    {
                        LiveDisplayConsole.Acknowledge("没有找到可安装 Mod 的游戏目录。", cancellationToken);
                        return prompt;
                    }

                    var target = LiveDisplayConsole.Select(
                        "请选择想要安装的 Mod",
                        new[] { "Hachimi", "umamusume-localify" },
                        cancellationToken: cancellationToken);
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
                else if (prompt == "加入QQ群（号被封过之后在频道里说话会概率被夹")
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://qm.qq.com/q/4z6xHQ908w",
                        UseShellExecute = true
                    });
                    LiveDisplayConsole.Acknowledge(
                        "已打开 QQ 群链接：https://qm.qq.com/q/4z6xHQ908w",
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            cancellationToken.ThrowIfCancellationRequested();
            return prompt;
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
