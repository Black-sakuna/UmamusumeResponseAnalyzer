using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal static partial class PluginManager
    {
        // ── 热重载 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 批量应用插件重载，逐项返回成功、失败或需重启。
        /// 新安装的共享上下文成员会先把已加载的同组插件排入重载顺序，避免共享锚点被加载进两个 ALC。
        /// </summary>
        internal static async Task<IReadOnlyList<PluginLifecycleResult>> ReloadPluginsAsync(params string[] pluginNames)
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
                    ReportPluginDiagnostic(ex);
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
                    ReportPluginDiagnostic(ex);
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
                    ReportPluginDiagnostic(
                        $"插件 {item.Raw} 不存在，无法加载。",
                        UiSeverity.Warning);
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
                        ReportPluginDiagnostic(
                            $"插件 {name} 加载失败: 依赖的共享上下文插件 {string.Join("、", missing)} 未安装。",
                            UiSeverity.Error);
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
                    staged.Context.TrackAssembly(assemblyName, assembly);
                    staged.Assemblies.Add(new(assemblyName, assembly));
                }

                phase = "读取插件导出类型";
                var type = assembly.GetExportedTypes().FirstOrDefault(x => typeof(IPlugin).IsAssignableFrom(x));
                if (type is null)
                {
                    ReportPluginDiagnostic(
                        $"插件 {metadata.PluginName} 加载失败: 未找到实现 {nameof(IPlugin)} 的公开类型。",
                        UiSeverity.Error);
                    FailedPlugins.Add(metadata.FilePath);
                    return false;
                }

                phase = "创建插件实例";
                if (Activator.CreateInstance(type) is not IPlugin createdPlugin)
                {
                    ReportPluginDiagnostic(
                        $"插件 {metadata.PluginName} 加载失败: 无法创建插件实例。type={type.FullName ?? type.Name}",
                        UiSeverity.Error);
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
                    catch (Exception cleanupEx) { ReportPluginDiagnostic(cleanupEx); }
                }

                ReportPluginDiagnostic(PluginLoadException(metadata, phase, ex));
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
                    failures.Add(new InvalidOperationException(
                        $"插件清理失败: plugin={pluginName}, phase=Dispose, " +
                        DescribeException(ex)));
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

    }
}
