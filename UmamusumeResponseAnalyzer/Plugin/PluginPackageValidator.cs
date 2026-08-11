using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace UmamusumeResponseAnalyzer.Plugin;

internal sealed record ValidatedPluginPackage(
    PluginInformation Manifest,
    string MainAssemblyEntry,
    IReadOnlyDictionary<string, string> Assemblies,
    IReadOnlyList<string> Entries);

internal static class PluginPackageValidator
{
    static readonly string[] ManifestPropertyNames =
    [
        "Author",
        "InternalName",
        "DisplayName",
        "Description",
        "Changelog",
        "Version",
        "Dependencies",
        "Targets",
        "RepositoryUrl",
        "LastUpdate",
        "Category",
        "Homepage",
    ];

    internal static ValidatedPluginPackage Validate(
        string packagePath,
        bool requireMatchingPackageFileName)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntries = archive.Entries
            .Where(entry => IsRoot(entry.FullName) &&
                            string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (manifestEntries.Length != 1 || manifestEntries[0].FullName != "manifest.json")
            throw new InvalidDataException("ZIP 根目录必须且只能包含一个 manifest.json。");

        PluginInformation manifest;
        using (var stream = manifestEntries[0].Open())
        {
            try
            {
                using var json = JsonDocument.Parse(stream, new()
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
                manifest = ParseManifest(json.RootElement);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidDataException("manifest.json 不是严格 JSON。", ex);
            }
        }

        ValidateManifest(manifest);
        if (requireMatchingPackageFileName)
        {
            var packageName = Path.GetFileNameWithoutExtension(packagePath);
            if (!string.Equals(packageName, manifest.InternalName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"ZIP 文件名必须等于 manifest InternalName: zip={packageName}, manifest={manifest.InternalName}");
        }

        var mainEntryName = $"{manifest.InternalName}.dll";
        var rootDlls = archive.Entries
            .Where(entry => IsRoot(entry.FullName) &&
                            entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var mainEntries = rootDlls
            .Where(entry => string.Equals(entry.FullName, mainEntryName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (mainEntries.Length != 1)
            throw new InvalidDataException($"ZIP 根目录必须且只能包含一个主程序集 {mainEntryName}。");
        ValidateMainAssemblyIdentity(mainEntries[0], manifest.InternalName);

        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rootDlls)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(entry.FullName);
            if (!assemblies.TryAdd(assemblyName, entry.FullName))
                throw new InvalidDataException($"ZIP 根目录程序集名重复: {assemblyName}");
        }

        return new(
            manifest,
            mainEntries[0].FullName,
            assemblies,
            [.. archive.Entries.Select(entry => entry.FullName)]);
    }

    static PluginInformation ParseManifest(JsonElement manifest)
    {
        if (manifest.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("manifest.json 根节点必须是 object。");

        var properties = manifest.EnumerateObject().ToArray();
        var actualProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties)
            if (!actualProperties.Add(property.Name))
                throw new InvalidDataException($"manifest 包含重复字段: {property.Name}");

        var missing = ManifestPropertyNames.Except(actualProperties, StringComparer.Ordinal).ToArray();
        var unexpected = actualProperties.Except(ManifestPropertyNames, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
            throw new InvalidDataException(
                $"manifest schema 不匹配: missing=[{string.Join(", ", missing)}], " +
                $"unexpected=[{string.Join(", ", unexpected)}]");

        string String(string name)
        {
            var value = manifest.GetProperty(name);
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"manifest {name} 类型无效: expected=String, actual={value.ValueKind}");
            return value.GetString()!;
        }

        string[] Strings(string name)
        {
            var value = manifest.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"manifest {name} 类型无效: expected=Array, actual={value.ValueKind}");
            return value.EnumerateArray().Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"manifest {name} 只能包含字符串。");
                return item.GetString()!;
            }).ToArray();
        }

        var lastUpdate = manifest.GetProperty("LastUpdate");
        if (lastUpdate.ValueKind != JsonValueKind.Number || !lastUpdate.TryGetInt64(out var lastUpdateValue))
            throw new InvalidDataException(
                $"manifest LastUpdate 类型无效: expected=Integer, actual={lastUpdate.ValueKind}");

        return new()
        {
            Author = String("Author"),
            InternalName = String("InternalName"),
            DisplayName = String("DisplayName"),
            Description = String("Description"),
            Changelog = String("Changelog"),
            RawVersion = String("Version"),
            Dependencies = Strings("Dependencies"),
            Targets = Strings("Targets"),
            RepositoryUrl = String("RepositoryUrl"),
            LastUpdate = lastUpdateValue,
            Category = String("Category"),
            Homepage = String("Homepage"),
        };
    }

    static void ValidateMainAssemblyIdentity(ZipArchiveEntry entry, string internalName)
    {
        using var source = entry.Open();
        using var stream = new MemoryStream();
        source.CopyTo(stream);
        stream.Position = 0;
        try
        {
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                throw new BadImageFormatException("主程序集没有 CLR metadata。");
            var metadata = pe.GetMetadataReader();
            var assembly = metadata.GetAssemblyDefinition();
            var assemblyName = metadata.GetString(assembly.Name);
            ValidateAssemblyIdentity(assemblyName, internalName);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"主程序集不是有效的 managed assembly: {entry.FullName}", ex);
        }
    }

    internal static void ValidateAssemblyIdentity(string? assemblyName, string internalName)
    {
        if (!string.Equals(assemblyName, internalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"主程序集名称必须等于 manifest InternalName: assembly={assemblyName ?? "<null>"}, " +
                $"manifest={internalName}");
    }

    static void ValidateManifest(PluginInformation manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Author))
            throw new InvalidDataException("manifest Author 不能为空。");
        if (string.IsNullOrWhiteSpace(manifest.InternalName))
            throw new InvalidDataException("manifest InternalName 不能为空。");
        if (manifest.InternalName != Path.GetFileName(manifest.InternalName) ||
            manifest.InternalName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"manifest InternalName 不是有效文件名: {manifest.InternalName}");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
            throw new InvalidDataException("manifest DisplayName 不能为空。");
        if (string.IsNullOrWhiteSpace(manifest.RawVersion) ||
            !Version.TryParse(manifest.RawVersion, out _))
            throw new InvalidDataException($"manifest Version 无效: {manifest.RawVersion}");
        if (manifest.Dependencies is null)
            throw new InvalidDataException("manifest Dependencies 不能为 null。");
        if (manifest.Targets is null)
            throw new InvalidDataException("manifest Targets 不能为 null。");

        ValidateNames(manifest.Dependencies, "Dependencies");
        ValidateNames(manifest.Targets, "Targets");
        if (manifest.Dependencies.Contains(manifest.InternalName, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"插件不能依赖自身: {manifest.InternalName}");
    }

    static void ValidateNames(IEnumerable<string> names, string field)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"manifest {field} 不能包含空名称。");
            if (!seen.Add(name))
                throw new InvalidDataException($"manifest {field} 包含重复名称: {name}");
        }
    }

    static bool IsRoot(string entryName)
        => entryName.IndexOfAny(['/', '\\']) < 0;
}
