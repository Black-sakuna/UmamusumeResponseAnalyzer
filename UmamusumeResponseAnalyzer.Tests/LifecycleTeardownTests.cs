using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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

    static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
