using Newtonsoft.Json;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal sealed record PluginUpdateInfo(
        string DisplayName,
        Version CurrentVersion,
        Version LatestVersion);

    internal static class PluginRepository
    {
        // URACloud 插件 API——唯一的插件源，不可由用户配置。正式域名经 NGINX 反代：root 是前端、/api 才是后端，
        // 故插件源在 /api/Plugins（生产/测试统一用）。本地起 URACloud 无反代时直连后端 http://localhost:4694/Plugins。
        const string PluginApiBase = "https://ura.shuise.net/api/Plugins";
        static readonly Version ZeroVersion = new(0, 0, 0);

        public static async Task ShowMenuAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ShowMenuCoreAsync(cancellationToken);
            }
            catch (global::UmamusumeResponseAnalyzer.UmamusumeResponseAnalyzer.PostShutdownProcessRequestedException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ModalDialogs.Acknowledge(
                    TerminalUi.Application,
                    $"插件仓库操作失败：{ex.Message}",
                    cancellationToken);
            }
        }

        static async Task ShowMenuCoreAsync(CancellationToken cancellationToken)
        {
            var plugins = await FetchAllPluginsAsync(cancellationToken: cancellationToken);
            if (plugins.Count == 0)
            {
                ModalDialogs.Acknowledge(
                    TerminalUi.Application,
                    "插件仓库没有可用插件",
                    cancellationToken);
                return;
            }

            var pluginChoices = plugins
                .OrderBy(p => string.IsNullOrWhiteSpace(p.Category) || p.Category == UncategorizedLabel ? 1 : 0)
                .ThenBy(p => string.IsNullOrWhiteSpace(p.Category) ? UncategorizedLabel : p.Category)
                .ThenBy(DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedPlugins = TerminalUi.MultiSelect(
                "选择要安装的插件",
                pluginChoices,
                converter: FormatChoice,
                cancellationToken: cancellationToken).ToList();
            if (selectedPlugins.Count == 0)
                return;

            // 同一 InternalName 的多个 fork(不同作者)都能勾选,但本地磁盘/加载器只认 InternalName——
            // 安装会按程序集名互相覆盖、实际只落一个。检出冲突,让用户每个 InternalName 只保留一个来源。
            selectedPlugins = DedupeForks(selectedPlugins, cancellationToken);
            if (selectedPlugins.Count == 0)
                return;

            ResolveDependencies(selectedPlugins, plugins);
            var installed = await InstallPluginsAsync(selectedPlugins, cancellationToken);

            if (installed.Count > 0)
            {
                var needRestart = await PluginManager.ReloadPluginsAsync([.. installed]);
                cancellationToken.ThrowIfCancellationRequested();
                if (needRestart.Count == 0)
                {
                    ModalDialogs.Acknowledge(
                        TerminalUi.Application,
                        $"插件已安装并生效：{string.Join("、", installed)}",
                        cancellationToken);
                }
                else
                {
                    // 无法热重载的情形（如 [LoadInHostContext] 插件）通过重启完成应用。
                    var restart = ModalDialogs.Acknowledge(
                        TerminalUi.Application,
                        $"需重启以应用插件：{string.Join("、", needRestart)}",
                        cancellationToken);
                    if (restart)
                        global::UmamusumeResponseAnalyzer.UmamusumeResponseAnalyzer.Restart();
                }
            }
        }

        public static async Task<IReadOnlyList<PluginUpdateInfo>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var loaded = PluginManager.SnapshotLoadedPlugins();
            if (loaded.Count == 0) return [];
            var remote = await FetchAllPluginsAsync(silent: true, cancellationToken);
            if (remote.Count == 0) return [];

            var remoteByInternalName = remote
                .GroupBy(r => r.InternalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var updates = new List<PluginUpdateInfo>();
            foreach (var plugin in loaded)
            {
                var assemblyName = PluginManager.InternalName(plugin);
                // 同名 fork 用作者消歧：只有一个匹配时直接使用；多个 fork 时
                // 按已加载插件的 Author 选对应那个，选不出就跳过（宁可不提示，也不对错误的 fork 误报更新）。
                if (!remoteByInternalName.TryGetValue(assemblyName, out var matches)) continue;
                var remoteInfo = matches.Count switch
                {
                    1 => matches[0],
                    _ => matches.FirstOrDefault(r => string.Equals(r.Author, plugin.Author, StringComparison.OrdinalIgnoreCase)),
                };
                if (remoteInfo is null) continue;
                if (remoteInfo.Version > plugin.Version)
                {
                    updates.Add(new PluginUpdateInfo(
                        DisplayLabel(remoteInfo),
                        plugin.Version,
                        remoteInfo.Version));
                }
            }

            return updates;
        }

        static async Task<List<PluginInformation>> FetchAllPluginsAsync(bool silent = false, CancellationToken cancellationToken = default)
        {
            if (!silent) TerminalUi.Log("URA", "正在从插件仓库获取插件信息");
            var list = await FetchAsync(PluginApiBase, cancellationToken);
            return BuildCatalog(list, Config.Repository.Targets);
        }

        // 把原始 manifest 列表过滤成目录。**不按 InternalName 去重**：仓库允许同名不同作者的
        // fork（如 离披/StatisticsCollector 与 URACloud-Tester/StatisticsCollector），插件身份
        // 是 (Author, InternalName) 复合键。早先这里用 Dictionary 拿 InternalName 当 key，后到
        // 的 fork 会覆盖先到的、连分类一起被吞掉（症状：目录里只剩一个、其余“消失”）。提成纯函数便于回归测试。
        internal static List<PluginInformation> BuildCatalog(
            IEnumerable<PluginInformation> raw, IReadOnlyCollection<string> targetFilter)
        {
            var noTargetFilter = targetFilter.Count == 0;
            var plugins = new List<PluginInformation>();
            foreach (var plugin in raw)
            {
                if (plugin is null)
                    throw new InvalidDataException("插件仓库返回了 null manifest");
                if (string.IsNullOrWhiteSpace(plugin.Author))
                    throw new InvalidDataException("插件 manifest 缺少 Author");
                if (string.IsNullOrWhiteSpace(plugin.InternalName))
                    throw new InvalidDataException($"插件 {plugin.Author} 的 manifest 缺少 InternalName");
                if (!System.Version.TryParse(plugin.RawVersion, out _))
                    throw new InvalidDataException($"插件 {plugin.Author}/{plugin.InternalName} 的版本无效: {plugin.RawVersion}");
                if (!noTargetFilter && plugin.Targets is { Length: > 0 } && !plugin.Targets.Intersect(targetFilter).Any())
                    continue;

                plugin.DownloadUrl = $"{PluginApiBase}/{Uri.EscapeDataString(plugin.Author)}/{Uri.EscapeDataString(plugin.InternalName)}/versions/{Uri.EscapeDataString(plugin.RawVersion)}/download";
                plugins.Add(plugin);
            }
            return plugins;
        }

        static async Task<List<PluginInformation>> FetchAsync(string url, CancellationToken cancellationToken)
        {
            using var resp = await ResourceUpdater.HttpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var text = await resp.Content.ReadAsStringAsync(cancellationToken);
            return JsonConvert.DeserializeObject<List<PluginInformation>>(text)
                ?? throw new JsonSerializationException($"{url} 返回了 null");
        }

        // 空分类的兜底组名。用 "其他" 而非单独的 "未分类"，好让它与 manifest 里
        // 真实声明 Category="其他" 的插件合并进同一组（与前端 DEFAULT_CATEGORY 一致）。
        const string UncategorizedLabel = "其他";

        static string DisplayLabel(PluginInformation info) =>
            string.IsNullOrWhiteSpace(info.DisplayName) ? info.InternalName : info.DisplayName;

        static string FormatChoice(PluginInformation info)
        {
            var version = info.Version == ZeroVersion ? "" : $" v{info.Version}";
            // @作者:区分同名不同作者的 fork；真实选择值直接绑定 PluginInformation,显示文本不参与身份判断。
            var author = string.IsNullOrWhiteSpace(info.Author) ? "" : $" @{info.Author}";
            var desc = string.IsNullOrEmpty(info.Description) ? "" : $" — {info.Description}";
            return $"{DisplayLabel(info)}{version}{author}{desc}".ReplaceLineEndings(" ");
        }

        internal static void ResolveDependencies(List<PluginInformation> selectedPlugins, List<PluginInformation> catalog)
        {
            var selectedByName = selectedPlugins.ToDictionary(p => p.InternalName, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < selectedPlugins.Count; i++)
            {
                foreach (var dependency in selectedPlugins[i].Dependencies)
                {
                    if (selectedByName.ContainsKey(dependency)) continue;

                    var matches = catalog
                        .Where(p => string.Equals(p.InternalName, dependency, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var dependencyPluginInfo = matches.Count switch
                    {
                        1 => matches[0],
                        0 => throw new InvalidOperationException($"插件 {selectedPlugins[i].InternalName} 的依赖 {dependency} 不存在"),
                        _ => throw new InvalidOperationException($"插件 {selectedPlugins[i].InternalName} 的依赖 {dependency} 有多个 fork，请先手动选择其中一个"),
                    };
                    selectedPlugins.Add(dependencyPluginInfo);
                    selectedByName[dependencyPluginInfo.InternalName] = dependencyPluginInfo;
                }
            }
        }

        /// <summary>
        /// 同一 InternalName 被勾选了多个 fork(不同作者)时,逐个让用户二选一——本地磁盘/加载器只按
        /// 程序集名(==InternalName)落地,装多个会互相覆盖,UI 的多选无法兑现。每个 InternalName 只留一个。
        /// </summary>
        static List<PluginInformation> DedupeForks(
            List<PluginInformation> selected,
            CancellationToken cancellationToken)
        {
            var result = new List<PluginInformation>();
            foreach (var group in selected.GroupBy(p => p.InternalName, StringComparer.OrdinalIgnoreCase))
            {
                var forks = group.ToList();
                if (forks.Count == 1)
                {
                    result.Add(forks[0]);
                    continue;
                }
                TerminalUi.Log("URA", $"插件 {group.Key} 选中了多个来源，本地只能安装一个，请选择保留哪个：");
                var pick = TerminalUi.Select(
                    $"为 {group.Key} 选择来源",
                    forks,
                    f => $"{DisplayLabel(f)} @{f.Author}",
                    cancellationToken);
                result.Add(pick);
            }
            return result;
        }

        static async Task<PluginInformation?> PromptVersionAsync(PluginInformation plugin, CancellationToken cancellationToken)
        {
            var versions = await FetchAsync(
                $"{PluginApiBase}/{Uri.EscapeDataString(plugin.Author)}/{Uri.EscapeDataString(plugin.InternalName)}/versions",
                cancellationToken);
            // 版本 manifest 不带 DownloadUrl——用原始 plugin 的 Author/InternalName 拼，不信任 response 字段
            foreach (var v in versions)
                v.DownloadUrl = $"{PluginApiBase}/{Uri.EscapeDataString(plugin.Author)}/{Uri.EscapeDataString(plugin.InternalName)}/versions/{Uri.EscapeDataString(v.RawVersion)}/download";
            versions = versions
                .Where(v => v.Version > ZeroVersion)
                .OrderByDescending(v => v.Version)
                .ToList();
            if (versions.Count == 0)
            {
                throw new InvalidDataException($"插件 {plugin.Author}/{plugin.InternalName} 没有可安装版本");
            }
            if (versions.Count == 1)
            {
                return versions[0];
            }

            const string CancelLabel = "取消该插件";
            var labelToVersion = new Dictionary<string, PluginInformation>(StringComparer.Ordinal);
            for (var i = 0; i < versions.Count; i++)
            {
                var v = versions[i];
                var tag = i == 0 ? " (最新)" : string.Empty;
                var changelog = string.IsNullOrEmpty(v.Changelog) ? string.Empty : $" — {v.Changelog}";
                labelToVersion[$"{v.Version}{tag}{changelog}"] = v;
            }

            var selection = TerminalUi.Select(
                $"选择 {DisplayLabel(plugin)} 要安装的版本",
                labelToVersion.Keys.Append(CancelLabel),
                cancellationToken: cancellationToken);
            if (selection == CancelLabel) return null;
            return labelToVersion[selection];
        }

        // 安装落地路径:Plugins/{InternalName}.zip。SDK 打的 zip 内容在根、无 Plugins/ 前缀,宿主 ScanAll 扫的就是 Plugins/*.zip。
        // (历史回归:URACloud 迁移期曾 ExtractToDirectory("./") 解到 WORKING_DIRECTORY 根 → ScanAll 扫不到 → 装了等于没装。)
        internal static string InstallZipPath(string internalName) => Path.Combine("Plugins", $"{internalName}.zip");

        internal static string Bracketed(string label) => $"[{label}]";

        internal static async Task<List<string>> InstallPluginsAsync(List<PluginInformation> selectedPlugins, CancellationToken cancellationToken = default)
        {
            var installed = new List<string>();
            var conflictingNames = selectedPlugins
                .GroupBy(p => p.InternalName, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Select(p => p.Author).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in selectedPlugins)
            {
                if (conflictingNames.Contains(plugin.InternalName))
                {
                    TerminalUi.Log("URA", $"{Bracketed(plugin.InternalName)} 被多个作者同时选中，跳过；请一次只安装其中一个 fork");
                    continue;
                }

                var installedFork = PluginManager.SnapshotLoadedPlugins()
                    .FirstOrDefault(p => string.Equals(PluginManager.InternalName(p), plugin.InternalName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(p.Author, plugin.Author, StringComparison.OrdinalIgnoreCase));
                if (installedFork != null)
                {
                    TerminalUi.Log("URA", $"{Bracketed(DisplayLabel(plugin))} 与已安装的 {installedFork.Author}/{plugin.InternalName} 冲突，跳过");
                    continue;
                }

                try
                {
                    var versionToInstall = await PromptVersionAsync(plugin, cancellationToken);
                    if (versionToInstall is null)
                    {
                        TerminalUi.Log("URA", $"{Bracketed(DisplayLabel(plugin))} 跳过");
                        continue;
                    }

                    TerminalUi.Log("URA", $"[{DisplayLabel(plugin)} v{versionToInstall.Version}] 正在下载");
                    // 直接把下载的 zip 落成 Plugins/{InternalName}.zip,不解压(SDK 打的 zip 内容在根、无 Plugins/ 前缀;
                    // 宿主 ScanAll 扫的就是 Plugins/*.zip,与本地开发 deploy 一致,热重载重扫即可发现)。
                    await DownloadPluginZipAsync(versionToInstall.DownloadUrl, plugin.InternalName, cancellationToken);
                    TerminalUi.Log("URA", $"[{DisplayLabel(plugin)}] 安装完成");
                    installed.Add(plugin.InternalName);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    TerminalUi.Log("URA", $"{Bracketed(DisplayLabel(plugin))} 安装失败: {ex.Message}");
                }
            }
            return installed;
        }

        internal static async Task DownloadPluginZipAsync(string url, string internalName, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory("Plugins");
            var tempDir = Path.Combine(Path.GetTempPath(), "UmamusumeResponseAnalyzer");
            Directory.CreateDirectory(tempDir);
            foreach (var stale in Directory.GetFiles(tempDir, "plugin-*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(stale);
            }

            using var response = await ResourceUpdater.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var dest = InstallZipPath(internalName);
            var tempPath = Path.Combine(tempDir, $"plugin-{internalName}-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                    await fs.FlushAsync(cancellationToken);
                }
                File.Move(tempPath, dest, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        /// <summary>
        /// 非交互安装:按 (author, internalName, version) 从【硬编码的】URACloud 仓库下载并落盘
        /// Plugins/{internalName}.zip。供 :4693 的 Web 端点(<see cref="WebInstallApi"/>)调用——刻意
        /// 只收三段引用、<b>绝不接受任何 URL</b>,下载源恒为 <see cref="PluginApiBase"/>。因此伪造的网页
        /// 既改不了下载源也投不了毒,顶多触发安装一个仓库里真实存在的插件。调用方负责随后调
        /// <see cref="PluginManager.ReloadPluginsAsync"/> 完成热重载。
        /// </summary>
        public static async Task InstallByReferenceAsync(string author, string internalName, string version, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(author) || !IsSafeSegment(internalName) || string.IsNullOrWhiteSpace(version))
                throw new ArgumentException("非法的插件标识");
            var url = $"{PluginApiBase}/{Uri.EscapeDataString(author)}/{Uri.EscapeDataString(internalName)}/versions/{Uri.EscapeDataString(version)}/download";
            await DownloadPluginZipAsync(url, internalName, cancellationToken);
        }

        static bool IsSafeSegment(string s) =>
            !string.IsNullOrEmpty(s) && s.Length <= 100 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }
}
