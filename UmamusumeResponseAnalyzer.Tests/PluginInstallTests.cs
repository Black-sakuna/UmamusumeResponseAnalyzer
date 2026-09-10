using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UmamusumeResponseAnalyzer.Plugin;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("PluginReload")]
public sealed class PluginInstallTests : IDisposable
{
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "ura-plugin-install-" + Guid.NewGuid().ToString("N"));
    readonly string originalCwd = Directory.GetCurrentDirectory();
    readonly Func<PluginRelease, CancellationToken, bool> originalConfirm = PluginRepository.ConfirmInstall;
    readonly HttpClient originalHttp = ResourceUpdater.HttpClient;
    readonly PackageHandler handler;
    readonly byte[] package;
    const string Name = "InstallFixture";

    public PluginInstallTests()
    {
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "Plugins"));
        PluginManager.ShutdownAsync().GetAwaiter().GetResult();
        Directory.SetCurrentDirectory(tempDir);
        var path = Path.Combine(tempDir, "fixture.zip");
        PluginCompiler.CompilePackage(PluginCode, Name, path, version: "2026.03.04");
        package = File.ReadAllBytes(path);
        var manifest = PluginPackageValidator.Validate(path, false).Manifest;
        handler = new PackageHandler(package, new(
            new(1, "owner/repository", 7, "owner", "User", "https://github.com/owner/repository", false, null, null),
            10, "release", "https://github.com/owner/repository/releases/10", false, 1, 100,
            Convert.ToHexStringLower(SHA256.HashData(package)), manifest));
        ResourceUpdater.HttpClient = new HttpClient(handler);
        PluginRepository.ConfirmInstall = (_, _) => true;
        PluginManager.Init();
        PluginManager.InitializeLoadedPlugins();
    }

    public void Dispose()
    {
        try { PluginManager.ShutdownAsync().GetAwaiter().GetResult(); }
        finally
        {
            PluginRepository.ConfirmInstall = originalConfirm;
            ResourceUpdater.HttpClient.Dispose();
            ResourceUpdater.HttpClient = originalHttp;
            Directory.SetCurrentDirectory(originalCwd);
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task LocalAndWebCancellationAndBusyStateNeverDownload()
    {
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        PluginRepository.ConfirmInstall = (_, ct) => { entered.Set(); finish.Wait(ct); return false; };
        var first = Task.Run(() => PluginRepository.InstallByReferenceAsync(1, 10, TestContext.Current.CancellationToken));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            var second = await PluginRepository.InstallByReferenceAsync(1, 10, TestContext.Current.CancellationToken);
            Assert.False(second.Ok);
            Assert.False(second.Loaded);
        }
        finally { finish.Set(); }
        Assert.False((await first).Ok);
        Assert.Equal(0, handler.Downloads);
        Assert.Empty(Directory.GetFiles("Plugins"));

        PluginRepository.ConfirmInstall = (_, _) => false;
        using var requests = new ServerRequestBarrier(TestContext.Current.CancellationToken);
        using var server = StartServer(requests, out var port);
        using var client = new HttpClient();
        using var request = WebRequest(port);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, handler.Downloads);
        Assert.Empty(Directory.GetFiles("Plugins"));
    }

    [Fact]
    public async Task WebInstallUsesNumericDescriptorAndSavesSourceBeforeLoading()
    {
        PluginRelease? confirmed = null;
        PluginRepository.ConfirmInstall = (d, _) => { confirmed = d; return true; };
        using var requests = new ServerRequestBarrier(TestContext.Current.CancellationToken);
        using var server = StartServer(requests, out var port);
        using var client = new HttpClient();
        using var request = WebRequest(port);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.Value<bool>("ok"));
        Assert.True(body.Value<bool>("loaded"));
        Assert.Equal("owner/repository", confirmed!.Source.FullName);
        Assert.Equal(Name, body.Value<string>("installed"));
        Assert.Equal(handler.Descriptor.InstalledSource, PluginInstallStorage.ReadSource(PluginRepository.InstallZipPath(Name)));
        Assert.NotNull(PluginManager.FindLoadedPlugin(Name));
        var status = JObject.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/uracloud/status", TestContext.Current.CancellationToken));
        Assert.Equal(1L, status["plugins"]![0]!["source"]!["repositoryId"]!.Value<long>());
        Assert.True(status["plugins"]![0]!["loaded"]!.Value<bool>());
        Assert.Equal(1, handler.Downloads);
    }

    [Fact]
    public async Task DescriptorOrBytesChangingAbortsWithoutReplacingInstalledPackage()
    {
        var zip = PluginRepository.InstallZipPath(Name);
        File.WriteAllBytes(zip, package);
        var original = handler.Descriptor;
        PluginRepository.ConfirmInstall = (_, _) => { handler.Descriptor = original with { AssetId = 101 }; return true; };
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginRepository.InstallByReferenceAsync(1, 10));
        Assert.Equal(0, handler.Downloads);
        PluginRepository.ConfirmInstall = (_, _) => true;
        handler.Descriptor = original;
        handler.Bytes = [1, 2, 3];
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginRepository.InstallByReferenceAsync(1, 10));
        Assert.Equal(package, File.ReadAllBytes(zip));
        Assert.Null(PluginInstallStorage.ReadSource(zip));
        Assert.Empty(Directory.GetFiles("Plugins", "*.tmp"));
        handler.Bytes = package;
        handler.AfterDownload = () => handler.Descriptor = original with { Source = original.Source with { OwnerId = 9 } };
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginRepository.InstallByReferenceAsync(1, 10));
        Assert.Equal(package, File.ReadAllBytes(zip));
    }

    [Fact]
    public async Task SourceCommitFailureRollsBackZipAndLoadFailureRetainsAccurateRecord()
    {
        var zip = PluginRepository.InstallZipPath(Name);
        var oldBytes = new byte[] { 4, 5, 6 };
        File.WriteAllBytes(zip, oldBytes);
        var sourcePath = PluginInstallStorage.SourcePath(zip);
        Directory.CreateDirectory(sourcePath);
        var error = await Record.ExceptionAsync(() => PluginRepository.InstallByReferenceAsync(1, 10));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(oldBytes, File.ReadAllBytes(zip));
        Assert.Null(PluginManager.FindLoadedPlugin(Name));
        Directory.Delete(sourcePath);
        File.Delete(zip);
        var brokenPath = Path.Combine(tempDir, "broken.zip");
        PluginCompiler.CompilePackage(PluginCode.Replace("{ }", "{ throw new System.InvalidOperationException(\"load failure\"); }"),
            Name, brokenPath, version: "2026.03.04");
        handler.Bytes = File.ReadAllBytes(brokenPath);
        handler.Descriptor = handler.Descriptor with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(handler.Bytes)) };
        var result = await PluginRepository.InstallByReferenceAsync(1, 10);
        Assert.True(result.Ok);
        Assert.False(result.Loaded);
        Assert.Equal(handler.Descriptor.InstalledSource, PluginInstallStorage.ReadSource(zip));
        Assert.Null(PluginManager.FindLoadedPlugin(Name));
    }

    [Fact]
    public async Task UpdatesCoverUnloadedPackagesAndUnknownSourcesAndSwitchesExplainDependencies()
    {
        var zip = PluginRepository.InstallZipPath(Name);
        File.WriteAllBytes(zip, package);
        File.WriteAllText(PluginInstallStorage.SourcePath(zip), JsonConvert.SerializeObject(handler.Descriptor.InstalledSource with { Prerelease = true }));
        var updates = await PluginRepository.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        Assert.Single(updates);
        Assert.Equal(updates[0].CurrentVersion, updates[0].LatestVersion);
        Assert.Null(PluginManager.FindLoadedPlugin(Name));
        PluginCompiler.CompilePackage(PluginCode, "Dependent", Path.Combine("Plugins", "Dependent.zip"), dependencies: [Name]);
        var switched = handler.Descriptor with { Source = handler.Descriptor.Source with { RepositoryId = 2, FullName = "fork/repository" } };
        var message = PluginRepository.BuildInstallConfirmation(switched);
        Assert.Contains("fork/repository", message);
        Assert.Contains("Dependent", message);
        Assert.Contains(Name, message);
        Assert.Contains("URACloud", message);
        Assert.False(switched.Source.Verified);
        File.AppendAllText(zip, "locally changed");
        Assert.Null(PluginInstallStorage.ReadSource(zip));
        Assert.Empty(await PluginRepository.CheckForUpdatesAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PluginRepository.InstallPluginsAsync([handler.Descriptor, switched]));
    }

    internal static WebserverLite StartServer(ServerRequestBarrier requests, out int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var server = new WebserverLite(new WebserverSettings("127.0.0.1", port), ctx => ctx.Response.Send(string.Empty));
        WebInstallApi.Register(server, requests);
        server.Start(TestContext.Current.CancellationToken);
        return server;
    }

    static HttpRequestMessage WebRequest(int port)
    {
        var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"http://127.0.0.1:{port}/uracloud/install");
        request.Headers.Add("Origin", "https://ura.shuise.net");
        request.Content = new StringContent("""{"repositoryId":1,"releaseId":10}""", Encoding.UTF8, "application/json");
        return request;
    }

    sealed class PackageHandler(byte[] bytes, PluginRelease descriptor) : HttpMessageHandler
    {
        internal byte[] Bytes = bytes;
        internal PluginRelease Descriptor = descriptor;
        internal int Downloads;
        internal Action? AfterDownload;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/download"))
            {
                Downloads++;
                Assert.Equal($"\"{Descriptor.AssetId}-{Descriptor.Sha256}\"", request.Headers.IfMatch.Single().Tag);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
                response.Headers.ETag = new EntityTagHeaderValue($"\"{Descriptor.AssetId}-{Descriptor.Sha256}\"");
                AfterDownload?.Invoke();
                return Task.FromResult(response);
            }
            var body = request.RequestUri.AbsolutePath.EndsWith("/Plugins")
                ? JsonConvert.SerializeObject(new[] { Descriptor }) : JsonConvert.SerializeObject(Descriptor);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    const string PluginCode = """
        using UmamusumeResponseAnalyzer.Plugin;
        public sealed class TestPlugin : IPlugin
        {
            public void Initialize(IPluginContext context) { }
        }
        """;
}
