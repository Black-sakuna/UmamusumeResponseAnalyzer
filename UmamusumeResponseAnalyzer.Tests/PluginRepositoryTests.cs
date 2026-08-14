using System.IO.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    /// <summary>
    /// <see cref="PluginRepository.BuildCatalog"/> 的确定性单测——不依赖网络/Config。
    /// 插件身份只由 InternalName 决定，并使用 OrdinalIgnoreCase 比较。
    /// </summary>
    public class PluginRepositoryTests
    {
        static PluginInformation Info(string author, string internalName, string category = "", string[]? targets = null) => new()
        {
            Author = author,
            InternalName = internalName,
            DisplayName = internalName,
            Category = category,
            Targets = targets ?? [],
            Version = new(1, 0, 0),
        };

        static readonly string[] NoFilter = [];
        const string ApiBase = "https://ura.shuise.net/api/Plugins";
        const string PackageInternalName = "UmamusumeResponseAnalyzer";

        [Fact]
        public void BuildCatalog_RejectsDuplicateInternalNamesIgnoringCase()
        {
            PluginInformation[] raw =
            [
                Info("离披", "StatisticsCollector", category: "数据收集"),
                Info("URACloud-Tester", "statisticscollector", category: ""),
            ];

            var error = Assert.Throws<InvalidDataException>(() =>
                PluginRepository.BuildCatalog(raw, NoFilter));

            Assert.Contains("InternalName 重复", error.Message);
            Assert.Contains("statisticscollector", error.Message);
        }

        [Fact]
        public void BuildCatalog_BuildsDownloadUrlFromManifestAuthor()
        {
            PluginInformation[] raw = [Info("离披", "StatisticsCollector")];

            var catalog = PluginRepository.BuildCatalog(raw, NoFilter);

            Assert.Equal(
                $"{ApiBase}/%E7%A6%BB%E6%8A%AB/StatisticsCollector/versions/1.0.0/download",
                Assert.Single(catalog).DownloadUrl);
        }

        [Fact]
        public void BuildCatalog_RejectsRowsMissingAuthorOrInternalName()
        {
            Assert.Throws<InvalidDataException>(() => PluginRepository.BuildCatalog([Info("", "HasNoAuthor")], NoFilter));
            Assert.Throws<InvalidDataException>(() => PluginRepository.BuildCatalog([Info("HasNoName", "")], NoFilter));
        }

        [Fact]
        public void BuildCatalog_RejectsNullEntries()
        {
            Assert.Throws<InvalidDataException>(() => PluginRepository.BuildCatalog([null!], NoFilter));
        }

        [Fact]
        public void BuildCatalog_EmptyTargetsMeansAllServers_EvenWithFilterActive()
        {
            // 约定:插件 Targets 为空 = 支持所有服。即便用户设了目标过滤,空 Targets 也必须通过。
            PluginInformation[] raw =
            [
                Info("a", "EmptyTargets", targets: []),
                Info("a", "CygamesOnly", targets: ["Cygames"]),
                Info("a", "KomoeOnly", targets: ["Komoe"]),
            ];

            var catalog = PluginRepository.BuildCatalog(raw, ["Cygames"]);

            Assert.Contains(catalog, p => p.InternalName == "EmptyTargets");    // 空 = 全服,通过
            Assert.Contains(catalog, p => p.InternalName == "CygamesOnly");     // 命中过滤
            Assert.DoesNotContain(catalog, p => p.InternalName == "KomoeOnly"); // 不匹配,过滤掉
        }

        [Fact]
        public void BuildCatalog_NoFilterReturnsEverythingRegardlessOfTargets()
        {
            PluginInformation[] raw =
            [
                Info("a", "P1", targets: ["Komoe"]),
                Info("a", "P2", targets: []),
            ];

            var catalog = PluginRepository.BuildCatalog(raw, NoFilter);

            Assert.Equal(2, catalog.Count);
        }

        [Fact]
        public void InstallZipPath_PlacesZipUnderPluginsDir()
        {
            // 回归(URACloud 迁移引入):安装曾 ExtractToDirectory("./") 把插件解到 WORKING_DIRECTORY 根,
            // 而 ScanAll 只扫 Plugins/ → 装了的插件永远扫不到、热重载/重启都不加载(实测复现)。
            // 落地必须在 Plugins/ 下、且为 {InternalName}.zip(对齐 ScanAll 的 Plugins/*.zip 与本地开发 deploy)。
            var path = PluginRepository.InstallZipPath("StatisticsCollector");

            Assert.Equal(Path.Combine("Plugins", "StatisticsCollector.zip"), path);
            // 关键不变量:父目录是 Plugins(不是 CWD 根),否则就是上面那个 bug
            Assert.Equal("Plugins", Path.GetDirectoryName(path));
        }

        [Fact]
        public void Bracketed_UsesLiteralBrackets()
        {
            Assert.Equal("[梦想杯剧本解析器]", PluginRepository.Bracketed("梦想杯剧本解析器"));
            Assert.Equal("[a[x]]", PluginRepository.Bracketed("a[x]"));
        }

        [Fact]
        public void BuildCatalog_DownloadUrl_PreservesRawVersionWithLeadingZeros()
        {
            // 回归(noVNC 实测):版本 "2026.03.04"(前导零)经 System.Version 归一会变 "2026.3.4",
            // 拼出的下载 URL 与服务器(.../2026.03.04/download)不符 → 404 → 安装失败。
            // 下载 URL 必须用服务器原样的 RawVersion;比较/排序仍用强类型 Version(前导零无所谓)。
            var raw = new PluginInformation { Author = "URACloud-Tester", InternalName = "BreedersScenarioAnalyzer", RawVersion = "2026.03.04" };
            Assert.Equal(new System.Version(2026, 3, 4), raw.Version);

            var catalog = PluginRepository.BuildCatalog([raw], NoFilter);

            Assert.Single(catalog);
            Assert.Equal($"{ApiBase}/URACloud-Tester/BreedersScenarioAnalyzer/versions/2026.03.04/download", catalog[0].DownloadUrl);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-version")]
        public void BuildCatalog_RejectsInvalidVersion(string rawVersion)
        {
            var plugin = Info("author", "InvalidVersion");
            plugin.RawVersion = rawVersion;

            Assert.Throws<InvalidDataException>(() => PluginRepository.BuildCatalog([plugin], NoFilter));
            Assert.ThrowsAny<ArgumentException>(() => _ = plugin.Version);
        }

        [Fact]
        public void ValidatePackage_ReturnsStrictManifestMetadata()
        {
            var manifest = Info("author", PackageInternalName, targets: ["Cygames"]);
            manifest.RawVersion = "2026.03.04";
            manifest.Dependencies = ["Dependency"];
            var package = CreatePackage(manifest);
            try
            {
                var actual = PluginRepository.ValidatePackage(
                    package,
                    "AUTHOR",
                    "umamusumeresponseanalyzer",
                    "2026.3.4");

                Assert.Equal(PackageInternalName, actual.InternalName);
                Assert.Equal(["Dependency"], actual.Dependencies);
                Assert.Equal(["Cygames"], actual.Targets);
                Assert.Equal("2026.03.04", actual.RawVersion);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_RejectsNonCurrentManifestSchema()
        {
            var manifest = Info("author", PackageInternalName);
            var package = CreatePackage(manifest, json =>
            {
                json["version"] = json["Version"];
                json.Remove("Version");
            });
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, "author", PackageInternalName, "1.0.0"));

                Assert.Contains("missing=[Version]", error.Message);
                Assert.Contains("unexpected=[version]", error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Theory]
        [InlineData("other", PackageInternalName, "1.0.0", "Author")]
        [InlineData("author", "OtherPlugin", "1.0.0", "InternalName")]
        [InlineData("author", PackageInternalName, "2.0.0", "Version")]
        public void ValidatePackage_RejectsRequestedIdentityMismatch(
            string author,
            string internalName,
            string version,
            string field)
        {
            var package = CreatePackage(Info("author", PackageInternalName));
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, author, internalName, version));

                Assert.Contains(field, error.Message);
                Assert.Contains("不匹配", error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_RequiresRootMainAssembly()
        {
            var package = CreatePackage(
                Info("author", PackageInternalName),
                mainAssemblyPath: $"lib/{PackageInternalName}.dll");
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, "author", PackageInternalName, "1.0.0"));

                Assert.Contains("根目录", error.Message);
                Assert.Contains($"{PackageInternalName}.dll", error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_DoesNotTreatBackslashNestedDllAsRootAssembly()
        {
            var package = CreatePackage(
                Info("author", PackageInternalName),
                additionalEntries: ["Dependency.dll", @"lib\dependency.DLL"]);
            try
            {
                var manifest = PluginRepository.ValidatePackage(
                    package,
                    "author",
                    PackageInternalName,
                    "1.0.0");

                Assert.Equal(PackageInternalName, manifest.InternalName);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_RejectsDuplicateRootAssemblyNamesIgnoringCase()
        {
            var package = CreatePackage(
                Info("author", PackageInternalName),
                additionalEntries: ["Dependency.dll", "dependency.DLL"]);
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, "author", PackageInternalName, "1.0.0"));

                Assert.Contains("程序集名重复", error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_RejectsCaseDuplicateRootManifest()
        {
            var package = CreatePackage(
                Info("author", PackageInternalName),
                additionalEntries: ["Manifest.json"]);
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, "author", PackageInternalName, "1.0.0"));

                Assert.Contains("manifest.json", error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Theory]
        [InlineData("LastUpdate", "not-an-integer", "类型无效")]
        [InlineData("Dependencies", 1, "只能包含字符串")]
        public void ValidatePackage_RejectsWrongManifestTokenShape(
            string property,
            object value,
            string expectedMessage)
        {
            var package = CreatePackage(Info("author", PackageInternalName), json =>
            {
                if (property == "Dependencies")
                    json[property] = new JArray(value);
                else
                    json[property] = JToken.FromObject(value);
            });
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, "author", PackageInternalName, "1.0.0"));

                Assert.Contains(expectedMessage, error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Theory]
        [InlineData("comment")]
        [InlineData("trailing-comma")]
        [InlineData("single-quotes")]
        [InlineData("extra-token")]
        public void ValidatePackage_RejectsNonStrictJson(string mutation)
        {
            var package = CreatePackage(
                Info("author", PackageInternalName),
                editRawManifest: json => mutation switch
                {
                    "comment" => $"/*comment*/{json}",
                    "trailing-comma" => $"{json[..^1]},}}",
                    "single-quotes" => json.Replace('"', '\''),
                    "extra-token" => $"{json}{{}}",
                    _ => throw new InvalidOperationException($"未知 JSON mutation: {mutation}"),
                });
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, "author", PackageInternalName, "1.0.0"));

                Assert.Contains("严格 JSON", error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_AllowsDotInLoaderValidInternalName()
        {
            var package = CreatePackage(Info("author", PackageInternalName));
            try
            {
                var manifest = PluginRepository.ValidatePackage(
                    package,
                    "author",
                    PackageInternalName,
                    "1.0.0");

                Assert.Equal(PackageInternalName, manifest.InternalName);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_PreservesDateLikeManifestStrings()
        {
            var expected = "2026-08-11T03:16:39.2341390Z";
            var info = Info("author", PackageInternalName);
            info.Description = expected;
            var package = CreatePackage(info);
            try
            {
                var manifest = PluginRepository.ValidatePackage(
                    package,
                    "author",
                    PackageInternalName,
                    "1.0.0");

                Assert.Equal(expected, manifest.Description);
            }
            finally
            {
                File.Delete(package);
            }
        }

        [Fact]
        public void ValidatePackage_RejectsEmbeddedMainAssemblyNameMismatch()
        {
            const string manifestName = "Different.Plugin";
            var package = CreatePackage(Info("author", manifestName));
            try
            {
                var error = Assert.Throws<InvalidDataException>(() =>
                    PluginRepository.ValidatePackage(package, "author", manifestName, "1.0.0"));

                Assert.Contains("主程序集名称", error.Message);
                Assert.Contains(PackageInternalName, error.Message);
                Assert.Contains(manifestName, error.Message);
            }
            finally
            {
                File.Delete(package);
            }
        }

        static string CreatePackage(
            PluginInformation manifest,
            Action<JObject>? editManifest = null,
            string? mainAssemblyPath = null,
            string[]? additionalEntries = null,
            Func<string, string>? editRawManifest = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"ura-repository-{Guid.NewGuid():N}.zip");
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            var json = JObject.FromObject(manifest, JsonSerializer.CreateDefault());
            editManifest?.Invoke(json);
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()))
                writer.Write(editRawManifest?.Invoke(json.ToString(Formatting.None)) ?? json.ToString(Formatting.None));
            using (var source = File.OpenRead(typeof(PluginInformation).Assembly.Location))
            using (var stream = archive.CreateEntry(mainAssemblyPath ?? $"{manifest.InternalName}.dll").Open())
                source.CopyTo(stream);
            foreach (var entry in additionalEntries ?? [])
            {
                using var stream = archive.CreateEntry(entry).Open();
                stream.WriteByte(0);
            }
            return path;
        }
    }
}
