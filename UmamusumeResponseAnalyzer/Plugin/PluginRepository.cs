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
                TerminalUi.Acknowledge(
                    $"插件仓库操作失败：{ex.Message}",
                    cancellationToken);
            }
        }

        static async Task ShowMenuCoreAsync(CancellationToken cancellationToken)
        {
            var plugins = await FetchAllPluginsAsync(cancellationToken: cancellationToken);
            if (plugins.Count == 0)
            {
                TerminalUi.Acknowledge(
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

            var installed = await InstallPluginsAsync(selectedPlugins, cancellationToken);

            if (installed.Count > 0)
            {
                var results = await PluginManager.ReloadPluginsAsync([.. installed]);
                cancellationToken.ThrowIfCancellationRequested();
                var failed = results
                    .Where(result => result.Outcome == PluginManager.PluginLifecycleOutcome.Failed)
                    .Select(result => result.PluginName)
                    .ToList();
                if (failed.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"插件安装完成，但加载失败：{string.Join("、", failed)}");
                }

                TerminalUi.Acknowledge(
                    $"插件已安装并生效：{string.Join("、", installed)}",
                    cancellationToken);
            }
        }

        public static async Task<IReadOnlyList<PluginUpdateInfo>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var loaded = PluginManager.SnapshotPluginStatuses()
                .Where(plugin => plugin.IsLoaded)
                .ToArray();
            if (loaded.Length == 0) return [];
            var remote = await FetchAllPluginsAsync(silent: true, cancellationToken);
            if (remote.Count == 0) return [];

            var remoteByInternalName = remote
                .ToDictionary(plugin => plugin.InternalName, StringComparer.OrdinalIgnoreCase);
            var updates = new List<PluginUpdateInfo>();
            foreach (var plugin in loaded)
            {
                var assemblyName = plugin.InternalName;
                var currentVersion = plugin.Version
                    ?? throw new InvalidOperationException($"已加载插件缺少版本: {assemblyName}");
                if (!remoteByInternalName.TryGetValue(assemblyName, out var remoteInfo)) continue;
                if (remoteInfo.Version > currentVersion)
                {
                    updates.Add(new PluginUpdateInfo(
                        DisplayLabel(remoteInfo),
                        currentVersion,
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

        internal static List<PluginInformation> BuildCatalog(
            IEnumerable<PluginInformation> raw, IReadOnlyCollection<string> targetFilter)
        {
            var noTargetFilter = targetFilter.Count == 0;
            var plugins = new List<PluginInformation>();
            var internalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in raw)
            {
                if (plugin is null)
                    throw new InvalidDataException("插件仓库返回了 null manifest");
                if (string.IsNullOrWhiteSpace(plugin.Author))
                    throw new InvalidDataException("插件 manifest 缺少 Author");
                if (string.IsNullOrWhiteSpace(plugin.InternalName))
                    throw new InvalidDataException($"插件 {plugin.Author} 的 manifest 缺少 InternalName");
                if (!internalNames.Add(plugin.InternalName))
                    throw new InvalidDataException($"插件 InternalName 重复: {plugin.InternalName}");
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
            var author = string.IsNullOrWhiteSpace(info.Author) ? "" : $" @{info.Author}";
            var desc = string.IsNullOrEmpty(info.Description) ? "" : $" — {info.Description}";
            return $"{DisplayLabel(info)}{version}{author}{desc}".ReplaceLineEndings(" ");
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
            var internalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in selectedPlugins)
                if (!internalNames.Add(plugin.InternalName))
                    throw new InvalidOperationException($"已选插件 InternalName 重复: {plugin.InternalName}");

            foreach (var plugin in selectedPlugins)
            {
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
                    var manifest = await DownloadPluginZipAsync(
                        versionToInstall.DownloadUrl,
                        plugin.Author,
                        plugin.InternalName,
                        versionToInstall.RawVersion,
                        cancellationToken);
                    TerminalUi.Log("URA", $"[{DisplayLabel(manifest)}] 安装完成");
                    installed.Add(manifest.InternalName);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    var message = $"{Bracketed(DisplayLabel(plugin))} 安装失败: {ex.Message}";
                    TerminalUi.Log("URA", message, UiSeverity.Error);
                    TerminalUi.Notify("URA", message, UiSeverity.Error);
                }
            }
            return installed;
        }

        internal static async Task<PluginInformation> DownloadPluginZipAsync(
            string url,
            string expectedAuthor,
            string expectedInternalName,
            string expectedVersion,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory("Plugins");
            foreach (var stale in Directory.GetFiles("Plugins", "plugin-*.tmp"))
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1))
                    File.Delete(stale);
            }

            using var response = await ResourceUpdater.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var tempPath = Path.Combine("Plugins", $"plugin-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                    await fs.FlushAsync(cancellationToken);
                }
                var manifest = ValidatePackage(
                    tempPath,
                    expectedAuthor,
                    expectedInternalName,
                    expectedVersion);
                File.Move(tempPath, InstallZipPath(manifest.InternalName), overwrite: true);
                return manifest;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        /// <summary>
        /// 非交互安装:按 (author, internalName, version) 从【硬编码的】URACloud 仓库下载，严格校验后落盘
        /// Plugins/{internalName}.zip，并返回已校验的 manifest。供 :4693 的 Web 端点(<see cref="WebInstallApi"/>)调用——刻意
        /// 只收三段引用、<b>绝不接受任何 URL</b>,下载源恒为 <see cref="PluginApiBase"/>。因此伪造的网页
        /// 既改不了下载源也投不了毒,顶多触发安装一个仓库里真实存在的插件。调用方负责随后调
        /// <see cref="PluginManager.ReloadPluginsAsync"/> 完成热重载。
        /// </summary>
        public static Task<PluginInformation> InstallByReferenceAsync(string author, string internalName, string version, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(author) || !IsSafeSegment(internalName) || string.IsNullOrWhiteSpace(version))
                throw new ArgumentException("非法的插件标识");
            var url = $"{PluginApiBase}/{Uri.EscapeDataString(author)}/{Uri.EscapeDataString(internalName)}/versions/{Uri.EscapeDataString(version)}/download";
            return DownloadPluginZipAsync(url, author, internalName, version, cancellationToken);
        }

        internal static PluginInformation ValidatePackage(
            string packagePath,
            string expectedAuthor,
            string expectedInternalName,
            string expectedVersion)
        {
            var manifest = PluginPackageValidator.Validate(
                packagePath,
                requireMatchingPackageFileName: false).Manifest;

            if (!string.Equals(manifest.Author, expectedAuthor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"下载包 Author 与请求不匹配: expected={expectedAuthor}, actual={manifest.Author}");
            if (!string.Equals(manifest.InternalName, expectedInternalName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"下载包 InternalName 与请求不匹配: expected={expectedInternalName}, actual={manifest.InternalName}");
            if (!Version.TryParse(expectedVersion, out var version) || manifest.Version != version)
                throw new InvalidDataException(
                    $"下载包 Version 与请求不匹配: expected={expectedVersion}, actual={manifest.RawVersion}");

            return manifest;
        }

        static bool IsSafeSegment(string s) =>
            !string.IsNullOrEmpty(s) && s.Length <= 100 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }
}