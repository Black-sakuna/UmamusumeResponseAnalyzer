using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    /// <summary>
    /// <see cref="PluginRepository.BuildCatalog"/> 的确定性单测——不依赖网络/Config。
    /// 锁定一个真实回归:仓库目录必须保留“同名不同作者”的 fork(插件身份是
    /// (Author, InternalName) 复合键)。旧实现用 Dictionary 拿 InternalName 当 key,
    /// 后到的 fork 会覆盖先到的,导致目录里只剩一个、其余连分类一起“消失”
    /// (现场表现:插件仓库里几乎全部归到了同一组)。
    /// </summary>
    public class PluginRepositoryTests
    {
        static PluginInformation Info(string author, string internalName, string category = "", string[]? targets = null) => new()
        {
            Author = author,
            InternalName = internalName,
            Category = category,
            Targets = targets ?? [],
            Version = new(1, 0, 0),
        };

        static readonly string[] NoFilter = [];
        const string ApiBase = "https://ura.shuise.net/api/Plugins";

        [Fact]
        public void BuildCatalog_KeepsSameInternalNameDifferentAuthorForks()
        {
            // 回归:离披 与 URACloud-Tester 各有一个 StatisticsCollector,二者都必须保留,
            // 各自的分类也不能被对方覆盖。
            PluginInformation[] raw =
            [
                Info("离披", "StatisticsCollector", category: "数据收集"),
                Info("URACloud-Tester", "StatisticsCollector", category: ""),
            ];

            var catalog = PluginRepository.BuildCatalog(raw, NoFilter);

            Assert.Equal(2, catalog.Count);
            Assert.Contains(catalog, p => p.Author == "离披" && p.Category == "数据收集");
            Assert.Contains(catalog, p => p.Author == "URACloud-Tester" && p.Category == "");
        }

        [Fact]
        public void BuildCatalog_BuildsDownloadUrlPerForkAuthor()
        {
            // DownloadUrl 必须用各自的 Author 拼,否则一个 fork 会下到另一个作者的包。
            PluginInformation[] raw =
            [
                Info("离披", "StatisticsCollector"),
                Info("URACloud-Tester", "StatisticsCollector"),
            ];

            var catalog = PluginRepository.BuildCatalog(raw, NoFilter);

            Assert.Contains(catalog, p => p.DownloadUrl == $"{ApiBase}/%E7%A6%BB%E6%8A%AB/StatisticsCollector/versions/1.0.0/download");
            Assert.Contains(catalog, p => p.DownloadUrl == $"{ApiBase}/URACloud-Tester/StatisticsCollector/versions/1.0.0/download");
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
        public void TruncateToWidth_ShortStringUnchanged() =>
            Assert.Equal("abc", PluginRepository.TruncateToWidth("abc", 10));

        [Fact]
        public void TruncateToWidth_LongAsciiCutWithEllipsis() =>
            Assert.Equal("abcd…", PluginRepository.TruncateToWidth("abcdefghij", 5));

        [Fact]
        public void TruncateToWidth_CountsCjkAsTwoColumns() =>
            // 5 个 CJK = 10 列;maxWidth 6 → 省略号也占 1 列,最终宽度必须 <= 6。
            Assert.Equal("中文…", PluginRepository.TruncateToWidth("中文测试啊", 6));

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
            // 拼出的下载 URL 与服务器(.../2026.03.04/download)不符 → 404 → 安装失败(再叠加 markup bug 就崩)。
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
        public void ResolveDependencies_AddsTransitiveDependencies()
        {
            // 回归:安装 A 时若 A -> B -> C,旧实现只追加 B,导致 C 不会下载到本地。
            var root = Info("a", "Root");
            root.Dependencies = ["Middle"];
            var middle = Info("a", "Middle");
            middle.Dependencies = ["Leaf"];
            var leaf = Info("a", "Leaf");
            var selected = new List<PluginInformation> { root };
            var catalog = new List<PluginInformation> { root, middle, leaf };

            PluginRepository.ResolveDependencies(selected, catalog);

            Assert.Equal(["Root", "Middle", "Leaf"], selected.Select(p => p.InternalName).ToArray());
        }

        [Fact]
        public void ResolveDependencies_ThrowsWhenDependencyIsMissing()
        {
            var root = Info("a", "Root");
            root.Dependencies = ["Missing"];

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PluginRepository.ResolveDependencies([root], [root]));

            Assert.Contains("Missing", exception.Message);
        }

        [Fact]
        public void ResolveDependencies_ThrowsWhenUnselectedDependencyHasMultipleForks()
        {
            var root = Info("a", "Root");
            root.Dependencies = ["Dependency"];
            var firstFork = Info("a", "Dependency");
            var secondFork = Info("b", "Dependency");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                PluginRepository.ResolveDependencies([root], [root, firstFork, secondFork]));

            Assert.Contains("Dependency", exception.Message);
            Assert.Contains("多个 fork", exception.Message);
        }

        [Fact]
        public void ResolveDependencies_PrefersSelectedFork()
        {
            var root = Info("a", "Root");
            root.Dependencies = ["Dependency"];
            var selectedFork = Info("a", "Dependency");
            var otherFork = Info("b", "Dependency");
            var selected = new List<PluginInformation> { root, selectedFork };

            PluginRepository.ResolveDependencies(selected, [root, selectedFork, otherFork]);

            Assert.Equal([root, selectedFork], selected);
        }
    }
}
