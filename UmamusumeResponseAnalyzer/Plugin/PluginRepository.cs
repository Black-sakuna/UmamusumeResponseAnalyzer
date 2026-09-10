using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin;

internal sealed record PluginUpdateInfo(string DisplayName, Version CurrentVersion, Version LatestVersion, string Reason);

internal static class PluginRepository
{
    internal const string PluginApiBase = "https://ura.shuise.net/api/Plugins";
    const long MaxPackageBytes = 64L * 1024 * 1024;
    static readonly System.Resources.ResourceManager Resources = new(
        "UmamusumeResponseAnalyzer.Localization.PluginRegistry", typeof(PluginRepository).Assembly);
    static int installing;
    internal static Func<PluginRelease, CancellationToken, bool> ConfirmInstall = (release, ct) =>
        TerminalUi.Confirm(BuildInstallConfirmation(release), cancellationToken: ct);

    static string Text(string name) => Resources.GetString(name,
        System.Globalization.CultureInfo.GetCultureInfo(LanguageConfig.GetCulture()))!;

    public static async Task ShowMenuAsync(CancellationToken cancellationToken)
    {
        try
        {
            var channel = TerminalUi.Select(Text("Channel"), new[] { Text("Stable"), Text("IncludePrerelease") },
                cancellationToken: cancellationToken);
            var includePrerelease = channel == Text("IncludePrerelease");
            var plugins = BuildCatalog(await FetchAsync<List<PluginRelease>>(
                $"{PluginApiBase}?includePrerelease={includePrerelease}", cancellationToken), Config.Repository.Targets);
            if (plugins.Count == 0) { TerminalUi.Acknowledge(Text("Empty"), cancellationToken); return; }
            var selected = TerminalUi.MultiSelect(Text("SelectPlugins"), plugins.OrderBy(r => r.Manifest.Category).ToArray(),
                converter: FormatChoice, cancellationToken: cancellationToken).ToList();
            await InstallPluginsAsync(selected, includePrerelease, cancellationToken);
        }
        catch (global::UmamusumeResponseAnalyzer.UmamusumeResponseAnalyzer.PostShutdownProcessRequestedException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { TerminalUi.Acknowledge($"{Text("Failed")}: {ex.Message}", cancellationToken); }
    }

    internal static List<PluginRelease> BuildCatalog(IEnumerable<PluginRelease> raw, IReadOnlyCollection<string> targets)
    {
        var result = new List<PluginRelease>();
        var repositories = new HashSet<long>();
        foreach (var release in raw)
        {
            if (release is null) throw new InvalidDataException("插件目录包含 null。");
            release.Validate();
            if (!repositories.Add(release.Source.RepositoryId))
                throw new InvalidDataException($"仓库 ID 重复: {release.Source.RepositoryId}");
            if (targets.Count == 0 || release.Manifest.Targets.Length == 0 || release.Manifest.Targets.Intersect(targets).Any())
                result.Add(release);
        }
        return result;
    }

    static string FormatChoice(PluginRelease release) =>
        $"{release.Manifest.DisplayName} v{release.Manifest.RawVersion} [{release.Source.FullName}] " +
        $"({Text(release.Prerelease ? "Prerelease" : "Stable")}; {Text(release.Source.Verified ? "Verified" : "Unverified")})";

    internal static async Task InstallPluginsAsync(List<PluginRelease> selected, bool includePrerelease = false, CancellationToken cancellationToken = default)
    {
        if (selected.Select(r => r.Manifest.InternalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
            throw new InvalidOperationException("已选插件 InternalName 重复。 / Selected plugins share an InternalName.");
        foreach (var plugin in selected)
        {
            try
            {
                var releases = await FetchAsync<List<PluginRelease>>(
                    $"{PluginApiBase}/{plugin.Source.RepositoryId}/releases?includePrerelease={includePrerelease}", cancellationToken);
                foreach (var release in releases)
                {
                    release.Validate();
                    if (release.Source.RepositoryId != plugin.Source.RepositoryId)
                        throw new InvalidDataException("Release 来源与所选仓库不匹配。");
                }
                if (releases.Count == 0) throw new InvalidDataException(Text("Empty"));
                var choices = releases.Select(r => $"{FormatChoice(r)} · {r.Tag} · #{r.ReleaseId}").Append(Text("Cancel")).ToArray();
                var choice = TerminalUi.Select(Text("SelectRelease"), choices, cancellationToken: cancellationToken);
                if (choice == Text("Cancel")) continue;
                var selectedRelease = releases[Array.IndexOf(choices, choice)];
                var result = await InstallByReferenceAsync(plugin.Source.RepositoryId, selectedRelease.ReleaseId, cancellationToken);
                TerminalUi.Acknowledge(result.Ok
                    ? $"{plugin.Manifest.DisplayName}: {Text(result.Loaded ? "InstalledLoaded" : "InstalledNotLoaded")} {result.Error}"
                    : result.Error!, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { TerminalUi.Acknowledge($"{plugin.Manifest.DisplayName}: {ex.Message}", cancellationToken); }
        }
    }

    internal static List<InstalledPlugin> ReadInstalledPlugins()
    {
        if (!Directory.Exists("Plugins")) return [];
        var result = new List<InstalledPlugin>();
        foreach (var path in Directory.GetFiles("Plugins", "*.zip"))
        {
            PluginInformation? manifest = null;
            try
            {
                manifest = PluginPackageValidator.Validate(path, requireMatchingPackageFileName: true).Manifest;
                var source = PluginInstallStorage.ReadSource(path);
                result.Add(new(path, manifest, source, source is null ? Text("UnknownSource") : null));
            }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or UnauthorizedAccessException or BadImageFormatException)
            {
                result.Add(new(path, manifest, null, ex.Message));
            }
        }
        return result;
    }

    public static async Task<IReadOnlyList<PluginUpdateInfo>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var installed = ReadInstalledPlugins();
        if (installed.Count == 0) return [];
        var remote = BuildCatalog(await FetchAsync<List<PluginRelease>>(PluginApiBase, cancellationToken), []);
        var updates = new List<PluginUpdateInfo>();
        foreach (var plugin in installed)
        {
            if (plugin.Source is null || plugin.Manifest is null)
            {
                TerminalUi.Log("URA", $"{Path.GetFileName(plugin.Path)}: {Text("UnknownSource")} {plugin.Error}", UiSeverity.Warning);
                continue;
            }
            var latest = remote.SingleOrDefault(r => r.Source.RepositoryId == plugin.Source.RepositoryId);
            if (latest is null) continue;
            var reason = UpdateReason(plugin.Manifest.Version, plugin.Source, latest);
            if (reason is not null) updates.Add(new(latest.Manifest.DisplayName, plugin.Manifest.Version, latest.Manifest.Version, Text(reason)));
        }
        return updates;
    }

    internal static string? UpdateReason(Version version, InstalledPluginSource installed, PluginRelease latest)
    {
        if (latest.Source.RepositoryId != installed.RepositoryId || latest.Prerelease || latest.Manifest.Version < version) return null;
        if (latest.Manifest.Version > version) return "NewVersion";
        if (installed.Prerelease) return "Promoted";
        if (latest.ReleaseId != installed.ReleaseId || latest.AssetId != installed.AssetId ||
            !latest.Sha256.Equals(installed.Sha256, StringComparison.OrdinalIgnoreCase)) return "PackageChanged";
        return null;
    }

    internal static string BuildInstallConfirmation(PluginRelease release)
    {
        var message = $"{Text("Confirm")} {release.Manifest.DisplayName} v{release.Manifest.RawVersion}\n" +
            $"{Text("Source")}: {release.Source.FullName}\n" +
            $"{release.Tag} · {Text(release.Prerelease ? "Prerelease" : "Stable")}\n";
        if (!release.Source.Verified) message += Text("UnverifiedWarning") + "\n";
        var installed = ReadInstalledPlugins();
        var existing = installed.SingleOrDefault(p => string.Equals(
            p.Manifest?.InternalName ?? Path.GetFileNameWithoutExtension(p.Path), release.Manifest.InternalName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Source?.RepositoryId != release.Source.RepositoryId)
        {
            var oldSource = existing.Source is null ? Text("UnknownSource") : $"repository #{existing.Source.RepositoryId}";
            message += string.Format(Text("SwitchSource"), oldSource, release.Source.FullName, release.Manifest.InternalName) + "\n";
            var dependents = installed.Where(p => p.Manifest?.Dependencies.Contains(release.Manifest.InternalName, StringComparer.OrdinalIgnoreCase) == true)
                .Select(p => p.Manifest!.DisplayName).ToArray();
            if (dependents.Length > 0) message += $"{Text("Dependents")}: {string.Join(", ", dependents)}";
        }
        return message;
    }

    internal static string InstallZipPath(string internalName) => Path.Combine("Plugins", $"{internalName}.zip");

    public static async Task<PluginInstallResult> InstallByReferenceAsync(long repositoryId, long releaseId, CancellationToken cancellationToken = default)
    {
        if (repositoryId <= 0 || releaseId <= 0) throw new ArgumentException("非法的插件来源 ID。 / Invalid plugin source ID.");
        if (Interlocked.CompareExchange(ref installing, 1, 0) != 0) return new(false, false, Error: Text("Busy"));
        try
        {
            var descriptorUrl = $"{PluginApiBase}/{repositoryId}/releases/{releaseId}";
            var descriptor = await FetchAsync<PluginRelease>(descriptorUrl, cancellationToken);
            descriptor.Validate();
            if (descriptor.Source.RepositoryId != repositoryId || descriptor.ReleaseId != releaseId)
                throw new InvalidDataException("安装描述来源与请求不匹配。");
            if (!ConfirmInstall(descriptor, cancellationToken)) return new(false, false, Error: Text("Cancelled"));
            cancellationToken.ThrowIfCancellationRequested();
            var current = await FetchAsync<PluginRelease>(descriptorUrl, cancellationToken);
            current.Validate();
            if (!SameDescriptor(descriptor, current))
                throw new InvalidDataException(Text("DescriptorChanged"));
            await DownloadAndCommitAsync(descriptor, descriptorUrl + "/download", cancellationToken);
            // Once committed, cancellation/loading failure cannot turn a successful installation into "not installed".
            try
            {
                var result = (await PluginManager.ReloadPluginsAsync(descriptor.Manifest.InternalName)).Single();
                var loaded = result.Outcome == PluginManager.PluginLifecycleOutcome.Succeeded;
                return new(true, loaded, descriptor.Manifest.InternalName, loaded ? null : Text("InstalledNotLoaded"));
            }
            catch (Exception ex) { return new(true, false, descriptor.Manifest.InternalName, ex.Message); }
        }
        finally { Volatile.Write(ref installing, 0); }
    }

    static bool SameDescriptor(PluginRelease a, PluginRelease b) =>
        a.Source == b.Source && a.InstalledSource == b.InstalledSource &&
        a.Tag == b.Tag && a.ReleaseUrl == b.ReleaseUrl && a.PublishedAt == b.PublishedAt &&
        JToken.DeepEquals(JToken.FromObject(a.Manifest), JToken.FromObject(b.Manifest));

    static async Task DownloadAndCommitAsync(PluginRelease descriptor, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{descriptor.AssetId}-{descriptor.Sha256}\""));
        using var response = await ResourceUpdater.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Headers.ETag?.Tag != $"\"{descriptor.AssetId}-{descriptor.Sha256}\"")
            throw new InvalidDataException(Text("DescriptorChanged"));
        if (response.Content.Headers.ContentLength > MaxPackageBytes) throw new InvalidDataException("插件 ZIP 超过 64 MiB。");
        Directory.CreateDirectory("Plugins");
        var temp = Path.Combine("Plugins", $"plugin-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                var total = 0L;
                for (var count = await input.ReadAsync(buffer, cancellationToken); count != 0; count = await input.ReadAsync(buffer, cancellationToken))
                {
                    total += count;
                    if (total > MaxPackageBytes) throw new InvalidDataException("插件 ZIP 超过 64 MiB。");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            using (var stream = File.OpenRead(temp))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载 ZIP 的 SHA-256 与确认时不符。 / Downloaded ZIP hash differs from confirmation.");
            var manifest = ValidatePackage(temp, descriptor.Manifest.Author, descriptor.Manifest.InternalName, descriptor.Manifest.RawVersion);
            if (!JToken.DeepEquals(JToken.FromObject(manifest), JToken.FromObject(descriptor.Manifest)))
                throw new InvalidDataException("下载 ZIP manifest 与确认时不符。 / Downloaded manifest differs from confirmation.");
            var current = await FetchAsync<PluginRelease>(
                $"{PluginApiBase}/{descriptor.Source.RepositoryId}/releases/{descriptor.ReleaseId}", cancellationToken);
            current.Validate();
            if (!SameDescriptor(descriptor, current)) throw new InvalidDataException(Text("DescriptorChanged"));
            cancellationToken.ThrowIfCancellationRequested();
            var existing = Directory.GetFiles("Plugins", "*.zip").SingleOrDefault(p =>
                Path.GetFileNameWithoutExtension(p).Equals(manifest.InternalName, StringComparison.OrdinalIgnoreCase));
            PluginInstallStorage.Commit(temp, existing ?? InstallZipPath(manifest.InternalName), descriptor.InstalledSource);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static async Task<T> FetchAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await ResourceUpdater.HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonConvert.DeserializeObject<T>(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new InvalidDataException("URACloud 返回了空 JSON。");
    }

    internal static PluginInformation ValidatePackage(string path, string expectedAuthor, string expectedInternalName, string expectedVersion)
    {
        var manifest = PluginPackageValidator.Validate(path, requireMatchingPackageFileName: false).Manifest;
        if (!manifest.Author.Equals(expectedAuthor, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"下载包 Author 与请求不匹配: expected={expectedAuthor}, actual={manifest.Author}");
        if (!manifest.InternalName.Equals(expectedInternalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"下载包 InternalName 与请求不匹配: expected={expectedInternalName}, actual={manifest.InternalName}");
        if (!Version.TryParse(expectedVersion, out var version) || manifest.Version != version)
            throw new InvalidDataException($"下载包 Version 与请求不匹配: expected={expectedVersion}, actual={manifest.RawVersion}");
        return manifest;
    }
}
