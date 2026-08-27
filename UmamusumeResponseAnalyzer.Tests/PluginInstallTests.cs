using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using UmamusumeResponseAnalyzer.Plugin;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    [Collection("PluginReload")]
    public sealed class PluginInstallTests : IDisposable
    {
        readonly string tempDir;
        readonly string originalCwd;
        readonly Func<string, string, string, CancellationToken, bool> originalConfirmInstall;
        readonly HttpClient originalHttpClient;

        public PluginInstallTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "ura-plugin-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, "Plugins"));
            originalCwd = Directory.GetCurrentDirectory();
            originalConfirmInstall = WebInstallApi.ConfirmInstall;
            originalHttpClient = ResourceUpdater.HttpClient;
            PluginManager.ShutdownAsync().GetAwaiter().GetResult();
            Directory.SetCurrentDirectory(tempDir);
        }

        public void Dispose()
        {
            try
            {
                PluginManager.ShutdownAsync().GetAwaiter().GetResult();
            }
            finally
            {
                WebInstallApi.ConfirmInstall = originalConfirmInstall;
                if (!ReferenceEquals(ResourceUpdater.HttpClient, originalHttpClient))
                {
                    ResourceUpdater.HttpClient.Dispose();
                    ResourceUpdater.HttpClient = originalHttpClient;
                }
                Directory.SetCurrentDirectory(originalCwd);
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public async Task WebInstall_RequiresLocalConfirmationBeforeDownload()
        {
            var confirmationToken = CancellationToken.None;
            WebInstallApi.ConfirmInstall = (_, _, _, cancellationToken) =>
            {
                confirmationToken = cancellationToken;
                return false;
            };
            var port = GetFreePort();
            using var server = new WebserverLite(new WebserverSettings("127.0.0.1", port), ctx => ctx.Response.Send(string.Empty));
            using var requests = new ServerRequestBarrier(TestContext.Current.CancellationToken);
            WebInstallApi.Register(server, requests);
            server.Start(TestContext.Current.CancellationToken);

            using var client = new HttpClient();
            using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/uracloud/install");
            request.Headers.Add("Origin", "https://ura.shuise.net");
            request.Content = new StringContent(
                """{"author":"tester","internalName":"NoConfirm","version":"1.0.0"}""",
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.False(File.Exists(Path.Combine(tempDir, "Plugins", "NoConfirm.zip")));
            Assert.Equal(requests.Token, confirmationToken);
        }

        [Fact]
        public async Task WebInstall_ValidPackageLoadsAndReturnsValidatedManifest()
        {
            const string pluginName = "WebInstallSuccess";
            var package = CompilePackageBytes(pluginName, "2026.03.04");
            ResourceUpdater.HttpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) }));
            WebInstallApi.ConfirmInstall = (_, _, _, _) => true;
            var port = GetFreePort();
            using var server = new WebserverLite(new WebserverSettings("127.0.0.1", port), ctx => ctx.Response.Send(string.Empty));
            using var requests = new ServerRequestBarrier(TestContext.Current.CancellationToken);
            WebInstallApi.Register(server, requests);
            PluginManager.Init();

            try
            {
                server.Start(TestContext.Current.CancellationToken);
                using var client = new HttpClient();
                using var request = CreateWebInstallRequest(port, pluginName, "2026.3.4");
                using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
                var body = JObject.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(body.Value<bool>("ok"));
                Assert.Equal(pluginName, body.Value<string>("installed"));
                Assert.Equal("2026.03.04", body.Value<string>("version"));
                Assert.NotNull(PluginManager.FindLoadedPlugin(pluginName));
            }
            finally
            {
                await PluginManager.ShutdownAsync();
            }
        }

        [Fact]
        public async Task ConcurrentInvalidWebInstallsKeepExistingZipAndRuntime()
        {
            const string pluginName = "WebInstallPreserve";
            var installedPath = Path.Combine(tempDir, PluginRepository.InstallZipPath(pluginName));
            PluginCompiler.CompilePackage(PluginSource, pluginName, installedPath);
            var installedBytes = await File.ReadAllBytesAsync(installedPath, TestContext.Current.CancellationToken);
            var invalidPackage = CompilePackageBytes("WrongWebInstallPlugin");
            ResourceUpdater.HttpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(invalidPackage) }));
            WebInstallApi.ConfirmInstall = (_, _, _, _) => true;
            var port = GetFreePort();
            using var server = new WebserverLite(new WebserverSettings("127.0.0.1", port), ctx => ctx.Response.Send(string.Empty));
            using var requests = new ServerRequestBarrier(TestContext.Current.CancellationToken);
            WebInstallApi.Register(server, requests);
            PluginManager.Init();
            PluginManager.InitializeLoadedPlugins();
            var loaded = Assert.Single(
                PluginManager.SnapshotLoadedPlugins(),
                plugin => PluginManager.InternalName(plugin) == pluginName);

            try
            {
                server.Start(TestContext.Current.CancellationToken);
                using var client = new HttpClient();
                using var firstRequest = CreateWebInstallRequest(port, pluginName, "1.0.0");
                using var secondRequest = CreateWebInstallRequest(port, pluginName, "1.0.0");
                var firstResponseTask = client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
                var secondResponseTask = client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
                using var firstResponse = await firstResponseTask;
                using var secondResponse = await secondResponseTask;

                Assert.Equal(HttpStatusCode.InternalServerError, firstResponse.StatusCode);
                Assert.Equal(HttpStatusCode.InternalServerError, secondResponse.StatusCode);
                Assert.Equal(installedBytes, await File.ReadAllBytesAsync(installedPath, TestContext.Current.CancellationToken));
                Assert.Same(loaded, PluginManager.FindLoadedPlugin(pluginName));
                Assert.Empty(Directory.GetFiles(Path.Combine(tempDir, "Plugins"), "plugin-*.tmp"));
            }
            finally
            {
                await PluginManager.ShutdownAsync();
            }
        }

        [Fact]
        public async Task InstallPluginsAsync_RejectsDuplicateInternalNamesIgnoringCase()
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PluginRepository.InstallPluginsAsync([
                new() { Author = "author-a", InternalName = "SameName", RawVersion = "1.0.0" },
                new() { Author = "author-b", InternalName = "samename", RawVersion = "1.0.0" },
            ], TestContext.Current.CancellationToken));

            Assert.Contains("InternalName 重复", error.Message);
            Assert.False(File.Exists(Path.Combine(tempDir, "Plugins", "SameName.zip")));
        }

        [Fact]
        public async Task InstallPluginsAsync_IsolatesEmptyVersionsAndDownloadFailures()
        {
            var package = CompilePackageBytes("Works");
            ResourceUpdater.HttpClient = new HttpClient(new StubHttpMessageHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/EmptyVersions/versions", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
                if (path.EndsWith("/FailedDownload/versions", StringComparison.Ordinal)
                    || path.EndsWith("/Works/versions", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"version\":\"1.0.0\"}]", Encoding.UTF8, "application/json") };
                if (path.Contains("/FailedDownload/versions/", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                if (path.Contains("/Works/versions/", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));

            var installed = await PluginRepository.InstallPluginsAsync([
                new() { Author = "Tests", InternalName = "EmptyVersions", RawVersion = "1.0.0" },
                new() { Author = "Tests", InternalName = "FailedDownload", RawVersion = "1.0.0" },
                new() { Author = "Tests", InternalName = "Works", RawVersion = "1.0.0" },
            ], TestContext.Current.CancellationToken);

            Assert.Equal(["Works"], installed);
            Assert.False(File.Exists(Path.Combine(tempDir, "Plugins", "EmptyVersions.zip")));
            Assert.False(File.Exists(Path.Combine(tempDir, "Plugins", "FailedDownload.zip")));
            Assert.Equal(package, await File.ReadAllBytesAsync(Path.Combine(tempDir, "Plugins", "Works.zip"), TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task DownloadPluginZipAsync_WhenCopyFails_KeepsExistingZipAndDeletesTempFile()
        {
            var existing = Path.Combine(tempDir, PluginRepository.InstallZipPath("KeepOld"));
            await File.WriteAllTextAsync(existing, "old-zip", TestContext.Current.CancellationToken);
            var url = StartOneShotHttpServer("partial", declaredLength: 100);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                PluginRepository.DownloadPluginZipAsync(
                    url,
                    "Tests",
                    "KeepOld",
                    "1.0.0",
                    TestContext.Current.CancellationToken));

            Assert.Equal("old-zip", await File.ReadAllTextAsync(existing, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(Path.Combine(tempDir, "Plugins"), "plugin-*.tmp"));
        }

        [Theory]
        [InlineData("malformed")]
        [InlineData("identity")]
        [InlineData("version")]
        public async Task DownloadPluginZipAsync_InvalidPackageKeepsExistingZipAndDeletesTempFile(string invalidKind)
        {
            var existing = Path.Combine(tempDir, PluginRepository.InstallZipPath("KeepOld"));
            var existingBytes = Encoding.UTF8.GetBytes("old-zip");
            await File.WriteAllBytesAsync(existing, existingBytes, TestContext.Current.CancellationToken);
            var package = invalidKind switch
            {
                "malformed" => Encoding.UTF8.GetBytes("not-a-zip"),
                "identity" => CompilePackageBytes("WrongName"),
                "version" => CompilePackageBytes("KeepOld", "2.0.0"),
                _ => throw new ArgumentOutOfRangeException(nameof(invalidKind)),
            };
            ResourceUpdater.HttpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) }));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PluginRepository.DownloadPluginZipAsync(
                    "https://example.invalid/plugin.zip",
                    "Tests",
                    "KeepOld",
                    "1.0.0",
                    TestContext.Current.CancellationToken));

            Assert.Equal(existingBytes, await File.ReadAllBytesAsync(existing, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(Path.Combine(tempDir, "Plugins"), "plugin-*.tmp"));
        }

        [Fact]
        public async Task DownloadPluginZipAsync_CleansStaleTempFiles()
        {
            var package = CompilePackageBytes("Fresh");
            ResourceUpdater.HttpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) }));
            var stale = Path.Combine(tempDir, "Plugins", "plugin-stale.tmp");
            await File.WriteAllTextAsync(stale, "stale", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

            var manifest = await PluginRepository.DownloadPluginZipAsync(
                "https://example.invalid/plugin.zip",
                "Tests",
                "Fresh",
                "1.0.0",
                TestContext.Current.CancellationToken);

            Assert.Equal("Fresh", manifest.InternalName);
            Assert.Equal(package, await File.ReadAllBytesAsync(Path.Combine(tempDir, "Plugins", "Fresh.zip"), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(stale));
        }

        byte[] CompilePackageBytes(string internalName, string version = "1.0.0")
        {
            var packagePath = Path.Combine(tempDir, $"package-{Guid.NewGuid():N}.zip");
            try
            {
                PluginCompiler.CompilePackage(PluginSource, internalName, packagePath, version: version);
                return File.ReadAllBytes(packagePath);
            }
            finally
            {
                File.Delete(packagePath);
            }
        }

        static HttpRequestMessage CreateWebInstallRequest(int port, string internalName, string version)
        {
            var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/uracloud/install");
            request.Headers.Add("Origin", "https://ura.shuise.net");
            request.Content = new StringContent(
                $$"""{"author":"Tests","internalName":"{{internalName}}","version":"{{version}}"}""",
                Encoding.UTF8,
                "application/json");
            return request;
        }

        static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        static string StartOneShotHttpServer(string body, int declaredLength)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAtLeastAsync(buffer, minimumBytes: 1, throwOnEndOfStream: false, cancellationToken: TestContext.Current.CancellationToken);
                var header = $"HTTP/1.1 200 OK\r\nContent-Length: {declaredLength}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
                listener.Stop();
            });
            return $"http://127.0.0.1:{port}/plugin.zip";
        }

        sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(send(request));
        }

        const string PluginSource = """
            using UmamusumeResponseAnalyzer.Plugin;

            public sealed class TestPlugin : IPlugin
            {
                public void Initialize(IPluginContext context) { }
            }
            """;
    }
}
