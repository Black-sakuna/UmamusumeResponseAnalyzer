using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Terminal.Gui.App;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal static partial class PluginManager
    {
        internal enum PluginLifecycleOutcome
        {
            Failed,
            Succeeded,
            RestartRequired,
        }

        internal sealed record PluginLifecycleResult(
            string PluginName,
            PluginLifecycleOutcome Outcome);

        internal sealed record PluginRuntimeStatus(
            string InternalName,
            string DisplayName,
            string Author,
            Version? Version,
            bool IsLoaded,
            bool IsAvailable,
            bool LoadInHost);

        static readonly PluginRuntimeState Runtime = new();
        internal static Dictionary<string, PluginMetadata> Metadatas => Runtime.Metadatas;
        internal static Dictionary<string, PluginMetadata> AssemblyMetadatas => Runtime.AssemblyMetadatas;
        internal static List<string> FailedPlugins => Runtime.FailedPlugins;
        internal static List<IPlugin> LoadedPlugins => Runtime.LoadedPlugins;
        internal static SortedDictionary<int, List<AnalyzerRegistration>> RequestAnalyzerMethods => Runtime.RequestAnalyzers;
        internal static SortedDictionary<int, List<AnalyzerRegistration>> ResponseAnalyzerMethods => Runtime.ResponseAnalyzers;
        internal static List<HashSet<string>> ContextGroups => Runtime.ContextGroups;
        internal static Dictionary<string, PluginLoadContext> Contexts => Runtime.Contexts;
        internal static Dictionary<string, Assembly> AssemblyMap => Runtime.AssemblyMap;
        internal static List<Assembly> Assemblies => Runtime.Assemblies;
        static readonly string HostAssemblyName = typeof(PluginManager).Assembly.GetName().Name ?? "UmamusumeResponseAnalyzer";
        static readonly FrozenSet<string> SharedAssemblyNames = new[]
        {
            HostAssemblyName,
            "Terminal.Gui",
            "Watson.Lite",
            "WatsonWebserver.Core",
            "WatsonWebserver.Lite",
        }.ToFrozenSet(StringComparer.Ordinal);
        static PluginHostEvents HostEvents => Runtime.HostEvents;
        static ConditionalWeakTable<IPlugin, PluginGeneration> PluginGenerations => Runtime.Generations;
        static AsyncLocal<int> PluginCallbackDepth => Runtime.CallbackDepth;
        static object AnalyzerGate => Runtime.AnalyzerGate;

        // 每个插件注册的 HTTP 路由，卸载时凭此精确移除（Watson 的 StaticRouteManager 支持 Remove）
        static Dictionary<IPlugin, List<RouteRegistration>> PluginRoutes => Runtime.Routes;
        static ReaderWriterLockSlim StateLock => Runtime.StateLock;
        static object ReloadTransactionGate => Runtime.LifecycleGate;
        static bool reloadTransactionActive
        {
            get => Runtime.ReloadTransactionActive;
            set => Runtime.ReloadTransactionActive = value;
        }
        static bool initializationComplete
        {
            get => Runtime.InitializationComplete;
            set => Runtime.InitializationComplete = value;
        }
        static bool startedComplete
        {
            get => Runtime.StartedComplete;
            set => Runtime.StartedComplete = value;
        }
        static bool shuttingDown
        {
            get => Runtime.ShuttingDown;
            set => Runtime.ShuttingDown = value;
        }
        static TaskCompletionSource? reloadCompleted
        {
            get => Runtime.ReloadCompleted;
            set => Runtime.ReloadCompleted = value;
        }
        static TaskCompletionSource shutdownCompleted
        {
            get => Runtime.ShutdownCompleted;
            set => Runtime.ShutdownCompleted = value;
        }

        internal static IDisposable EnterPluginCallback(
            IPlugin plugin,
            CancellationToken cancellationToken = default)
        {
            var callback = TryEnterPluginCallback(plugin, cancellationToken);
            return callback ?? throw new InvalidOperationException(
                $"插件已卸载或 generation 已关闭，拒绝启动回调: {InternalName(plugin)}");
        }

        internal static IDisposable? TryEnterPluginCallback(
            IPlugin plugin,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsShuttingDown() ||
                !PluginGenerations.TryGetValue(plugin, out var generation) ||
                !generation.TryEnterCallback(out var generationLease))
                return null;

            return generationLease;
        }

        internal static IDisposable EnterPluginConfiguration(
            IPlugin plugin,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryEnterPluginInspection(plugin) ?? throw new InvalidOperationException(
                $"插件已卸载或 generation 已关闭，拒绝打开设置: {InternalName(plugin)}");
        }

        static IDisposable? TryEnterPluginInspection(IPlugin plugin)
        {
            if (IsShuttingDown() ||
                !PluginGenerations.TryGetValue(plugin, out var generation) ||
                !generation.TryEnterInspection(out var inspection))
                return null;

            return inspection;
        }

        internal static string DescribeException(Exception exception)
        {
            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            string message;
            try { message = exception.Message; }
            catch (Exception error)
            {
                message = $"<读取 Message 失败: {error.GetType().FullName ?? error.GetType().Name}>";
            }
            return $"exception={exceptionType}, message={message}";
        }

        internal static Exception? ReportPluginFailure(string source, InvalidOperationException failure)
        {
            Exception? notificationError = null;
            try
            {
                TerminalUi.Notify("Plugin", failure.Message, UiSeverity.Error);
            }
            catch (Exception ex)
            {
                notificationError = ex;
            }

            try
            {
                TerminalUi.LogException(
                    source,
                    notificationError is null
                        ? failure
                        : new AggregateException("插件错误及 notification diagnostics 失败。", failure, notificationError));
                return null;
            }
            catch (Exception logError)
            {
                return notificationError is null
                    ? logError
                    : new AggregateException("插件 diagnostics sinks 均失败。", notificationError, logError);
            }
        }

        static void ReportPluginDiagnostic(Exception exception)
        {
            TerminalUi.LogException("Plugin", exception);
            TerminalUi.Notify(
                "Plugin",
                TerminalUi.FormatExceptionLogMessage(exception),
                UiSeverity.Error);
        }

        static void ReportPluginDiagnostic(string message, UiSeverity severity)
        {
            TerminalUi.Log("Plugin", message, severity);
            TerminalUi.Notify("Plugin", message, severity);
        }

        internal static IDisposable? TryEnterPluginRegistration(IPlugin plugin)
        {
            if (!IsShuttingDown() &&
                PluginGenerations.TryGetValue(plugin, out var generation) &&
                generation.TryEnterRegistration(out var registration))
                return registration!;

            return null;
        }

        internal static IDisposable EnterPluginRegistration(IPlugin plugin)
            => TryEnterPluginRegistration(plugin)
               ?? throw new InvalidOperationException(
                   $"插件已卸载或 generation 不接受注册: {InternalName(plugin)}");

        static bool IsShuttingDown()
        {
            lock (ReloadTransactionGate)
                return shuttingDown;
        }

        static TaskCompletionSource NewTaskSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static void EnterStateRead() => StateLock.EnterReadLock();

        internal static void ExitStateRead() => StateLock.ExitReadLock();

        internal static IReadOnlyList<IPlugin> SnapshotLoadedPlugins()
        {
            EnterStateRead();
            try { return [.. LoadedPlugins]; }
            finally { ExitStateRead(); }
        }

        internal static IPlugin? FindLoadedPlugin(string internalName)
        {
            EnterStateRead();
            try
            {
                return LoadedPlugins.FirstOrDefault(plugin =>
                    string.Equals(InternalName(plugin), internalName, StringComparison.OrdinalIgnoreCase));
            }
            finally { ExitStateRead(); }
        }

        internal static IReadOnlyList<PluginRuntimeStatus> SnapshotPluginStatuses()
            => BuildPluginStatuses(new Dictionary<string, PluginMetadata>());

        internal static IReadOnlyList<PluginRuntimeStatus> InspectPluginStatuses()
            => BuildPluginStatuses(ScanPluginMetadata(reportFailures: false).Plugins);

        static IReadOnlyList<PluginRuntimeStatus> BuildPluginStatuses(
            IReadOnlyDictionary<string, PluginMetadata> scanned)
        {
            IPlugin[] loaded;
            PluginMetadata[] known;
            EnterStateRead();
            try
            {
                loaded = [.. LoadedPlugins];
                known = [.. Metadatas.Values];
            }
            finally { ExitStateRead(); }

            var loadedByName = loaded
                .GroupBy(InternalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var knownByName = known
                .GroupBy(metadata => metadata.PluginName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var names = scanned.Keys
                .Concat(knownByName.Keys)
                .Concat(loadedByName.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            List<PluginRuntimeStatus> statuses = [];
            foreach (var name in names)
            {
                loadedByName.TryGetValue(name, out var plugin);
                var metadata = GetValueIgnoreCase(scanned, name) ?? GetValueIgnoreCase(knownByName, name);
                var displayName = metadata?.PluginName ?? name;
                var author = string.Empty;
                Version? version = null;
                var isLoaded = false;
                if (plugin is not null)
                {
                    using var inspection = TryEnterPluginInspection(plugin);
                    if (inspection is not null)
                    {
                        using var callback = EnterPluginCallbackScope();
                        using var owner = HotkeyManager.RegisterScope(plugin);
                        isLoaded = true;
                        try
                        {
                            (displayName, author, version) = (plugin.Name, plugin.Author, plugin.Version);
                        }
                        catch (Exception ex)
                        {
                            var failure = new InvalidOperationException(
                                $"读取插件状态失败: plugin={name}, {DescribeException(ex)}");
                            _ = ReportPluginFailure("Plugin", failure);
                        }
                    }
                }

                statuses.Add(new(
                    metadata?.PluginName ?? name,
                    displayName,
                    author,
                    version,
                    isLoaded,
                    metadata is not null,
                    metadata?.LoadInHost ?? false));
            }

            return statuses;
        }

        internal static string InternalName(IPlugin plugin)
            => plugin.GetType().Assembly.GetName().Name
               ?? plugin.GetType().FullName
               ?? nameof(IPlugin);

        static PluginGeneration GenerationFor(IPlugin plugin)
            => PluginGenerations.GetValue(plugin, static value => new(value));

        static PluginGeneration RequireGeneration(IPlugin plugin)
            => PluginGenerations.TryGetValue(plugin, out var generation)
                ? generation
                : throw new InvalidOperationException($"插件缺少 runtime generation: {InternalName(plugin)}");

        internal static void Init()
        {
            using var transaction = EnterInitializationTransaction();
            Directory.CreateDirectory("Plugins");
            LoadMetadatas();
            BuildGroups();
            LoadPlugins();
        }

        static IDisposable EnterInitializationTransaction()
        {
            if (PluginCallbackDepth.Value != 0)
                throw new InvalidOperationException("插件回调内禁止重新初始化插件系统。");

            lock (ReloadTransactionGate)
            {
                if (reloadTransactionActive)
                    throw new InvalidOperationException("已有插件 lifecycle 事务正在运行，无法重新初始化。");
                if (shuttingDown && !shutdownCompleted.Task.IsCompletedSuccessfully)
                    throw new InvalidOperationException("上一次插件 shutdown 尚未完成，无法重新初始化。");

                reloadTransactionActive = true;
                reloadCompleted = NewTaskSource();
                initializationComplete = false;
                startedComplete = false;
                shuttingDown = false;
                return new ReloadTransaction();
            }
        }

        /// <summary>
        /// 调用插件 Initialize，并把期间注册的快捷键归属到该插件实例。
        /// 初始批量加载（Program.cs）与热重载都经此入口，保证 owner 标记一致。
        /// </summary>
        internal static void InitializePlugin(IPlugin plugin)
            => InitializePlugin(plugin, activateCallbacks: true);

        static void InitializePlugin(IPlugin plugin, bool activateCallbacks)
        {
            var generation = GenerationFor(plugin);
            using var initialization = generation.EnterInitialization();
            try
            {
                using var callback = EnterPluginCallbackScope();
                using var owner = HotkeyManager.RegisterScope(plugin);
                InvokeContextInitialize(plugin, new PluginContext(TerminalUi.RequireHost(), plugin, HostEvents));
                generation.CompleteInitialization(activateCallbacks);
            }
            catch
            {
                generation.AbortInitialization();
                throw;
            }
        }

        internal static void InitializeLoadedPlugins()
        {
            using var transaction = EnterReloadTransaction();
            var loaded = SnapshotLoadedPlugins();
            List<HashSet<string>> groups;
            EnterStateRead();
            try
            {
                groups = ContextGroups
                    .Select(group => group.ToHashSet(StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            finally { ExitStateRead(); }

            var grouped = new HashSet<IPlugin>(ReferenceEqualityComparer.Instance);
            foreach (var group in groups)
            {
                var plugins = loaded
                    .Where(plugin => group.Contains(InternalName(plugin)))
                    .ToList();
                if (plugins.Count == 0)
                    continue;
                grouped.UnionWith(plugins);

                var initialized = true;
                foreach (var plugin in plugins)
                    if (!GenerationFor(plugin).IsAccepting && !TryInitializePlugin(plugin, committed: false))
                    {
                        initialized = false;
                        break;
                    }

                if (initialized)
                {
                    foreach (var plugin in plugins)
                        RequireGeneration(plugin).Open();
                    continue;
                }

                PendingPluginUnload unload;
                StateLock.EnterWriteLock();
                try
                {
                    if (!Contexts.ContainsKey(GroupKey(group)))
                        throw new InvalidOperationException($"初始化失败的插件组缺少 context: {string.Join("、", group)}");
                    unload = PrepareUnloadGroupLocked(group);
                }
                finally { StateLock.ExitWriteLock(); }
                CompletePendingUnloadsAsync([unload], clearAll: false).GetAwaiter().GetResult();
            }

            foreach (var plugin in loaded.Where(plugin => !grouped.Contains(plugin)))
                if (!GenerationFor(plugin).IsAccepting)
                    TryInitializePlugin(plugin, committed: true);

            lock (ReloadTransactionGate)
                initializationComplete = true;
        }

        static bool TryInitializePlugin(IPlugin plugin, bool committed)
        {
            try
            {
                InitializePlugin(plugin, activateCallbacks: committed);
                return true;
            }
            catch (Exception ex)
            {
                var internalName = InternalName(plugin);
                var failedPlugin = Metadatas.TryGetValue(internalName, out var metadata)
                    ? metadata.FilePath
                    : plugin.Name;
                ReportPluginDiagnostic(
                    new InvalidOperationException($"插件初始化失败: plugin={plugin.Name} ({internalName})", ex));
                if (!FailedPlugins.Contains(failedPlugin))
                    FailedPlugins.Add(failedPlugin);
                if (committed)
                {
                    try { CompleteFailedPluginLoadAsync(plugin, flush: true).GetAwaiter().GetResult(); }
                    catch (Exception cleanupEx) { ReportPluginDiagnostic(cleanupEx); }
                }
                return false;
            }
        }

        static void InvokeContextInitialize(IPlugin plugin, IPluginContext context)
        {
            var method = plugin.GetType().GetMethod(
                nameof(IPlugin.Initialize),
                BindingFlags.Instance | BindingFlags.Public,
                [typeof(IPluginContext)]);
            if (method is null || method.DeclaringType == typeof(IPlugin))
                throw new InvalidOperationException(
                    $"插件必须实现 Initialize(IPluginContext context): plugin={plugin.Name} ({InternalName(plugin)})");

            if (method.ReturnType != typeof(void))
                throw new InvalidOperationException(
                    $"插件 Initialize 签名无效: plugin={plugin.Name} ({InternalName(plugin)}), " +
                    $"method={method.DeclaringType?.FullName}.{method.Name}, expected=void, actual={method.ReturnType.FullName}");

            try
            {
                method.Invoke(plugin, [context]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        internal static async Task TriggerStartedAsync(CancellationToken cancellationToken = default)
        {
            using var transaction = EnterReloadTransaction();
            lock (ReloadTransactionGate)
                if (startedComplete)
                    return;

            await HostEvents.TriggerStartedAsync(cancellationToken: cancellationToken);
            lock (ReloadTransactionGate)
                startedComplete = true;
        }

        internal static Task TriggerStartedForPluginsAsync(IEnumerable<IPlugin> plugins, CancellationToken cancellationToken = default)
            => HostEvents.TriggerStartedAsync(plugins, cancellationToken);

        internal static void DisposeHostEventSubscriptions(IPlugin plugin)
            => HostEvents.DisposeFor(plugin);

        internal static void ClearHostEventSubscriptions()
            => HostEvents.Clear();

        internal static async Task ShutdownAsync()
        {
            Task transactionWait;
            TaskCompletionSource completion;
            var performShutdown = false;
            lock (ReloadTransactionGate)
            {
                if (shuttingDown)
                {
                    completion = shutdownCompleted;
                    transactionWait = Task.CompletedTask;
                }
                else
                {
                    shuttingDown = true;
                    completion = shutdownCompleted = NewTaskSource();
                    transactionWait = reloadCompleted?.Task ?? Task.CompletedTask;
                    performShutdown = true;
                }
            }

            if (!performShutdown)
            {
                await completion.Task;
                return;
            }

            try
            {
                await transactionWait;
                List<PendingPluginUnload> unloads = [];
                StateLock.EnterWriteLock();
                try
                {
                    var generations = LoadedPlugins.Select(GenerationFor).ToList();
                    foreach (var context in Contexts.Values.Distinct())
                    {
                        var contextGenerations = generations
                            .Where(x => ReferenceEquals(
                                AssemblyLoadContext.GetLoadContext(x.Plugin.GetType().Assembly),
                                context))
                            .ToList();
                        unloads.Add(new(
                            contextGenerations.Select(x => InternalName(x.Plugin)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                            Contexts.First(x => ReferenceEquals(x.Value, context)).Key,
                            context,
                            contextGenerations,
                            true));
                    }

                    var contextPlugins = unloads.SelectMany(x => x.Generations).ToHashSet();
                    var hostGenerations = generations.Where(x => !contextPlugins.Contains(x)).ToList();
                    if (hostGenerations.Count != 0)
                        unloads.Add(new(
                            hostGenerations.Select(x => InternalName(x.Plugin)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                            null,
                            null,
                            hostGenerations,
                            true));

                    foreach (var generation in generations)
                        _ = generation.Close();
                }
                finally
                {
                    StateLock.ExitWriteLock();
                }

                await CompletePendingUnloadsAsync(unloads, clearAll: true);
                unloads.Clear();

                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                throw;
            }
        }

    }
}
