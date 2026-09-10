using Newtonsoft.Json;
using System.Security.Cryptography;

namespace UmamusumeResponseAnalyzer.Plugin;

internal sealed record PluginSource(long RepositoryId, string FullName, long OwnerId, string OwnerLogin,
    string OwnerType, string Url, bool Verified, string? VerificationMethod, long? VerifiedAt);

internal sealed record PluginRelease(PluginSource Source, long ReleaseId, string Tag, string ReleaseUrl,
    bool Prerelease, long PublishedAt, long AssetId, string Sha256, PluginInformation Manifest)
{
    internal InstalledPluginSource InstalledSource => new(Source.RepositoryId, ReleaseId, AssetId, Sha256, Prerelease);

    internal void Validate()
    {
        if (Source is null || Source.RepositoryId <= 0 || Source.OwnerId <= 0 || string.IsNullOrWhiteSpace(Source.FullName) ||
            ReleaseId <= 0 || AssetId <= 0 || Sha256 is null || Sha256.Length != 64 || !Sha256.All(char.IsAsciiHexDigit) || Manifest is null)
            throw new InvalidDataException("插件安装描述缺少有效来源或包身份。 / Invalid plugin source or package identity.");
        PluginPackageValidator.ValidateManifest(Manifest);
    }
}

internal sealed record InstalledPluginSource(long RepositoryId, long ReleaseId, long AssetId, string Sha256, bool Prerelease);
internal sealed record InstalledPlugin(string Path, PluginInformation? Manifest, InstalledPluginSource? Source, string? Error);
internal sealed record PluginInstallResult(bool Ok, bool Loaded, string? Installed = null, string? Error = null);

internal static class PluginInstallStorage
{
    internal static string SourcePath(string zipPath) => Path.ChangeExtension(zipPath, ".source.json");

    internal static InstalledPluginSource? ReadSource(string zipPath)
    {
        var path = SourcePath(zipPath);
        if (!File.Exists(path)) return null;
        var source = JsonConvert.DeserializeObject<InstalledPluginSource>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"插件来源记录为空: {path}");
        if (source.RepositoryId <= 0 || source.ReleaseId <= 0 || source.AssetId <= 0)
            throw new InvalidDataException($"插件来源记录无效: {path}");
        using var stream = File.OpenRead(zipPath);
        if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            return null;
        return source;
    }

    internal static void Commit(string stagedZip, string zipPath, InstalledPluginSource source)
    {
        var recordPath = SourcePath(zipPath);
        var recordTemp = stagedZip + ".json";
        var backup = stagedZip + ".backup";
        var hadZip = File.Exists(zipPath);
        var replaced = false;
        try
        {
            using (var record = File.Create(recordTemp))
            {
                record.Write(System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(source)));
                record.Flush(flushToDisk: true);
            }
            if (hadZip) File.Replace(stagedZip, zipPath, backup);
            else File.Move(stagedZip, zipPath);
            replaced = true;
            // Record publication is atomic; a failed publication restores the previous ZIP.
            File.Move(recordTemp, recordPath, overwrite: true);
        }
        catch (Exception commitError)
        {
            if (replaced)
            {
                try
                {
                    if (hadZip) File.Move(backup, zipPath, overwrite: true);
                    else File.Delete(zipPath);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException($"插件提交与回滚均失败；恢复包保留于 {backup}。 / Commit and rollback failed; backup: {backup}",
                        new AggregateException(commitError, rollbackError));
                }
            }
            throw;
        }
        finally
        {
            if (File.Exists(recordTemp)) File.Delete(recordTemp);
        }
        if (File.Exists(backup))
        {
            try { File.Delete(backup); }
            catch (IOException ex) { TerminalGui.TerminalUi.Log("URA", $"插件已提交；备份清理失败: {backup}: {ex.Message}", TerminalGui.UiSeverity.Warning); }
            catch (UnauthorizedAccessException ex) { TerminalGui.TerminalUi.Log("URA", $"插件已提交；备份清理失败: {backup}: {ex.Message}", TerminalGui.UiSeverity.Warning); }
        }
    }
}
