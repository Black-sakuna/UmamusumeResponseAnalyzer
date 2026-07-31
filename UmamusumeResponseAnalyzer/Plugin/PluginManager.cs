using System.Collections.Frozen;
using Gallop.Endpoints;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Terminal.Gui.App;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.TerminalGui;
using WatsonWebserver.Core;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal sealed record AnalyzerRegistration(
        IPlugin Plugin,
        MethodInfo? Method,
        Type EndpointType,
        AnalyzerKind Kind,
        int Priority,
        Func<AnalyzerDispatchContext, ValueTask> Handler,
        string Source);

    internal sealed class PluginCallbackSnapshot<T>(
        List<T> items,
        List<IDisposable> leases) : IDisposable
    {
        List<T>? items = items;
        List<IDisposable>? leases = leases;

        internal int Count => items?.Count ?? 0;
        internal T this[int index] => items![index];

        internal static PluginCallbackSnapshot<T> Create(
            IEnumerable<T> candidates,
            Func<T, IPlugin> plugin,
            CancellationToken cancellationToken = default)
        {
            var items = new List<T>();
            var leases = new List<IDisposable>();
            try
            {
                foreach (var candidate in candidates)
                {
                    var lease = PluginManager.TryEnterPluginCallback(plugin(candidate), cancellationToken);
                    if (lease is null)
                        continue;
                    items.Add(candidate);
                    leases.Add(lease);
                }
                return new(items, leases);
            }
            catch
            {
                items.Clear();
                foreach (var lease in leases)
                    lease.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var items = Interlocked.Exchange(ref this.items, null);
            var leases = Interlocked.Exchange(ref this.leases, null);
            items?.Clear();
            if (leases is null)
                return;

            foreach (var lease in leases)
                lease.Dispose();
            leases.Clear();
        }
    }

    internal sealed record PluginRuntimeStatus(
        string InternalName,
        string DisplayName,
        string Author,
        Version? Version,
        bool IsLoaded,
        bool IsAvailable,
        bool LoadInHost);

    sealed class PluginGeneration(IPlugin plugin)
    {
        readonly object gate = new();
        TaskCompletionSource drained = CompletedSignal();
        int inFlight;
        bool initializing;
        bool accepting;
        bool closed;

        internal IPlugin Plugin { get; } = plugin;

        internal bool IsAccepting
        {
            get
            {
                lock (gate)
                    return accepting;
            }
        }

        internal IDisposable EnterInitialization()
        {
            lock (gate)
            {
                if (closed)
                    throw Closed();
                if (initializing || accepting)
                    throw new InvalidOperationException($"插件 generation 已在初始化或运行: {PluginManager.InternalName(Plugin)}");

                initializing = true;
                return EnterLocked();
            }
        }

        internal void CompleteInitialization(bool activateCallbacks)
        {
            lock (gate)
            {
                if (!initializing)
                    throw new InvalidOperationException($"插件 generation 未在初始化: {PluginManager.InternalName(Plugin)}");

                initializing = false;
                if (closed)
                    throw Closed();
                accepting = activateCallbacks;
            }
        }

        internal void AbortInitialization()
        {
            lock (gate)
                initializing = false;
        }

        internal void Open()
        {
            lock (gate)
            {
                if (closed)
                    throw Closed();
                if (initializing)
                    throw new InvalidOperationException($"插件 generation 仍在初始化: {PluginManager.InternalName(Plugin)}");
                accepting = true;
            }
        }

        internal Task Close()
        {
            lock (gate)
            {
                accepting = false;
                closed = true;
                return drained.Task;
            }
        }

        internal bool TryEnterCallback(out IDisposable? lease)
        {
            lock (gate)
            {
                if (!accepting)
                {
                    lease = null;
                    return false;
                }

                lease = EnterLocked();
                return true;
            }
        }

        internal bool TryEnterInspection(out IDisposable? lease)
        {
            lock (gate)
            {
                if (closed)
                {
                    lease = null;
                    return false;
                }

                lease = EnterLocked();
                return true;
            }
        }

        internal bool TryEnterRegistration(out IDisposable? lease)
        {
            lock (gate)
            {
                if (closed || !initializing && !accepting)
                {
                    lease = null;
                    return false;
                }

                lease = EnterLocked();
                return true;
            }
        }

        IDisposable EnterLocked()
        {
            if (inFlight++ == 0)
                drained = NewSignal();
            return new Lease(Exit);
        }

        void Exit()
        {
            TaskCompletionSource? signal = null;
            lock (gate)
            {
                if (--inFlight == 0)
                    signal = drained;
            }
            signal?.TrySetResult();
        }

        static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        static TaskCompletionSource CompletedSignal()
        {
            var signal = NewSignal();
            signal.SetResult();
            return signal;
        }

        InvalidOperationException Closed()
            => new($"插件 generation 已关闭: {PluginManager.InternalName(Plugin)}");

        sealed class Lease(Action release) : IDisposable
        {
            Action? release = release;

            public void Dispose()
                => Interlocked.Exchange(ref release, null)?.Invoke();
        }
    }

    sealed class PluginScopedAnalyzerRegistry(IPlugin plugin) : IPluginAnalyzerRegistry
    {
        public IDisposable RegisterRequest<TEndpoint>(
            Func<byte[], ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
        {
            ArgumentNullException.ThrowIfNull(handler);
            return RegisterRaw(AnalyzerKind.Request, typeof(TEndpoint), (payload, _) => handler(payload), priority);
        }

        public IDisposable RegisterRequest<TEndpoint>(
            Func<byte[], GameHttpHeaders, ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
            => RegisterRaw(AnalyzerKind.Request, typeof(TEndpoint), handler, priority);

        public IDisposable RegisterResponse<TEndpoint>(
            Func<byte[], ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
        {
            ArgumentNullException.ThrowIfNull(handler);
            return RegisterRaw(AnalyzerKind.Response, typeof(TEndpoint), (payload, _) => handler(payload), priority);
        }

        public IDisposable RegisterResponse<TEndpoint>(
            Func<byte[], GameHttpHeaders, ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
            => RegisterRaw(AnalyzerKind.Response, typeof(TEndpoint), handler, priority);

        public IDisposable RegisterRequest<TEndpoint, TRequest>(
            Func<TRequest, ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
        {
            ArgumentNullException.ThrowIfNull(handler);
            return RegisterDto<TRequest>(AnalyzerKind.Request, typeof(TEndpoint), (payload, _) => handler(payload), priority);
        }

        public IDisposable RegisterRequest<TEndpoint, TRequest>(
            Func<TRequest, GameHttpHeaders, ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
            => RegisterDto(AnalyzerKind.Request, typeof(TEndpoint), handler, priority);

        public IDisposable RegisterResponse<TEndpoint, TResponse>(
            Func<TResponse, ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
        {
            ArgumentNullException.ThrowIfNull(handler);
            return RegisterDto<TResponse>(AnalyzerKind.Response, typeof(TEndpoint), (payload, _) => handler(payload), priority);
        }

        public IDisposable RegisterResponse<TEndpoint, TResponse>(
            Func<TResponse, GameHttpHeaders, ValueTask> handler,
            int priority = 0)
            where TEndpoint : IGameEndpoint
            => RegisterDto(AnalyzerKind.Response, typeof(TEndpoint), handler, priority);

        IDisposable RegisterRaw(
            AnalyzerKind kind,
            Type endpointType,
            Func<byte[], GameHttpHeaders, ValueTask> handler,
            int priority)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return PluginManager.RegisterProgrammaticAnalyzer(
                plugin,
                kind,
                endpointType,
                typeof(byte[]),
                priority,
                context => handler(context.Payload, context.Headers),
                "programmatic raw analyzer");
        }

        IDisposable RegisterDto<TPayload>(
            AnalyzerKind kind,
            Type endpointType,
            Func<TPayload, GameHttpHeaders, ValueTask> handler,
            int priority)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (typeof(TPayload) == typeof(byte[]))
                throw new InvalidOperationException("DTO analyzer 不能使用 byte[]；raw analyzer 请使用单泛型 RegisterRequest/RegisterResponse overload。");

            return PluginManager.RegisterProgrammaticAnalyzer(
                plugin,
                kind,
                endpointType,
                typeof(TPayload),
                priority,
                context => handler((TPayload)context.GetDto(), context.Headers),
                "programmatic DTO analyzer");
        }
    }

    sealed class AnalyzerRegistrationHandle(AnalyzerRegistration registration) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            PluginManager.RemoveAnalyzerRegistration(registration);
        }
    }

    sealed class RouteRegistration(
        IPlugin plugin,
        WatsonWebserver.Core.HttpMethod method,
        string path,
        Func<HttpContextBase, Task> handler)
    {
        int removed;

        public IPlugin Plugin { get; } = plugin;
        public WatsonWebserver.Core.HttpMethod Method { get; } = method;
        public string Path { get; } = path;

        public async Task InvokeAsync(HttpContextBase ctx)
        {
            using var generation = PluginManager.EnterPluginCallback(Plugin);
            if (Volatile.Read(ref removed) != 0)
                throw new ObjectDisposedException(Path, $"插件路由已卸载: plugin={PluginManager.InternalName(Plugin)}, path={Path}");

            using var callback = PluginManager.EnterPluginCallbackScope();
            using var owner = HotkeyManager.RegisterScope(Plugin);
            try
            {
                await handler(ctx);
            }
            catch (Exception ex)
            {
                var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
                string message;
                try { message = ex.Message; }
                catch (Exception messageError)
                {
                    var messageErrorType = messageError.GetType().FullName ?? messageError.GetType().Name;
                    message = $"<读取 Message 失败: {messageErrorType}>";
                }

                var failure = new InvalidOperationException(
                    $"插件路由处理失败: plugin={PluginManager.InternalName(Plugin)}, path={Path}, " +
                    $"exception={exceptionType}, message={message}");
                if (PluginManager.ReportPluginFailure("Plugin", failure) is { } diagnosticsError)
                    throw new AggregateException("插件路由处理及 diagnostics 失败。", failure, diagnosticsError);
                throw failure;
            }
        }

        public void MarkRemoved()
            => Interlocked.Exchange(ref removed, 1);
    }

    sealed record PluginRegistrationPlan(
        List<AnalyzerRegistration> Analyzers,
        List<RouteRegistration> Routes);

    sealed class PendingPluginUnload(
        HashSet<string> names,
        string? key,
        PluginManager.PluginLoadContext? context,
        List<PluginGeneration> generations,
        bool removeGroupState)
    {
        internal HashSet<string> Names { get; } = names;
        internal string? Key { get; } = key;
        internal PluginManager.PluginLoadContext? Context { get; private set; } = context;
        internal List<PluginGeneration> Generations { get; } = generations;
        internal bool RemoveGroupState { get; } = removeGroupState;

        internal void Release()
        {
            Generations.Clear();
            Context = null;
        }
    }

    sealed record StagedAssembly(string Name, Assembly Assembly);

    sealed record StagedPlugin(IPlugin Plugin, PluginRegistrationPlan Plan);

    sealed record StagedGroupLoad(
        HashSet<string> Names,
        string Key,
        PluginManager.PluginLoadContext Context,
        List<StagedAssembly> Assemblies,
        List<StagedPlugin> Plugins);

    public static class PluginManager
    {
        public enum PluginLifecycleOutcome
        {
            Failed,
            Succeeded,
            RestartRequired,
        }

        public sealed record PluginLifecycleResult(
            string PluginName,
            PluginLifecycleOutcome Outcome);

        internal static Dictionary<string, PluginMetadata> Metadatas { get; } = [];
        internal static Dictionary<string, PluginMetadata> AssemblyMetadatas { get; } = [];
        internal static List<string> FailedPlugins { get; } = [];
        public static List<IPlugin> LoadedPlugins { get; } = [];
        internal static SortedDictionary<int, List<AnalyzerRegistration>> RequestAnalyzerMethods { get; } = [];
        internal static SortedDictionary<int, List<AnalyzerRegistration>> ResponseAnalyzerMethods { get; } = [];
        internal static List<HashSet<string>> ContextGroups { get; } = [];
        internal static Dictionary<string, PluginLoadContext> Contexts { get; } = [];
        internal static Dictionary<string, Assembly> AssemblyMap { get; } = [];
        internal static List<Assembly> Assemblies { get; } = [];
        static readonly string HostAssemblyName = typeof(PluginManager).Assembly.GetName().Name ?? "UmamusumeResponseAnalyzer";
        static readonly FrozenSet<string> SharedAssemblyNames = new[]
        {
            HostAssemblyName,
            "Terminal.Gui",
            "Watson.Lite",
            "WatsonWebserver.Core",
            "WatsonWebserver.Lite",
        }.ToFrozenSet(StringComparer.Ordinal);
        static readonly PluginHostEvents HostEvents = new();
        static readonly ConditionalWeakTable<IPlugin, PluginGeneration> PluginGenerations = new();
        static readonly AsyncLocal<int> PluginCallbackDepth = new();
        static readonly object AnalyzerGate = new();

        // 每个插件注册的 HTTP 路由，卸载时凭此精确移除（Watson 的 StaticRouteManager 支持 Remove）
        private static Dictionary<IPlugin, List<RouteRegistration>> PluginRoutes { get; } = new(ReferenceEqualityComparer.Instance);

        private static readonly ReaderWriterLockSlim StateLock = new(LockRecursionPolicy.SupportsRecursion);
        static readonly object ReloadTransactionGate = new();
        static bool reloadTransactionActive;
        static bool initializationComplete;
        static bool startedComplete;
        static bool shuttingDown;
        static TaskCompletionSource? reloadCompleted;
        static TaskCompletionSource shutdownCompleted = CompletedTaskSource();

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

        static IDisposable? TryEnterPluginInspection(IPlugin plugin)
        {
            if (IsShuttingDown() ||
                !PluginGenerations.TryGetValue(plugin, out var generation) ||
                !generation.TryEnterInspection(out var inspection))
                return null;

            return inspection;
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

        internal static IDisposable EnterPluginRegistration(IPlugin plugin)
        {
            if (!IsShuttingDown() &&
                PluginGenerations.TryGetValue(plugin, out var generation) &&
                generation.TryEnterRegistration(out var registration))
                return registration!;

            throw new InvalidOperationException(
                $"插件已卸载或 generation 不接受注册: {InternalName(plugin)}");
        }

        static bool IsShuttingDown()
        {
            lock (ReloadTransactionGate)
                return shuttingDown;
        }

        static TaskCompletionSource NewTaskSource()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        static TaskCompletionSource CompletedTaskSource()
        {
            var completion = NewTaskSource();
            completion.SetResult();
            return completion;
        }

        internal static void EnterStateRead() => StateLock.EnterReadLock();

        internal static void ExitStateRead() => StateLock.ExitReadLock();

        /// <summary>
        /// 线程安全地快照当前已加载插件。分发路径之外的消费者（菜单、更新检查等）应经此枚举：
        /// 热重载会在写锁内增删 <see cref="LoadedPlugins"/>，直接枚举该 List 会与之并发触发 InvalidOperationException。
        /// </summary>
        public static IReadOnlyList<IPlugin> SnapshotLoadedPlugins()
        {
            EnterStateRead();
            try { return [.. LoadedPlugins]; }
            finally { ExitStateRead(); }
        }

        internal static IReadOnlyList<PluginRuntimeStatus> SnapshotPluginStatuses()
        {
            var scanned = ScanPluginMetadataForStatus();
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
                            var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
                            string message;
                            try { message = ex.Message; }
                            catch (Exception messageError)
                            {
                                var messageErrorType = messageError.GetType().FullName ?? messageError.GetType().Name;
                                message = $"<读取 Message 失败: {messageErrorType}>";
                            }

                            var failure = new InvalidOperationException(
                                $"读取插件状态失败: plugin={name}, exception={exceptionType}, message={message}");
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
            lock (ReloadTransactionGate)
            {
                if (reloadTransactionActive || shuttingDown && !shutdownCompleted.Task.IsCompletedSuccessfully)
                    throw new InvalidOperationException("上一次插件 shutdown 尚未完成，无法重新初始化。");
                initializationComplete = false;
                startedComplete = false;
                shuttingDown = false;
            }

            Directory.CreateDirectory("Plugins");
            LoadMetadatas();
            BuildGroups();
            LoadPlugins();
        }

        internal static void LoadMetadatas()
        {
            Dictionary<string, PluginMetadata> assemblies = [];
            ScanAll(Metadatas, assemblies);
            ReplaceAssemblyMetadatas(assemblies);
        }

        /// <summary>扫描 Plugins/ 下所有 dll 与 zip，把插件元数据（含卫星资源关联）写入 <paramref name="target"/>。</summary>
        static void ScanAll(Dictionary<string, PluginMetadata> target, Dictionary<string, PluginMetadata> assemblyTarget)
        {
            var pluginsDir = new DirectoryInfo("Plugins");
            if (!pluginsDir.Exists) return;
            var culture = LanguageConfig.GetCulture();

            foreach (var dll in pluginsDir.GetFiles("*.dll", SearchOption.AllDirectories))
            {
                if (dll.Name.EndsWith(".resources.dll") && !dll.FullName.Contains(culture)) continue;
                try
                {
                    var metadata = LoadMetadata(dll.FullName, null, false);
                    assemblyTarget[metadata.PluginName] = metadata;
                    target[metadata.PluginName] = metadata;
                }
                catch (Exception ex)
                {
                    TerminalUi.LogException("Plugin", ex);
                    if (!FailedPlugins.Contains(dll.FullName)) FailedPlugins.Add(dll.FullName);
                }
            }

            foreach (var zip in pluginsDir.GetFiles("*.zip", SearchOption.TopDirectoryOnly).Select(x => x.FullName))
                LoadZipMetadatas(zip, culture, target, assemblyTarget);
        }

        /// <summary>读取单个 zip 内的主插件元数据并关联其卫星资源，写入 <paramref name="target"/>。供初始加载与热重载复用。</summary>
        static void LoadZipMetadatas(
            string zip,
            string culture,
            Dictionary<string, PluginMetadata> target,
            Dictionary<string, PluginMetadata> assemblyTarget)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zip);
                var pluginName = Path.GetFileNameWithoutExtension(zip);
                var hasMainPlugin = false;
                // 收集需要后处理的卫星资源条目
                List<ZipArchiveEntry> satelliteEntries = [];

                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!entry.FullName.Contains('/'))
                    {
                        // 主dll（根目录下）
                        using var stream = entry.Open();
                        var ms = new MemoryStream();
                        stream.CopyTo(ms);
                        ms.Position = 0;

                        try
                        {
                            var pluginPath = $"{zip}|{entry.FullName}";
                            var metadata = LoadMetadata(pluginPath, ms, true);
                            assemblyTarget[metadata.PluginName] = metadata;
                            if (string.Equals(metadata.PluginName, pluginName, StringComparison.OrdinalIgnoreCase))
                            {
                                target[metadata.PluginName] = metadata;
                                hasMainPlugin = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            TerminalUi.LogException("Plugin", ex);
                            var pluginPath = $"{zip}|{entry.FullName}";
                            if (!FailedPlugins.Contains(pluginPath)) FailedPlugins.Add(pluginPath);
                        }
                    }
                    else if (entry.FullName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) &&
                             entry.FullName.Contains(culture, StringComparison.OrdinalIgnoreCase))
                    {
                        // 卫星资源文件（子目录下），延迟处理以确保主dll元数据已加载
                        satelliteEntries.Add(entry);
                    }
                }

                if (!hasMainPlugin)
                    TerminalUi.Log("Plugin", $"插件包 {Path.GetFileName(zip)} 未找到主插件 DLL {pluginName}.dll，已跳过。", UiSeverity.Warning);

                // 关联卫星资源到对应的程序集元数据
                foreach (var entry in satelliteEntries)
                {
                    var resourceName = Path.GetFileNameWithoutExtension(entry.FullName);
                    if (resourceName.EndsWith(".resources"))
                    {
                        var assemblyName = resourceName[..^".resources".Length];
                        if (assemblyTarget.TryGetValue(assemblyName, out var metadata))
                        {
                            metadata.SatelliteEntries.Add(entry.FullName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                TerminalUi.LogException("Plugin", ex);
                if (!FailedPlugins.Contains(zip)) FailedPlugins.Add(zip);
            }
        }

        internal static PluginMetadata LoadMetadata(string path, Stream? stream, bool isFromZip)
        {
            var tempContext = new PluginLoadContext("temp");
            // 一律从内存流加载，绝不用 LoadFromAssemblyPath：后者会内存映射并锁住 DLL 文件直到该 ALC 被 GC，
            // 会妨碍开发者重建插件 DLL（热重载的前提）。与实际加载（CreateStream→LoadFromStream）保持一致。
            Assembly assembly;
            if (stream != null)
            {
                assembly = tempContext.LoadFromStream(stream);
            }
            else
            {
                using var fileStream = PluginLoadContext.LoadFileStream(path);
                assembly = tempContext.LoadFromStream(fileStream);
            }
            var loadInHost = assembly.GetCustomAttribute<LoadInHostContextAttribute>() != null;
            var sharedWith = assembly.GetCustomAttributes<SharedContextWithAttribute>().SelectMany(x => x.PluginNames).ToList();
            var metadata = new PluginMetadata(path, assembly.GetName().Name ?? string.Empty, loadInHost, sharedWith, isFromZip);
            tempContext.Unload();
            return metadata;
        }

        static void ReplaceAssemblyMetadatas(Dictionary<string, PluginMetadata> assemblies)
        {
            AssemblyMetadatas.Clear();
            foreach (var (name, metadata) in assemblies)
                AssemblyMetadatas[name] = metadata;
        }

        static bool TryGetAssemblyMetadata(string name, out PluginMetadata metadata)
            => AssemblyMetadatas.TryGetValue(name, out metadata!) || Metadatas.TryGetValue(name, out metadata!);

        static TValue? GetValueIgnoreCase<TValue>(IReadOnlyDictionary<string, TValue> source, string key)
            where TValue : class
            => source.TryGetValue(key, out var value)
                ? value
                : source.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        static string ResolvePluginName(string pluginName, params IEnumerable<string>[] sources)
        {
            foreach (var source in sources)
                if (source.FirstOrDefault(x => string.Equals(x, pluginName, StringComparison.OrdinalIgnoreCase)) is { } match)
                    return match;

            return pluginName;
        }

        internal static void BuildGroups()
        {
            // 用已有分组播种，使本方法可重入：已成组的插件（含初始加载/已热重载的）不会被重复建组。
            var pluginToGroup = new Dictionary<string, HashSet<string>>();
            foreach (var existing in ContextGroups)
                foreach (var name in existing)
                    pluginToGroup[name] = existing;

            foreach (var m in Metadatas.Values.Where(x => x.SharedContextsWith.Count != 0 && !pluginToGroup.ContainsKey(x.PluginName)))
            {
                m.SharedContextsWith.RemoveAll(x => Metadatas.TryGetValue(x, out var v) && v.LoadInHost);
                foreach (var share in m.SharedContextsWith)
                {
                    if (pluginToGroup.TryGetValue(share, out var group))
                    {
                        group.Add(m.PluginName);
                        pluginToGroup[m.PluginName] = group;
                    }
                    else
                    {
                        var newGroup = new HashSet<string> { share, m.PluginName };
                        ContextGroups.Add(newGroup);
                        pluginToGroup[share] = newGroup;
                        pluginToGroup[m.PluginName] = newGroup;
                    }
                }
            }

            foreach (var m in Metadatas.Values.Where(x => !x.LoadInHost && x.SharedContextsWith.Count == 0))
            {
                if (!pluginToGroup.ContainsKey(m.PluginName))
                    ContextGroups.Add([m.PluginName]);
            }
        }

        static string GroupKey(IEnumerable<string> group) => string.Join("&", group);

        internal static void LoadGroup(HashSet<string> group)
        {
            var missing = group.Where(name => !Metadatas.ContainsKey(name)).ToList();
            if (missing.Count != 0)
            {
                foreach (var name in group)
                    if (Metadatas.TryGetValue(name, out var present))
                    {
                        TerminalUi.Log("Plugin", $"插件 {name} 加载失败: 依赖的共享上下文插件 {string.Join("、", missing)} 未安装。", UiSeverity.Error);
                        if (!FailedPlugins.Contains(present.FilePath)) FailedPlugins.Add(present.FilePath);
                    }
                return;
            }

            var key = GroupKey(group);
            var createdContext = !Contexts.TryGetValue(key, out var ctx);
            if (createdContext)
            {
                ctx = new PluginLoadContext(key);
            }

            var loadedBefore = LoadedPlugins.ToHashSet<IPlugin>(ReferenceEqualityComparer.Instance);
            foreach (var name in group)
            {
                if (LoadIntoContext(ctx!, Metadatas[name]))
                    continue;

                if (createdContext)
                    RollBackGroupLoad(group, ctx!, loadedBefore);
                return;
            }

            if (createdContext)
                Contexts[key] = ctx!;
        }

        internal static void LoadPlugins()
        {
            foreach (var m in Metadatas.Values.Where(x => x.LoadInHost))
            {
                LoadIntoContext(AssemblyLoadContext.Default, m);
            }

            foreach (var group in ContextGroups)
                LoadGroup(group);
        }

        internal static bool LoadIntoContext(AssemblyLoadContext ctx, PluginMetadata m)
        {
            IPlugin? plugin = null;
            Assembly? assembly = null;
            string? assemblyName = null;
            var phase = "读取插件程序集";
            try
            {
                using var stream = CreateStream(m);
                assembly = ctx.LoadFromStream(stream);

                phase = "读取插件导出类型";
                var type = assembly.GetExportedTypes().FirstOrDefault(x => typeof(IPlugin).IsAssignableFrom(x));
                if (type == null)
                {
                    TerminalUi.Log("Plugin", $"插件 {m.PluginName} 加载失败: 未找到实现 {nameof(IPlugin)} 的公开类型。", UiSeverity.Error);
                    FailedPlugins.Add(m.FilePath);
                    return false;
                }

                phase = "创建插件实例";
                if (Activator.CreateInstance(type) is not IPlugin createdPlugin)
                {
                    TerminalUi.Log("Plugin", $"插件 {m.PluginName} 加载失败: 无法创建插件实例。type={type.FullName ?? type.Name}", UiSeverity.Error);
                    FailedPlugins.Add(m.FilePath);
                    return false;
                }
                plugin = createdPlugin;
                _ = GenerationFor(plugin);

                if (ShouldLoadPluginForCurrentTargets(plugin))
                {
                    phase = "注册插件入口";
                    RegisterMethods(plugin);
                    LoadedPlugins.Add(plugin);
                }

                phase = "登记插件程序集";
                assemblyName = assembly.GetName().Name;
                if (assemblyName != null) AssemblyMap[assemblyName] = assembly;
                Assemblies.Add(assembly);

                if (m.LoadInHost)
                {
                    phase = "加载宿主上下文依赖";
                    foreach (var r in assembly.GetReferencedAssemblies())
                    {
                        if (ResolveSharedAssembly(r) is not null)
                            continue;

                        if (r.Name != null && TryGetAssemblyMetadata(r.Name, out var dep) &&
                            AssemblyLoadContext.Default.Assemblies.All(a => a.GetName().Name != r.Name))
                        {
                            using var s = CreateStream(dep);
                            AssemblyLoadContext.Default.LoadFromStream(s);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                if (plugin is not null)
                {
                    try { CompleteFailedPluginLoadAsync(plugin, flush: false).GetAwaiter().GetResult(); }
                    catch (Exception cleanupEx) { TerminalUi.LogException("Plugin", cleanupEx); }
                }

                if (assemblyName is not null)
                    AssemblyMap.Remove(assemblyName);
                if (assembly is not null)
                    Assemblies.Remove(assembly);

                TerminalUi.LogException("Plugin", PluginLoadException(m, phase, ex));
                FailedPlugins.Add(m.FilePath);
                return false;
            }
        }

        static bool ShouldLoadPluginForCurrentTargets(IPlugin plugin)
            => plugin.Targets.Length == 0 ||
               plugin.Targets.Intersect(Config.Repository.Targets).Any() ||
               Config.Repository.Targets.Count == 0;

        static InvalidOperationException PluginLoadException(PluginMetadata metadata, string phase, Exception inner)
            => new(
                $"插件加载失败: plugin={metadata.PluginName}, phase={phase}",
                inner);

        static void RollBackGroupLoad(HashSet<string> group, PluginLoadContext ctx, HashSet<IPlugin> loadedBefore)
        {
            foreach (var plugin in LoadedPlugins
                         .Where(p => !loadedBefore.Contains(p) && group.Contains(InternalName(p)))
                         .ToList())
            {
                try { CompleteFailedPluginLoadAsync(plugin, flush: false).GetAwaiter().GetResult(); }
                catch (Exception ex) { TerminalUi.LogException("Plugin", ex); }
            }

            foreach (var name in group)
            {
                if (!AssemblyMap.TryGetValue(name, out var asm))
                    continue;

                Assemblies.Remove(asm);
                AssemblyMap.Remove(name);
            }

            try { ctx.Unload(); }
            catch (Exception ex) { TerminalUi.LogException("Plugin", ex); }
        }

        internal static Assembly? ResolveSharedAssembly(AssemblyName requested)
        {
            if (requested.Name is not { } name || !SharedAssemblyNames.Contains(name))
                return null;

            var shared = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, name, StringComparison.Ordinal));
            if (shared is null)
            {
                try
                {
                    shared = AssemblyLoadContext.Default.LoadFromAssemblyName(requested);
                }
                catch (Exception ex)
                {
                    throw new FileLoadException($"shared ABI assembly {requested.FullName} 必须由 Default ALC 加载，但宿主无法加载。", requested.FullName, ex);
                }
            }

            ValidateSharedAssemblyVersion(name, requested, shared.GetName());
            return shared;
        }

        static void ValidateSharedAssemblyVersion(string name, AssemblyName requested, AssemblyName actual)
        {
            if (requested.Version is null)
                return;

            if (string.Equals(name, HostAssemblyName, StringComparison.Ordinal))
            {
                if (actual.Version is not null && requested.Version > actual.Version)
                    TerminalUi.Log(
                        "Plugin",
                        $"插件依赖的宿主 ABI 版本更高，请更新 UmamusumeResponseAnalyzer: 插件请求 {requested.FullName}，当前宿主 {actual.FullName}。",
                        UiSeverity.Warning);
                return;
            }

            if (actual.Version != requested.Version)
                throw new FileLoadException(
                    $"shared ABI assembly 版本不一致: 插件请求 {requested.FullName}，宿主 Default ALC 已加载 {actual.FullName}。",
                    requested.FullName);
        }

        internal static Stream CreateStream(PluginMetadata m)
        {
            if (!m.IsFromZip)
                return PluginLoadContext.LoadFileStream(m.FilePath);

            var parts = m.FilePath.Split('|', 2);
            using var archive = ZipFile.OpenRead(parts[0]);
            var entry = archive.GetEntry(parts[1])!;
            var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }

        internal static void RegisterMethods(IPlugin plugin)
        {
            var plan = CreateRegistrationPlan(plugin);
            try
            {
                CommitRegistrationPlan(plan);
            }
            catch (Exception primary)
            {
                List<Exception> failures = [primary];
                try { RemoveAnalyzerMethods(plugin); }
                catch (Exception cleanupError) { failures.Add(cleanupError); }
                RemoveRoutes(plugin, failures);

                if (failures.Count != 1)
                    throw new AggregateException("插件 registration 提交失败。", failures);
                ExceptionDispatchInfo.Capture(primary).Throw();
                throw;
            }
        }

        static PluginRegistrationPlan CreateRegistrationPlan(IPlugin plugin)
        {
            var analyzers = new List<AnalyzerRegistration>();
            var routes = new List<RouteRegistration>();
            foreach (var method in plugin.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var analyzer in method.GetCustomAttributes<AnalyzerAttribute>())
                {
                    var registration = CreateAnalyzerRegistration(plugin, method, analyzer);
                    analyzers.Add(registration);
                }

                var route = method.GetCustomAttribute<RouteAttribute>();
                if (route is not null)
                    routes.Add(CreateRouteRegistration(plugin, method, route));
            }

            return new(analyzers, routes);
        }

        static RouteRegistration CreateRouteRegistration(IPlugin plugin, MethodInfo method, RouteAttribute route)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 1 || parameters[0].ParameterType != typeof(HttpContextBase) || method.ReturnType != typeof(Task))
            {
                var actualParameters = parameters.Length == 0
                    ? "<none>"
                    : string.Join(", ", parameters.Select(x => x.ParameterType.FullName ?? x.ParameterType.Name));
                throw new InvalidOperationException(
                    $"插件 Route 签名无效: plugin={plugin.Name} ({InternalName(plugin)}), " +
                    $"method={method.DeclaringType?.FullName}.{method.Name}, path={route.Path}, " +
                    $"expected=Task {nameof(HttpContextBase)}, actual={method.ReturnType.FullName} ({actualParameters})");
            }

            var handler = method.IsStatic
                ? method.CreateDelegate<Func<HttpContextBase, Task>>()
                : method.CreateDelegate<Func<HttpContextBase, Task>>(plugin);
            return new(plugin, route.Method, $"/{plugin.Name}/{route.Path}", handler);
        }

        static void CommitRegistrationPlan(PluginRegistrationPlan plan)
        {
            foreach (var registration in plan.Analyzers)
                CommitAnalyzerRegistration(registration);

            foreach (var route in plan.Routes)
            {
                // 包一层在途登记：使路由调用与 reload 互斥，避免在 ctx.Unload() 拆毁插件时仍有 handler 在跑（防泄漏）
                Server.Instance.Routes.PreAuthentication.Static.Add(route.Method, route.Path, route.InvokeAsync);
                if (!PluginRoutes.TryGetValue(route.Plugin, out var routes))
                {
                    routes = [];
                    PluginRoutes[route.Plugin] = routes;
                }
                routes.Add(route);
            }
        }

        internal static IPluginAnalyzerRegistry AnalyzersFor(IPlugin plugin)
            => new PluginScopedAnalyzerRegistry(plugin);

        internal static IDisposable RegisterProgrammaticAnalyzer(
            IPlugin plugin,
            AnalyzerKind kind,
            Type endpointType,
            Type payloadType,
            int priority,
            Func<AnalyzerDispatchContext, ValueTask> handler,
            string source)
        {
            using var admission = EnterPluginRegistration(plugin);
            var registration = RegisterAnalyzerCore(plugin, kind, endpointType, payloadType, priority, handler, source, method: null);
            CommitAnalyzerRegistration(registration);
            return new AnalyzerRegistrationHandle(registration);
        }

        static AnalyzerRegistration CreateAnalyzerRegistration(IPlugin plugin, MethodInfo method, AnalyzerAttribute analyzer)
        {
            var (payloadType, hasHeaders) = ValidateAttributeAnalyzerSignature(plugin, method, analyzer);
            var source = $"{method.DeclaringType?.FullName}.{method.Name}";
            Func<AnalyzerDispatchContext, ValueTask> handler = payloadType == typeof(byte[])
                ? context => InvokeAttributeAnalyzer(plugin, method, context.Payload, hasHeaders ? context.Headers : null)
                : context => InvokeAttributeAnalyzer(plugin, method, context.GetDto(), hasHeaders ? context.Headers : null);

            return RegisterAnalyzerCore(
                plugin,
                analyzer.Kind,
                analyzer.EndpointType,
                payloadType,
                analyzer.Priority,
                handler,
                source,
                method,
                attribute: analyzer);
        }

        static (Type PayloadType, bool HasHeaders) ValidateAttributeAnalyzerSignature(IPlugin plugin, MethodInfo method, AnalyzerAttribute analyzer)
        {
            var parameters = method.GetParameters();
            if ((parameters.Length is not 1 and not 2) || method.ReturnType != typeof(ValueTask))
                throw AnalyzerRegistrationException(
                    plugin,
                    method,
                    analyzer.EndpointType,
                    analyzer.Kind,
                    "<signature>",
                    "ValueTask analyzer(TPayload payload) or ValueTask analyzer(TPayload payload, GameHttpHeaders headers)",
                    DescribeAnalyzerSignature(method));

            if (parameters.Length == 2 && parameters[1].ParameterType != typeof(GameHttpHeaders))
                throw AnalyzerRegistrationException(
                    plugin,
                    method,
                    analyzer.EndpointType,
                    analyzer.Kind,
                    "<signature>",
                    "ValueTask analyzer(TPayload payload, GameHttpHeaders headers)",
                    DescribeAnalyzerSignature(method));

            return (parameters[0].ParameterType, parameters.Length == 2);
        }

        static AnalyzerRegistration RegisterAnalyzerCore(
            IPlugin plugin,
            AnalyzerKind kind,
            Type endpointType,
            Type payloadType,
            int priority,
            Func<AnalyzerDispatchContext, ValueTask> handler,
            string source,
            MethodInfo? method,
            AnalyzerAttribute? attribute = null)
        {
            if (!GameEndpointCatalog.ByEndpointType.TryGetValue(endpointType, out var endpoint))
                throw AnalyzerRegistrationException(
                    plugin,
                    method,
                    endpointType,
                    kind,
                    "<signature>",
                    "catalog endpoint",
                    $"未在 {nameof(GameEndpointCatalog)}.{nameof(GameEndpointCatalog.ByEndpointType)} 注册");

            var expected = payloadType == typeof(byte[])
                ? typeof(byte[])
                : kind == AnalyzerKind.Request
                    ? endpoint.RequestType
                    : endpoint.ResponseType;
            if (payloadType != expected)
                throw AnalyzerRegistrationException(
                    plugin,
                    method,
                    endpointType,
                    kind,
                    payloadType == typeof(byte[]) ? "raw" : "DTO",
                    expected.FullName ?? expected.Name,
                    method is null
                        ? payloadType.FullName ?? payloadType.Name
                        : DescribeAnalyzerSignature(method));

            return new(
                plugin,
                method,
                endpointType,
                kind,
                priority,
                handler,
                attribute is null ? source : $"{source} [{attribute.GetType().Name}]");
        }

        static string DescribeAnalyzerSignature(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var parameterText = parameters.Length == 0
                ? "<none>"
                : string.Join(", ", parameters.Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
            var asyncVoid = method.GetCustomAttribute<AsyncStateMachineAttribute>() is null ? string.Empty : ", async-state-machine";
            return $"return={method.ReturnType.FullName ?? method.ReturnType.Name}, parameters=({parameterText}){asyncVoid}";
        }

        static ValueTask InvokeAttributeAnalyzer(IPlugin plugin, MethodInfo method, object payload, GameHttpHeaders? headers)
        {
            var target = method.IsStatic ? null : plugin;
            object?[] args = headers is null ? [payload] : [payload, headers];
            return (ValueTask)method.Invoke(target, args)!;
        }

        static void CommitAnalyzerRegistration(AnalyzerRegistration registration)
        {
            lock (AnalyzerGate)
            {
                var dict = registration.Kind == AnalyzerKind.Response ? ResponseAnalyzerMethods : RequestAnalyzerMethods;
                if (!dict.TryGetValue(registration.Priority, out var list))
                {
                    list = [];
                    dict[registration.Priority] = list;
                }

                list.Add(registration);
            }
        }

        internal static void RemoveAnalyzerRegistration(AnalyzerRegistration registration)
        {
            lock (AnalyzerGate)
                RemoveAnalyzerRegistrationLocked(registration);
        }

        static void RemoveAnalyzerRegistrationLocked(AnalyzerRegistration registration)
        {
            var dict = registration.Kind == AnalyzerKind.Response ? ResponseAnalyzerMethods : RequestAnalyzerMethods;
            if (!dict.TryGetValue(registration.Priority, out var list))
                return;

            list.Remove(registration);
            if (list.Count == 0)
                dict.Remove(registration.Priority);
        }

        internal static PluginCallbackSnapshot<AnalyzerRegistration> SnapshotAnalyzerRegistrations(AnalyzerKind kind, Type endpointType)
        {
            lock (AnalyzerGate)
            {
                var dict = kind == AnalyzerKind.Request ? RequestAnalyzerMethods : ResponseAnalyzerMethods;
                return PluginCallbackSnapshot<AnalyzerRegistration>.Create(
                    dict
                    .SelectMany(x => x.Value)
                    .Where(x => x.EndpointType == endpointType),
                    static registration => registration.Plugin);
            }
        }

        static InvalidOperationException AnalyzerRegistrationException(
            IPlugin plugin,
            MethodInfo? method,
            Type endpointType,
            AnalyzerKind kind,
            string payload,
            string expected,
            string actual)
        {
            var methodName = method is null
                ? "<programmatic>"
                : $"{method.DeclaringType?.FullName}.{method.Name}";
            return new InvalidOperationException(
                $"插件 analyzer 签名无效: plugin={plugin.Name} ({InternalName(plugin)}), " +
                $"method={methodName}, endpoint={endpointType.FullName}, kind={kind}, payload={payload}, " +
                $"expected={expected}, actual={actual}");
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
                TerminalUi.LogException(
                    "Plugin",
                    new InvalidOperationException($"插件初始化失败: plugin={plugin.Name} ({internalName})", ex));
                if (!FailedPlugins.Contains(failedPlugin))
                    FailedPlugins.Add(failedPlugin);
                if (committed)
                {
                    try { CompleteFailedPluginLoadAsync(plugin, flush: true).GetAwaiter().GetResult(); }
                    catch (Exception cleanupEx) { TerminalUi.LogException("Plugin", cleanupEx); }
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

        // ── 热重载 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 批量应用插件重载，逐项返回成功、失败或需重启。
        /// 新安装的共享上下文成员会先把已加载的同组插件排入重载顺序，避免共享锚点被加载进两个 ALC。
        /// </summary>
        public static async Task<IReadOnlyList<PluginLifecycleResult>> ReloadPluginsAsync(params string[] pluginNames)
        {
            var requested = pluginNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (requested.Count == 0) return [];

            using var transaction = EnterReloadTransaction();
            var startedPluginBatches = new List<IPlugin[]>();
            var (scanned, assemblyMetadatas) = ScanPluginMetadata();
            List<string> ordered;
            StateLock.EnterWriteLock();
            try
            {
                ReplaceAssemblyMetadatas(assemblyMetadatas);
                requested = requested
                    .Select(name => ResolvePluginName(name, scanned.Keys, Metadatas.Keys, LoadedPlugins.Select(InternalName)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                ordered = BuildReloadOrder(requested, scanned);
            }
            finally
            {
                StateLock.ExitWriteLock();
            }

            var outcomes = new Dictionary<string, PluginLifecycleOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ordered)
            {
                try
                {
                    outcomes[name] = await ReloadPluginAsync(name, scanned, outcomes, startedPluginBatches);
                }
                catch (Exception ex)
                {
                    outcomes[name] = PluginLifecycleOutcome.Failed;
                    TerminalUi.LogException("Plugin", ex);
#if DEBUG
                    throw;
#endif
                }
            }

            foreach (var plugins in startedPluginBatches)
            {
                try
                {
                    await TriggerStartedForPluginsAsync(plugins);
                }
                catch (Exception ex)
                {
                    TerminalUi.LogException("Plugin", ex);
#if DEBUG
                    throw;
#endif
                }
            }

            return requested
                .Select(name => new PluginLifecycleResult(name, outcomes[name]))
                .ToList();
        }

        internal static async Task<IReadOnlyList<PluginLifecycleResult>> LoadPluginsAsync(params string[] pluginNames)
        {
            var requested = pluginNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (requested.Count == 0) return [];

            using var transaction = EnterReloadTransaction();
            var startedPluginBatches = new List<IPlugin[]>();
            var (scanned, assemblyMetadatas) = ScanPluginMetadata();
            List<(string Raw, string Name, bool Available, bool Loaded)> resolved;
            List<string> ordered;
            StateLock.EnterWriteLock();
            try
            {
                ReplaceAssemblyMetadatas(assemblyMetadatas);
                resolved = requested.Select(raw =>
                {
                    var name = ResolvePluginName(raw, scanned.Keys, Metadatas.Keys, LoadedPlugins.Select(InternalName));
                    return (
                        raw,
                        name,
                        scanned.ContainsKey(name) || Metadatas.ContainsKey(name),
                        LoadedPlugins.Any(plugin => string.Equals(InternalName(plugin), name, StringComparison.OrdinalIgnoreCase)));
                }).ToList();
                ordered = BuildReloadOrder(
                    resolved.Where(item => item.Available && !item.Loaded).Select(item => item.Name).ToList(),
                    scanned);
            }
            finally
            {
                StateLock.ExitWriteLock();
            }

            var outcomes = new Dictionary<string, PluginLifecycleOutcome>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ordered)
                outcomes[name] = await ReloadPluginAsync(name, scanned, outcomes, startedPluginBatches);

            foreach (var item in resolved)
            {
                if (outcomes.ContainsKey(item.Name))
                    continue;
                if (item.Loaded)
                {
                    outcomes[item.Name] = PluginLifecycleOutcome.Succeeded;
                    continue;
                }
                if (!item.Available)
                {
                    TerminalUi.Log("Plugin", $"插件 {item.Raw} 不存在，无法加载。", UiSeverity.Warning);
                    outcomes[item.Name] = PluginLifecycleOutcome.Failed;
                    continue;
                }
                outcomes[item.Name] = IsPluginLoaded(item.Name)
                    ? PluginLifecycleOutcome.Succeeded
                    : PluginLifecycleOutcome.Failed;
            }

            foreach (var plugins in startedPluginBatches)
                await TriggerStartedForPluginsAsync(plugins);

            return resolved
                .Select(item => new PluginLifecycleResult(item.Raw, outcomes[item.Name]))
                .ToList();
        }

        internal static async Task<IReadOnlyList<PluginLifecycleResult>> UnloadPluginsAsync(params string[] pluginNames)
        {
            var requested = pluginNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (requested.Count == 0) return [];

            using var transaction = EnterReloadTransaction();
            var outcomes = new Dictionary<string, PluginLifecycleOutcome>(StringComparer.OrdinalIgnoreCase);
            List<Exception> failures = [];
            foreach (var rawName in requested)
            {
                string name;
                StateLock.EnterReadLock();
                try
                {
                    name = ResolvePluginName(
                        rawName,
                        Metadatas.Keys,
                        LoadedPlugins.Select(InternalName),
                        ContextGroups.SelectMany(x => x));
                }
                finally
                {
                    StateLock.ExitReadLock();
                }

                try
                {
                    outcomes[rawName] = await UnloadPluginAsync(name, outcomes);
                }
                catch (Exception ex)
                {
                    outcomes[rawName] = PluginLifecycleOutcome.Failed;
                    failures.Add(new InvalidOperationException($"插件卸载失败: plugin={name}", ex));
                }
            }

            if (failures.Count != 0)
                throw new AggregateException("插件批量卸载失败。", failures);

            return requested
                .Select(name => new PluginLifecycleResult(name, outcomes[name]))
                .ToList();
        }

        static IDisposable EnterReloadTransaction()
        {
            if (PluginCallbackDepth.Value != 0)
                throw new InvalidOperationException("插件回调内禁止执行热重载；请在当前回调返回后由宿主侧重新发起 reload。");

            lock (ReloadTransactionGate)
            {
                if (shuttingDown)
                    throw new InvalidOperationException("插件系统正在关闭，拒绝执行热重载。");
                if (reloadTransactionActive)
                    throw new InvalidOperationException("已有插件热重载事务正在运行，拒绝并发或重入 reload。");

                reloadTransactionActive = true;
                reloadCompleted = NewTaskSource();
                return new ReloadTransaction();
            }
        }

        sealed class ReloadTransaction : IDisposable
        {
            int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                TaskCompletionSource? completion;
                lock (ReloadTransactionGate)
                {
                    reloadTransactionActive = false;
                    completion = reloadCompleted;
                    reloadCompleted = null;
                }
                completion?.TrySetResult();
            }
        }

        internal static IDisposable EnterPluginCallbackScope()
        {
            PluginCallbackDepth.Value++;
            return new PluginCallbackScope();
        }

        sealed class PluginCallbackScope : IDisposable
        {
            bool disposed;

            public void Dispose()
            {
                if (disposed)
                    return;

                PluginCallbackDepth.Value--;
                disposed = true;
            }
        }

        static (Dictionary<string, PluginMetadata> Plugins, Dictionary<string, PluginMetadata> Assemblies) ScanPluginMetadata()
        {
            Dictionary<string, PluginMetadata> scanned = [];
            Dictionary<string, PluginMetadata> assemblies = [];
            ScanAll(scanned, assemblies);
            return (scanned, assemblies);
        }

        static Dictionary<string, PluginMetadata> ScanPluginMetadataForStatus()
        {
            Dictionary<string, PluginMetadata> scanned = [];
            Dictionary<string, PluginMetadata> assemblies = [];
            ScanAll(scanned, assemblies);
            return scanned;
        }

        static List<string> BuildReloadOrder(IReadOnlyList<string> requested, IReadOnlyDictionary<string, PluginMetadata> scanned)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in requested)
            {
                if (scanned.TryGetValue(name, out var metadata))
                {
                    foreach (var share in metadata.SharedContextsWith)
                    {
                        var group = ContextGroups.FirstOrDefault(g => g.Contains(share, StringComparer.OrdinalIgnoreCase));
                        if (group == null) continue;
                        foreach (var member in group)
                            if (seen.Add(member)) ordered.Add(member);
                    }
                }
                if (seen.Add(name)) ordered.Add(name);
            }
            return ordered;
        }

        static async Task<PluginLifecycleOutcome> ReloadPluginAsync(
            string pluginName,
            IReadOnlyDictionary<string, PluginMetadata> scanned,
            Dictionary<string, PluginLifecycleOutcome> outcomes,
            List<IPlugin[]> startedPluginBatches)
        {
            if (outcomes.TryGetValue(pluginName, out var prior)) return prior;

            var affectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pluginName };
            PendingPluginUnload? unload = null;
            var loadInHost = false;
            StateLock.EnterWriteLock();
            try
            {
                var loadedPlugin = LoadedPlugins.FirstOrDefault(plugin =>
                    string.Equals(InternalName(plugin), pluginName, StringComparison.OrdinalIgnoreCase));
                var existing = GetValueIgnoreCase(Metadatas, pluginName);
                loadInHost = loadedPlugin is not null && ReferenceEquals(
                    AssemblyLoadContext.GetLoadContext(loadedPlugin.GetType().Assembly),
                    AssemblyLoadContext.Default);
                loadInHost |= existing is { LoadInHost: true };
                var group = existing is null
                    ? null
                    : ContextGroups.FirstOrDefault(x => x.Contains(pluginName, StringComparer.OrdinalIgnoreCase));
                if (group is not null)
                    affectedNames.UnionWith(group);

                loadInHost |= affectedNames.Any(name => scanned.TryGetValue(name, out var metadata) && metadata.LoadInHost);
                if (!loadInHost && group is not null && Contexts.ContainsKey(GroupKey(group)))
                    unload = PrepareUnloadGroupLocked(group);
            }
            finally
            {
                StateLock.ExitWriteLock();
            }

            if (loadInHost)
            {
                TerminalUi.Log("Plugin", $"插件 {pluginName} 在宿主上下文加载，不支持热重载，请重启。", UiSeverity.Warning);
                foreach (var name in affectedNames)
                    outcomes[name] = PluginLifecycleOutcome.RestartRequired;
                return PluginLifecycleOutcome.RestartRequired;
            }

            if (unload is not null)
            {
                await CompletePendingUnloadsAsync([unload], clearAll: false);
                unload = null;
            }

            bool missing;
            bool becameLoadInHost;
            StateLock.EnterWriteLock();
            try
            {
                MergeUnloadedMetadatas(scanned);
                missing = !Metadatas.ContainsKey(pluginName);
                becameLoadInHost = !missing && Metadatas[pluginName].LoadInHost;
                BuildGroups();
            }
            finally
            {
                StateLock.ExitWriteLock();
            }

            if (missing)
            {
                TerminalUi.Log("Plugin", $"插件 {pluginName} 的文件已不存在，已卸载。", UiSeverity.Warning);
                await LoadAffectedGroupsAsync(affectedNames, outcomes, startedPluginBatches);
                return outcomes[pluginName] = PluginLifecycleOutcome.Succeeded;
            }

            if (becameLoadInHost)
            {
                TerminalUi.Log("Plugin", $"插件 {pluginName} 在宿主上下文加载，不支持热重载，请重启。", UiSeverity.Warning);
                return outcomes[pluginName] = PluginLifecycleOutcome.RestartRequired;
            }

            await LoadAffectedGroupsAsync(affectedNames, outcomes, startedPluginBatches);

            var loaded = IsPluginLoaded(pluginName);
            if (loaded)
                TerminalUi.Log("Plugin", $"插件 {pluginName} 已重载。", UiSeverity.Success);
            return outcomes[pluginName] = loaded
                ? PluginLifecycleOutcome.Succeeded
                : PluginLifecycleOutcome.Failed;
        }

        static async Task<PluginLifecycleOutcome> UnloadPluginAsync(
            string pluginName,
            Dictionary<string, PluginLifecycleOutcome> outcomes)
        {
            if (outcomes.TryGetValue(pluginName, out var prior)) return prior;

            PendingPluginUnload? unload = null;
            HashSet<string>? group = null;
            var loadInHost = false;
            var notLoaded = false;
            StateLock.EnterWriteLock();
            try
            {
                var loadedPlugin = LoadedPlugins.FirstOrDefault(plugin =>
                    string.Equals(InternalName(plugin), pluginName, StringComparison.OrdinalIgnoreCase));
                var existing = GetValueIgnoreCase(Metadatas, pluginName);
                loadInHost = loadedPlugin is not null && ReferenceEquals(
                    AssemblyLoadContext.GetLoadContext(loadedPlugin.GetType().Assembly),
                    AssemblyLoadContext.Default);
                group = ContextGroups.FirstOrDefault(x => x.Contains(pluginName, StringComparer.OrdinalIgnoreCase));
                if (!loadInHost && group is null)
                {
                    if (loadedPlugin is not null)
                        throw new InvalidOperationException($"已加载插件缺少 context group: {pluginName}");
                    if (existing is not null)
                        Metadatas.Remove(existing.PluginName);
                    notLoaded = true;
                }
                else if (!loadInHost)
                {
                    var unloadGroup = group!;
                    if (unloadGroup.Any(name => GetValueIgnoreCase(Metadatas, name) is { LoadInHost: true }))
                    {
                        loadInHost = true;
                    }
                    else if (Contexts.ContainsKey(GroupKey(unloadGroup)))
                    {
                        unload = PrepareUnloadGroupLocked(unloadGroup);
                    }
                    else
                    {
                        foreach (var name in unloadGroup)
                            Metadatas.Remove(name);
                        ContextGroups.RemoveAll(x => x.SetEquals(unloadGroup));
                    }
                }
            }
            finally
            {
                StateLock.ExitWriteLock();
            }

            if (loadInHost)
            {
                TerminalUi.Log("Plugin", $"插件 {string.Join("、", group ?? [pluginName])} 在宿主上下文加载，不支持卸载，请重启。", UiSeverity.Warning);
                foreach (var name in group ?? [pluginName])
                    outcomes[name] = PluginLifecycleOutcome.RestartRequired;
                return outcomes[pluginName] = PluginLifecycleOutcome.RestartRequired;
            }

            if (notLoaded)
            {
                TerminalUi.Log("Plugin", $"插件 {pluginName} 未加载。", UiSeverity.Warning);
                return outcomes[pluginName] = PluginLifecycleOutcome.Succeeded;
            }

            if (unload is not null)
            {
                foreach (var name in group!)
                    outcomes[name] = PluginLifecycleOutcome.Failed;
                await CompletePendingUnloadsAsync([unload], clearAll: false);
                unload = null;
            }

            foreach (var name in group!)
                outcomes[name] = PluginLifecycleOutcome.Succeeded;

            TerminalUi.Log("Plugin", $"插件 {pluginName} 已卸载。", UiSeverity.Success);
            return outcomes[pluginName] = PluginLifecycleOutcome.Succeeded;
        }

        /// <summary>加载受本轮重载影响且尚无 ALC 的上下文组，对新实例调用 Initialize，并补发一次启动事件。</summary>
        static async Task LoadAffectedGroupsAsync(
            IEnumerable<string> affectedNames,
            Dictionary<string, PluginLifecycleOutcome> outcomes,
            List<IPlugin[]> startedPluginBatches)
        {
            bool initialize;
            bool deliverStarted;
            lock (ReloadTransactionGate)
            {
                initialize = initializationComplete;
                deliverStarted = startedComplete;
            }

            var affected = affectedNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<HashSet<string>> pendingGroups;
            StateLock.EnterWriteLock();
            try
            {
                pendingGroups = ContextGroups
                    .Where(group => group.Any(affected.Contains) && !Contexts.ContainsKey(GroupKey(group)))
                    .Select(group => group.ToHashSet(StringComparer.OrdinalIgnoreCase))
                    .ToList();
                foreach (var group in pendingGroups)
                    foreach (var name in group)
                        if (Metadatas.TryGetValue(name, out var metadata))
                            FailedPlugins.Remove(metadata.FilePath);
            }
            finally
            {
                StateLock.ExitWriteLock();
            }

            foreach (var group in pendingGroups)
            {
                var staged = await StageGroupLoadAsync(group);
                if (staged is null)
                {
                    foreach (var name in group.Where(Metadatas.ContainsKey))
                        outcomes[name] = PluginLifecycleOutcome.Failed;
                    continue;
                }

                if (initialize)
                {
                    if (!InitializeStagedPlugins(staged))
                    {
                        foreach (var name in group.Where(Metadatas.ContainsKey))
                            outcomes[name] = PluginLifecycleOutcome.Failed;
                        await DisposeStagedGroupAsync(staged, flush: true);
                        continue;
                    }
                }

                try
                {
                    CommitStagedGroupLoad(staged);
                }
                catch (Exception commitError)
                {
                    foreach (var name in group.Where(Metadatas.ContainsKey))
                        outcomes[name] = PluginLifecycleOutcome.Failed;
                    try
                    {
                        await DisposeStagedGroupAsync(staged, flush: initialize);
                    }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException(
                            "插件 staged group 提交及清理失败。",
                            commitError,
                            cleanupError);
                    }
                    ExceptionDispatchInfo.Capture(commitError).Throw();
                    throw;
                }

                if (initialize && staged.Plugins.Count != 0)
                {
                    foreach (var plugin in staged.Plugins)
                        RequireGeneration(plugin.Plugin).Open();
                    if (deliverStarted)
                        startedPluginBatches.Add([.. staged.Plugins.Select(x => x.Plugin)]);
                }

                foreach (var name in group.Where(Metadatas.ContainsKey))
                    outcomes[name] = IsPluginLoaded(name)
                        ? PluginLifecycleOutcome.Succeeded
                        : PluginLifecycleOutcome.Failed;
            }
        }

        static async Task<StagedGroupLoad?> StageGroupLoadAsync(HashSet<string> group)
        {
            var missing = group.Where(name => !Metadatas.ContainsKey(name)).ToList();
            if (missing.Count != 0)
            {
                foreach (var name in group)
                    if (Metadatas.TryGetValue(name, out var present))
                    {
                        TerminalUi.Log("Plugin", $"插件 {name} 加载失败: 依赖的共享上下文插件 {string.Join("、", missing)} 未安装。", UiSeverity.Error);
                        if (!FailedPlugins.Contains(present.FilePath)) FailedPlugins.Add(present.FilePath);
                    }
                return null;
            }

            var key = GroupKey(group);
            var ctx = new PluginLoadContext(key);
            var staged = new StagedGroupLoad(group, key, ctx, [], []);

            foreach (var name in group)
            {
                if (await StageIntoContextAsync(staged, Metadatas[name]))
                    continue;

                await DisposeStagedGroupAsync(staged, flush: false);
                return null;
            }

            return staged;
        }

        static async Task<bool> StageIntoContextAsync(StagedGroupLoad staged, PluginMetadata metadata)
        {
            IPlugin? plugin = null;
            var phase = "读取插件程序集";
            try
            {
                using var stream = CreateStream(metadata);
                var assembly = staged.Context.LoadFromStream(stream);
                if (assembly.GetName().Name is { } assemblyName)
                {
                    staged.Context.LocalAssemblyMap[assemblyName] = assembly;
                    staged.Assemblies.Add(new(assemblyName, assembly));
                }

                phase = "读取插件导出类型";
                var type = assembly.GetExportedTypes().FirstOrDefault(x => typeof(IPlugin).IsAssignableFrom(x));
                if (type is null)
                {
                    TerminalUi.Log("Plugin", $"插件 {metadata.PluginName} 加载失败: 未找到实现 {nameof(IPlugin)} 的公开类型。", UiSeverity.Error);
                    FailedPlugins.Add(metadata.FilePath);
                    return false;
                }

                phase = "创建插件实例";
                if (Activator.CreateInstance(type) is not IPlugin createdPlugin)
                {
                    TerminalUi.Log("Plugin", $"插件 {metadata.PluginName} 加载失败: 无法创建插件实例。type={type.FullName ?? type.Name}", UiSeverity.Error);
                    FailedPlugins.Add(metadata.FilePath);
                    return false;
                }
                plugin = createdPlugin;
                _ = GenerationFor(plugin);

                if (!ShouldLoadPluginForCurrentTargets(plugin))
                    return true;

                phase = "注册插件入口";
                staged.Plugins.Add(new(plugin, CreateRegistrationPlan(plugin)));
                return true;
            }
            catch (Exception ex)
            {
                if (plugin is not null)
                {
                    try { await CompleteFailedPluginLoadAsync(plugin, flush: false); }
                    catch (Exception cleanupEx) { TerminalUi.LogException("Plugin", cleanupEx); }
                }

                TerminalUi.LogException("Plugin", PluginLoadException(metadata, phase, ex));
                FailedPlugins.Add(metadata.FilePath);
                return false;
            }
        }

        static bool InitializeStagedPlugins(StagedGroupLoad staged)
        {
            foreach (var plugin in staged.Plugins)
                if (!TryInitializePlugin(plugin.Plugin, committed: false))
                    return false;

            return true;
        }

        static void CommitStagedGroupLoad(StagedGroupLoad staged)
        {
            var analyzers = staged.Plugins.SelectMany(plugin => plugin.Plan.Analyzers).ToList();
            var routes = staged.Plugins.SelectMany(plugin => plugin.Plan.Routes).ToList();
            var addedRouteCount = 0;
            try
            {
                foreach (var analyzer in analyzers)
                    CommitAnalyzerRegistration(analyzer);

                foreach (var route in routes)
                {
                    Server.Instance.Routes.PreAuthentication.Static.Add(route.Method, route.Path, route.InvokeAsync);
                    addedRouteCount++;
                }

                StateLock.EnterWriteLock();
                try
                {
                    foreach (var assembly in staged.Assemblies)
                    {
                        AssemblyMap.Add(assembly.Name, assembly.Assembly);
                        Assemblies.Add(assembly.Assembly);
                    }

                    foreach (var plugin in staged.Plugins)
                    {
                        LoadedPlugins.Add(plugin.Plugin);
                        if (plugin.Plan.Routes.Count != 0)
                            PluginRoutes.Add(plugin.Plugin, [.. plugin.Plan.Routes]);
                    }

                    Contexts.Add(staged.Key, staged.Context);
                }
                finally
                {
                    StateLock.ExitWriteLock();
                }
            }
            catch (Exception commitError)
            {
                List<Exception> failures = [commitError];
                try
                {
                    StateLock.EnterWriteLock();
                    try
                    {
                        if (Contexts.TryGetValue(staged.Key, out var context) && ReferenceEquals(context, staged.Context))
                            Contexts.Remove(staged.Key);
                        foreach (var plugin in staged.Plugins)
                        {
                            var index = LoadedPlugins.FindIndex(candidate => ReferenceEquals(candidate, plugin.Plugin));
                            if (index >= 0)
                                LoadedPlugins.RemoveAt(index);
                            PluginRoutes.Remove(plugin.Plugin);
                        }
                        foreach (var assembly in staged.Assemblies)
                        {
                            if (AssemblyMap.TryGetValue(assembly.Name, out var mapped) && ReferenceEquals(mapped, assembly.Assembly))
                                AssemblyMap.Remove(assembly.Name);
                            Assemblies.RemoveAll(candidate => ReferenceEquals(candidate, assembly.Assembly));
                        }
                    }
                    finally { StateLock.ExitWriteLock(); }
                }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException("插件 staged state 回滚失败。", ex));
                }
                foreach (var analyzer in analyzers)
                {
                    try { RemoveAnalyzerRegistration(analyzer); }
                    catch (Exception ex) { failures.Add(ex); }
                }
                for (var i = 0; i < addedRouteCount; i++)
                    RemoveRoute(routes[i], "StagedRegistrationRollback", failures);

                if (failures.Count != 1)
                    throw new AggregateException("插件 staged registration 提交失败。", failures);
                ExceptionDispatchInfo.Capture(commitError).Throw();
                throw;
            }
        }

        static async Task DisposeStagedGroupAsync(StagedGroupLoad staged, bool flush)
        {
            var generations = staged.Plugins.Select(x => RequireGeneration(x.Plugin)).ToList();
            foreach (var generation in generations)
                _ = generation.Close();
            await CompletePendingUnloadsAsync(
                [new(staged.Names, staged.Key, staged.Context, generations, false)],
                clearAll: false,
                flush: flush);
        }

        static void MergeUnloadedMetadatas(IReadOnlyDictionary<string, PluginMetadata> scanned)
        {
            foreach (var (name, m) in scanned)
            {
                if (Metadatas.ContainsKey(name)) continue; // 仍加载中的插件不动
                Metadatas[name] = m;
            }
        }

        static bool IsPluginLoaded(string pluginName)
        {
            StateLock.EnterReadLock();
            try
            {
                return LoadedPlugins.Any(plugin =>
                    string.Equals(InternalName(plugin), pluginName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                StateLock.ExitReadLock();
            }
        }

        static PendingPluginUnload PrepareUnloadGroupLocked(HashSet<string> group)
        {
            var key = GroupKey(group);
            var generations = LoadedPlugins
                .Where(plugin => group.Contains(InternalName(plugin)))
                .Select(RequireGeneration)
                .ToList();
            foreach (var generation in generations)
                _ = generation.Close();
            return new(
                group.ToHashSet(StringComparer.OrdinalIgnoreCase),
                key,
                Contexts[key],
                generations,
                true);
        }

        static Task CompleteFailedPluginLoadAsync(IPlugin plugin, bool flush)
        {
            var generation = GenerationFor(plugin);
            _ = generation.Close();
            return CompletePendingUnloadsAsync(
                [new([InternalName(plugin)], null, null, [generation], false)],
                clearAll: false,
                flush: flush);
        }

        static async Task CompletePendingUnloadsAsync(
            IReadOnlyList<PendingPluginUnload> pendingUnloads,
            bool clearAll,
            bool flush = true)
        {
            var generations = pendingUnloads
                .SelectMany(unload => unload.Generations)
                .Distinct()
                .ToList();
            await Task.WhenAll(generations.Select(generation => generation.Close()));

            List<Exception> failures = [];
            var plugins = generations
                .Select(generation => generation.Plugin)
                .ToList();
            for (var i = 0; i < plugins.Count; i++)
            {
                var pluginName = InternalName(plugins[i]);
                DisposePluginForUnload(plugins[i], pluginName, failures);

                if (flush)
                {
                    try
                    {
                        await TerminalUi.RequireHost().FlushAsync();
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new InvalidOperationException(
                            $"插件清理失败: plugin={pluginName}, phase=Flush",
                            ex));
                    }
                }
            }

            FinishPendingUnloads(pendingUnloads, clearAll, generations, plugins, failures);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void DisposePluginForUnload(
            IPlugin plugin,
            string pluginName,
            List<Exception> failures)
        {
            using (HotkeyManager.RegisterScope(plugin))
            using (EnterPluginCallbackScope())
            {
                try
                {
                    plugin.Dispose();
                }
                catch (Exception ex)
                {
                    var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
                    string message;
                    try { message = ex.Message; }
                    catch (Exception messageError)
                    {
                        var messageErrorType = messageError.GetType().FullName ?? messageError.GetType().Name;
                        message = $"<读取 Message 失败: {messageErrorType}>";
                    }

                    failures.Add(new InvalidOperationException(
                        $"插件清理失败: plugin={pluginName}, phase=Dispose, " +
                        $"exception={exceptionType}, message={message}"));
                }
            }

            try
            {
                HotkeyManager.UnregisterByOwner(plugin);
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"插件清理失败: plugin={pluginName}, phase=UnregisterHotkeys",
                    ex));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void FinishPendingUnloads(
            IReadOnlyList<PendingPluginUnload> pendingUnloads,
            bool clearAll,
            List<PluginGeneration> generations,
            List<IPlugin> plugins,
            List<Exception> failures)
        {
            List<RouteRegistration> routes = [];
            StateLock.EnterWriteLock();
            try
            {
                foreach (var plugin in plugins)
                {
                    var index = LoadedPlugins.FindIndex(candidate => ReferenceEquals(candidate, plugin));
                    if (index >= 0)
                        LoadedPlugins.RemoveAt(index);
                    RemoveAnalyzerMethods(plugin);
                    if (PluginRoutes.Remove(plugin, out var pluginRoutes))
                        routes.AddRange(pluginRoutes);
                    PluginGenerations.Remove(plugin);
                }

                foreach (var unload in pendingUnloads)
                {
                    if (unload.Context is not null)
                    {
                        foreach (var name in AssemblyMap
                                     .Where(pair => ReferenceEquals(AssemblyLoadContext.GetLoadContext(pair.Value), unload.Context))
                                     .Select(pair => pair.Key)
                                     .ToList())
                            AssemblyMap.Remove(name);
                        Assemblies.RemoveAll(assembly =>
                            ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), unload.Context));
                    }

                    if (unload.Key is not null)
                        Contexts.Remove(unload.Key);
                    if (!unload.RemoveGroupState)
                        continue;
                    foreach (var name in unload.Names)
                        Metadatas.Remove(name);
                    ContextGroups.RemoveAll(group => group.SetEquals(unload.Names));
                }

                if (clearAll)
                {
                    foreach (var pluginRoutes in PluginRoutes.Values)
                        routes.AddRange(pluginRoutes);
                    LoadedPlugins.Clear();
                    Metadatas.Clear();
                    AssemblyMetadatas.Clear();
                    FailedPlugins.Clear();
                    ContextGroups.Clear();
                    Contexts.Clear();
                    AssemblyMap.Clear();
                    Assemblies.Clear();
                    PluginRoutes.Clear();
                    lock (AnalyzerGate)
                    {
                        RequestAnalyzerMethods.Clear();
                        ResponseAnalyzerMethods.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException("插件 registration/context 清理失败。", ex));
            }
            finally
            {
                StateLock.ExitWriteLock();
            }

            foreach (var plugin in plugins)
            {
                try { DisposeHostEventSubscriptions(plugin); }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException(
                        $"插件清理失败: plugin={InternalName(plugin)}, phase=HostEvents",
                        ex));
                }
            }
            if (clearAll)
            {
                try { ClearHostEventSubscriptions(); }
                catch (Exception ex) { failures.Add(new InvalidOperationException("插件 HostEvents 清理失败。", ex)); }
            }

            foreach (var route in routes)
                RemoveRoute(route, "Unload", failures);

            var contexts = pendingUnloads
                .Select(unload => unload.Context)
                .Where(context => context is not null)
                .Distinct()
                .ToList();
            foreach (var context in contexts)
            {
                try { context!.Unload(); }
                catch (Exception ex)
                {
                    failures.Add(new InvalidOperationException(
                        $"插件 ALC 清理失败: context={context!.Name}",
                        ex));
                }
            }

            foreach (var unload in pendingUnloads)
                unload.Release();
            contexts.Clear();
            routes.Clear();
            plugins.Clear();
            generations.Clear();

            if (failures.Count != 0)
                throw new AggregateException("插件清理失败。", failures);
        }

        static void RemoveAnalyzerMethods(IPlugin plugin)
        {
            lock (AnalyzerGate)
            {
                foreach (var dict in (SortedDictionary<int, List<AnalyzerRegistration>>[])[RequestAnalyzerMethods, ResponseAnalyzerMethods])
                {
                    foreach (var priority in dict.Keys.ToList())
                    {
                        var list = dict[priority];
                        list.RemoveAll(x => ReferenceEquals(x.Plugin, plugin));
                        if (list.Count == 0) dict.Remove(priority);
                    }
                }
            }
        }

        static void RemoveRoutes(IPlugin plugin, List<Exception> failures)
        {
            if (!PluginRoutes.Remove(plugin, out var routes))
                return;
            foreach (var route in routes)
                RemoveRoute(route, "RegistrationRollback", failures);
        }

        static void RemoveRoute(
            RouteRegistration route,
            string phase,
            List<Exception> failures)
        {
            try
            {
                route.MarkRemoved();
                // StaticRouteManager.Remove 按 (method, path) 精确移除，清掉钉住插件程序集的 handler 闭包
                if (Server.Instance.Routes.PreAuthentication.Static.Exists(route.Method, route.Path))
                    Server.Instance.Routes.PreAuthentication.Static.Remove(route.Method, route.Path);
            }
            catch (Exception cleanupError)
            {
                failures.Add(new InvalidOperationException(
                    $"插件 route 清理失败: plugin={InternalName(route.Plugin)}, phase={phase}, path={route.Path}",
                    cleanupError));
            }
        }

        internal class PluginLoadContext(string name) : AssemblyLoadContext(name, true)
        {
            internal Dictionary<string, Assembly> LocalAssemblyMap { get; } = new(StringComparer.Ordinal);

            protected override Assembly? Load(AssemblyName name)
            {
                if (ResolveSharedAssembly(name) is { } sharedAssembly)
                    return sharedAssembly;

                if (name.Name != null && LocalAssemblyMap.TryGetValue(name.Name, out var local))
                    return local;

                if (name.Name != null && AssemblyMap.TryGetValue(name.Name, out var shared))
                    return shared;

                // 处理卫星资源文件加载（从zip中）
                if (!string.IsNullOrEmpty(name.CultureName) && name.Name != null && name.Name.EndsWith(".resources"))
                {
                    var searchSuffix = $"{name.CultureName}/{name.Name}.dll";
                    foreach (var metadata in Metadatas.Values.Where(m => m.IsFromZip))
                    {
                        var satelliteEntry = metadata.SatelliteEntries.FirstOrDefault(e =>
                            e.Contains(searchSuffix, StringComparison.OrdinalIgnoreCase));

                        if (satelliteEntry != null)
                        {
                            var zipPath = metadata.FilePath.Split('|', 2)[0];
                            using var satelliteStream = CreateZipEntryStream(zipPath, satelliteEntry);
                            if (satelliteStream != null) return LoadFromStream(satelliteStream);
                        }
                    }
                }

                if (name.Name == null || !TryGetAssemblyMetadata(name.Name, out var dep)) return null;

                using var stream = CreateStream(dep);
                var assembly = LoadFromStream(stream);
                if (assembly.GetName().Name is { } assemblyName)
                    LocalAssemblyMap[assemblyName] = assembly;
                return assembly;
            }

            private static MemoryStream? CreateZipEntryStream(string zipPath, string entryName)
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.GetEntry(entryName);
                if (entry == null) return null;

                var ms = new MemoryStream();
                using (var s = entry.Open()) s.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }

            internal static MemoryStream LoadFileStream(string path)
            {
                var ms = new MemoryStream();
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                fs.CopyTo(ms);
                ms.Position = 0;
                return ms;
            }
        }

        internal class PluginMetadata(string path, string name, bool loadInHost, List<string> shared, bool isFromZip)
        {
            public string FilePath { get; } = path;
            public string PluginName { get; } = name;
            public bool LoadInHost { get; } = loadInHost;
            public List<string> SharedContextsWith { get; } = shared;
            public bool IsFromZip { get; } = isFromZip;
            public List<string> SatelliteEntries { get; } = [];
        }
    }
}
