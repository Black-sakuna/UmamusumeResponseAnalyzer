using System.Net;
using Newtonsoft.Json;
using UmamusumeResponseAnalyzer.Plugin;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

public sealed class LifecycleTeardownTests
{
    [Fact]
    public async Task RunCleanupAsync_AttemptsEveryActionAndPreservesWorkflowFailure()
    {
        var workflowFailure = new InvalidOperationException("workflow");
        var calls = new List<int>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UmamusumeResponseAnalyzer.RunCleanupAsync(
                ExceptionDispatchInfo.Capture(workflowFailure),
                [
                    () =>
                    {
                        calls.Add(1);
                        throw new IOException("cleanup-1");
                    },
                    () =>
                    {
                        calls.Add(2);
                        return ValueTask.CompletedTask;
                    },
                    () =>
                    {
                        calls.Add(3);
                        throw new IOException("cleanup-3");
                    },
                ]));

        Assert.Same(workflowFailure, thrown);
        Assert.Equal([1, 2, 3], calls);
    }

    [Fact]
    public async Task RunCleanupAsync_WithoutWorkflowFailureAggregatesCleanupFailures()
    {
        var thrown = await Assert.ThrowsAsync<AggregateException>(() =>
            UmamusumeResponseAnalyzer.RunCleanupAsync(
                null,
                [
                    () => throw new IOException("cleanup-1"),
                    () => throw new InvalidOperationException("cleanup-2"),
                ]));

        Assert.Collection(
            thrown.InnerExceptions,
            ex => Assert.Equal("cleanup-1", ex.Message),
            ex => Assert.Equal("cleanup-2", ex.Message));
    }

    [Fact]
    public async Task StopAsync_PublishesCompletionBeforeCancellationCallbacks()
    {
        const string scenario = "server-stop-reentrant";
        if (TerminalUiLifecycleChildProcess.IsChild(scenario))
        {
            await RunReentrantServerStopAsync();
            TerminalUiLifecycleChildProcess.WriteResult("ok");
            return;
        }

        Assert.Equal(
            "ok",
            await TerminalUiLifecycleProcessTests.RunChildAsync(
                scenario,
                typeof(LifecycleTeardownTests),
                nameof(StopAsync_PublishesCompletionBeforeCancellationCallbacks)));
    }

    [Fact]
    public async Task StopAsync_BeforeConfigInitializationDoesNotCreateServer()
    {
        const string scenario = "server-stop-before-config";
        if (TerminalUiLifecycleChildProcess.IsChild(scenario))
        {
            await Server.StopAsync();
            Assert.False(Server.IsRunning);
            TerminalUiLifecycleChildProcess.WriteResult("ok");
            return;
        }

        Assert.Equal(
            "ok",
            await TerminalUiLifecycleProcessTests.RunChildAsync(
                scenario,
                typeof(LifecycleTeardownTests),
                nameof(StopAsync_BeforeConfigInitializationDoesNotCreateServer)));
    }

    [Fact]
    public async Task ShutdownCoreAsync_WaitsForTrackedHandlerAndReleasesPort()
    {
        var port = GetFreePort();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCompleted = false;
        var requests = new ServerRequestBarrier(TestContext.Current.CancellationToken);
        var server = new WebserverLite(
            new WebserverSettings("127.0.0.1", port),
            ctx => ctx.Response.Send(string.Empty));
        server.Routes.PreAuthentication.Static.Add(
            WatsonWebserver.Core.HttpMethod.GET,
            "/hold",
            requests.Wrap(async (ctx, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                handlerCompleted = true;
                await ctx.Response.Send("done");
            }));

        Task? request = null;
        Task? shutdown = null;
        try
        {
            server.Start(TestContext.Current.CancellationToken);
            using var client = new HttpClient();
            request = client.GetAsync(
                $"http://127.0.0.1:{port}/hold",
                TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            shutdown = Server.ShutdownCoreAsync(server, requests);

            Assert.False(server.IsListening);
            Assert.False(shutdown.IsCompleted);
            release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await request.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (HttpRequestException)
            {
            }

            Assert.True(handlerCompleted);
            using var rebound = new TcpListener(IPAddress.Loopback, port);
            rebound.Start();
        }
        finally
        {
            release.TrySetResult();
            if (shutdown is null)
                shutdown = Server.ShutdownCoreAsync(server, requests);
            try
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            if (request is not null)
            {
                try
                {
                    await request;
                }
                catch
                {
                }
            }
        }
    }

    static async Task RunReentrantServerStopAsync()
    {
        var port = GetFreePort();
        var config = Config.Serialize(new YamlConfig()).Replace(
            $"custom-database-repository: {Environment.NewLine}",
            $"custom-database-repository: \"\"{Environment.NewLine}",
            StringComparison.Ordinal);
        File.WriteAllText(Config.CONFIG_FILEPATH, config);
        Config.Initialize();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseHandler = new ManualResetEventSlim();
        var originalConfirmInstall = WebInstallApi.ConfirmInstall;
        var originalHttpClient = ResourceUpdater.HttpClient;
        ResourceUpdater.HttpClient = new HttpClient(new PluginInfoHandler());
        Task? request = null;
        Task? shutdown = null;
        Task? inlineShutdown = null;
        Task? crossThreadShutdown = null;
        var callbackCount = 0;
        var crossThreadReturned = false;

        try
        {
            WebInstallApi.ConfirmInstall = (_, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(() =>
                {
                    Interlocked.Increment(ref callbackCount);
                    inlineShutdown = Server.StopAsync();
                    var thread = new Thread(() => crossThreadShutdown = Server.StopAsync())
                    {
                        IsBackground = true
                    };
                    thread.Start();
                    crossThreadReturned = thread.Join(TimeSpan.FromSeconds(5));
                    releaseHandler.Set();
                });

                callbackEntered.TrySetResult();
                if (!releaseHandler.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Server shutdown did not cancel the active request.");
                return false;
            };

            Server.Instance = new(
                new WebserverSettings("127.0.0.1", port),
                ctx => ctx.Response.Send(string.Empty));
            Server.Start(CancellationToken.None);

            using var client = new HttpClient();
            using var message = new HttpRequestMessage(
                System.Net.Http.HttpMethod.Post,
                $"http://127.0.0.1:{port}/uracloud/install");
            message.Headers.Add("Origin", "https://ura.shuise.net");
            message.Content = new StringContent(
                "{\"repositoryId\":1,\"releaseId\":10}",
                Encoding.UTF8,
                "application/json");
            request = client.SendAsync(message, TestContext.Current.CancellationToken);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            shutdown = Server.StopAsync();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(crossThreadReturned);
            Assert.Same(shutdown, inlineShutdown);
            Assert.Same(shutdown, crossThreadShutdown);
            Assert.Same(shutdown, Server.StopAsync());
            Assert.Equal(1, Volatile.Read(ref callbackCount));
            Assert.False(Server.Instance.IsListening);

            using var rebound = new TcpListener(IPAddress.Loopback, port);
            rebound.Start();
        }
        finally
        {
            releaseHandler.Set();
            WebInstallApi.ConfirmInstall = originalConfirmInstall;
            ResourceUpdater.HttpClient.Dispose();
            ResourceUpdater.HttpClient = originalHttpClient;
            shutdown ??= Server.StopAsync();
            try
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

            if (request is not null)
            {
                try
                {
                    await request;
                }
                catch
                {
                }
            }
        }
    }

    sealed class PluginInfoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var plugin = new
            {
                source = new { repositoryId = 1 },
                releaseId = 10,
                manifest = new PluginInformation { Author = "test", InternalName = "test", DisplayName = "test", RawVersion = "1.0.0" }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonConvert.SerializeObject(plugin)) });
        }
    }
    static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
