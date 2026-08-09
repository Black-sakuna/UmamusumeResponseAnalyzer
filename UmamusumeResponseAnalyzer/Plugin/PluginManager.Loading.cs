using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal static partial class PluginManager
    {
        internal static void LoadMetadatas()
        {
            Dictionary<string, PluginMetadata> assemblies = [];
            ScanAll(Metadatas, assemblies);
            ReplaceAssemblyMetadatas(assemblies);
        }

        static (Dictionary<string, PluginMetadata> Plugins, Dictionary<string, PluginMetadata> Assemblies) ScanPluginMetadata(
            bool reportFailures = true)
        {
            Dictionary<string, PluginMetadata> scanned = [];
            Dictionary<string, PluginMetadata> assemblies = [];
            ScanAll(scanned, assemblies, reportFailures);
            return (scanned, assemblies);
        }

        /// <summary>扫描 Plugins/ 下所有 dll 与 zip，把插件元数据（含卫星资源关联）写入 <paramref name="target"/>。</summary>
        static void ScanAll(
            Dictionary<string, PluginMetadata> target,
            Dictionary<string, PluginMetadata> assemblyTarget,
            bool reportFailures = true)
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
                    if (reportFailures)
                    {
                        ReportPluginDiagnostic(ex);
                        if (!FailedPlugins.Contains(dll.FullName))
                            FailedPlugins.Add(dll.FullName);
                    }
                }
            }

            foreach (var zip in pluginsDir.GetFiles("*.zip", SearchOption.TopDirectoryOnly).Select(x => x.FullName))
                LoadZipMetadatas(zip, culture, target, assemblyTarget, reportFailures);
        }

        /// <summary>读取单个 zip 内的主插件元数据并关联其卫星资源，写入 <paramref name="target"/>。供初始加载与热重载复用。</summary>
        static void LoadZipMetadatas(
            string zip,
            string culture,
            Dictionary<string, PluginMetadata> target,
            Dictionary<string, PluginMetadata> assemblyTarget,
            bool reportFailures)
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
                            if (reportFailures)
                            {
                                ReportPluginDiagnostic(ex);
                                var pluginPath = $"{zip}|{entry.FullName}";
                                if (!FailedPlugins.Contains(pluginPath))
                                    FailedPlugins.Add(pluginPath);
                            }
                        }
                    }
                    else if (entry.FullName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) &&
                             entry.FullName.Contains(culture, StringComparison.OrdinalIgnoreCase))
                    {
                        // 卫星资源文件（子目录下），延迟处理以确保主dll元数据已加载
                        satelliteEntries.Add(entry);
                    }
                }

                if (!hasMainPlugin && reportFailures)
                    ReportPluginDiagnostic(
                        $"插件包 {Path.GetFileName(zip)} 未找到主插件 DLL {pluginName}.dll，已跳过。",
                        UiSeverity.Warning);

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
                if (reportFailures)
                {
                    ReportPluginDiagnostic(ex);
                    if (!FailedPlugins.Contains(zip))
                        FailedPlugins.Add(zip);
                }
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
                        ReportPluginDiagnostic(
                            $"插件 {name} 加载失败: 依赖的共享上下文插件 {string.Join("、", missing)} 未安装。",
                            UiSeverity.Error);
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
                    ReportPluginDiagnostic(
                        $"插件 {m.PluginName} 加载失败: 未找到实现 {nameof(IPlugin)} 的公开类型。",
                        UiSeverity.Error);
                    FailedPlugins.Add(m.FilePath);
                    return false;
                }

                phase = "创建插件实例";
                if (Activator.CreateInstance(type) is not IPlugin createdPlugin)
                {
                    ReportPluginDiagnostic(
                        $"插件 {m.PluginName} 加载失败: 无法创建插件实例。type={type.FullName ?? type.Name}",
                        UiSeverity.Error);
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
                    catch (Exception cleanupEx) { ReportPluginDiagnostic(cleanupEx); }
                }

                if (assemblyName is not null)
                    AssemblyMap.Remove(assemblyName);
                if (assembly is not null)
                    Assemblies.Remove(assembly);

                ReportPluginDiagnostic(PluginLoadException(m, phase, ex));
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
                catch (Exception ex) { ReportPluginDiagnostic(ex); }
            }

            foreach (var name in group)
            {
                if (!AssemblyMap.TryGetValue(name, out var asm))
                    continue;

                Assemblies.Remove(asm);
                AssemblyMap.Remove(name);
            }

            try { ctx.Unload(); }
            catch (Exception ex) { ReportPluginDiagnostic(ex); }
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
                    ReportPluginDiagnostic(
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

        internal class PluginLoadContext(string name) : AssemblyLoadContext(name, true)
        {
            readonly object localAssemblyGate = new();
            readonly Dictionary<string, Assembly> localAssemblies = new(StringComparer.Ordinal);

            internal void TrackAssembly(string name, Assembly assembly)
            {
                lock (localAssemblyGate)
                    localAssemblies[name] = assembly;
            }

            protected override Assembly? Load(AssemblyName name)
            {
                if (ResolveSharedAssembly(name) is { } sharedAssembly)
                    return sharedAssembly;

                if (name.Name is not { } assemblyName)
                    return null;

                lock (localAssemblyGate)
                {
                    if (localAssemblies.TryGetValue(assemblyName, out var local))
                        return local;

                    Assembly? mappedAssembly;
                    PluginMetadata? dependency;
                    PluginMetadata[] zipMetadatas;
                    EnterStateRead();
                    try
                    {
                        AssemblyMap.TryGetValue(assemblyName, out mappedAssembly);
                        dependency = TryGetAssemblyMetadata(assemblyName, out var metadata) ? metadata : null;
                        zipMetadatas = !string.IsNullOrEmpty(name.CultureName) && assemblyName.EndsWith(".resources")
                            ? [.. Metadatas.Values.Where(metadata => metadata.IsFromZip)]
                            : [];
                    }
                    finally { ExitStateRead(); }

                    if (mappedAssembly is not null)
                        return mappedAssembly;

                    if (zipMetadatas.Length != 0)
                    {
                        var searchSuffix = $"{name.CultureName}/{assemblyName}.dll";
                        foreach (var metadata in zipMetadatas)
                        {
                            var satelliteEntry = metadata.SatelliteEntries.FirstOrDefault(entry =>
                                entry.Contains(searchSuffix, StringComparison.OrdinalIgnoreCase));
                            if (satelliteEntry is null)
                                continue;

                            var zipPath = metadata.FilePath.Split('|', 2)[0];
                            using var satelliteStream = CreateZipEntryStream(zipPath, satelliteEntry);
                            if (satelliteStream is not null)
                                return LoadFromStream(satelliteStream);
                        }
                    }

                    if (dependency is null)
                        return null;

                    using var stream = CreateStream(dependency);
                    var assembly = LoadFromStream(stream);
                    if (assembly.GetName().Name is { } loadedName)
                        localAssemblies[loadedName] = assembly;
                    return assembly;
                }
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
