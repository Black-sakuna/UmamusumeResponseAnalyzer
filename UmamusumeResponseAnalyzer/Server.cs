using MessagePack;
using Gallop.Endpoints;
using Newtonsoft.Json.Linq;
using System.Reflection;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;
using static UmamusumeResponseAnalyzer.Localization.Server;

namespace UmamusumeResponseAnalyzer
{
    internal sealed class ServerRequestBarrier(CancellationToken hostCancellationToken) : IDisposable
    {
        readonly object gate = new();
        readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int inFlight;
        bool stopping;

        internal CancellationToken Token => lifetime.Token;

        internal Func<HttpContextBase, Task> Wrap(Func<HttpContextBase, CancellationToken, Task> handler) => ctx => InvokeAsync(ctx, handler);

        async Task InvokeAsync(HttpContextBase ctx, Func<HttpContextBase, CancellationToken, Task> handler)
        {
            var rejected = false;
            lock (gate)
            {
                if (stopping)
                {
                    rejected = true;
                    ctx.Response.StatusCode = 503;
                }
                else
                {
                    inFlight++;
                }
            }

            if (rejected)
            {
                await ctx.Response.Send("server_stopping");
                return;
            }

            try
            {
                await handler(ctx, lifetime.Token);
            }
            finally
            {
                lock (gate)
                {
                    if (--inFlight == 0 && stopping)
                        drained.TrySetResult();
                }
            }
        }

        internal Task StopAsync()
        {
            lock (gate)
            {
                if (stopping)
                    return drained.Task;

                stopping = true;
                if (inFlight == 0)
                    drained.TrySetResult();
            }

            lifetime.Cancel();
            return drained.Task;
        }

        public void Dispose() => lifetime.Dispose();
    }

    internal sealed class AnalyzerDispatchContext(
        AnalyzerKind kind,
        GameEndpointDescriptor descriptor,
        byte[] payload,
        GameHttpHeaders headers)
    {
        object? dto;

        public byte[] Payload { get; } = payload;
        public GameHttpHeaders Headers { get; } = headers;

        public object GetDto() => dto ??= DeserializeDto();

        object DeserializeDto()
        {
            var dtoType = kind == AnalyzerKind.Request ? descriptor.RequestType : descriptor.ResponseType;
            try
            {
                return MessagePackSerializer.Deserialize(dtoType, Payload)
                    ?? throw new InvalidOperationException(
                        $"Gallop DTO 反序列化返回 null: endpoint={descriptor.EndpointType.FullName}, path={descriptor.Path}");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Gallop DTO 反序列化失败: endpoint={descriptor.EndpointType.FullName}, path={descriptor.Path}, dto={dtoType.FullName}",
                    ex);
            }
        }
    }

    internal static class Server
    {
        const string GameEndpointPathPrefix = "/umamusume";
        const string CanonicalUrlHeaderName = "X-Hachimi-Game-Url";
        static readonly object DebugPacketCleanupLock = new();
        static readonly object LifecycleLock = new();
        static ServerRequestBarrier? requests;
        static Task? shutdownTask;
        internal static WebserverLite Instance = new(new WebserverSettings(Config.Core.ListenAddress, Config.Core.ListenPort), ctx => ctx.Response.Send(string.Empty));
        internal static bool IsRunning => Instance.IsListening;
        internal static void Start(CancellationToken hostCancellationToken)
        {
            lock (LifecycleLock)
            {
                if (requests is not null || shutdownTask is not null)
                    throw new InvalidOperationException("HTTP server lifecycle 已启动，不能重复 Start。");
                requests = new(hostCancellationToken);
            }

            Instance.Routes.PreAuthentication.Static.Add(
                WatsonWebserver.Core.HttpMethod.POST,
                "/notify/response",
                requests.Wrap((ctx, cancellationToken) => HandleNotificationAsync(AnalyzerKind.Response, ctx, cancellationToken)));
            Instance.Routes.PreAuthentication.Static.Add(
                WatsonWebserver.Core.HttpMethod.POST,
                "/notify/request",
                requests.Wrap((ctx, cancellationToken) => HandleNotificationAsync(AnalyzerKind.Request, ctx, cancellationToken)));
            Instance.Routes.PreAuthentication.Static.Add(
                WatsonWebserver.Core.HttpMethod.GET,
                "/notify/ping",
                requests.Wrap((ctx, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TerminalUi.Log("Server", I18N_PingReceived, UiSeverity.Trace);
                    return ctx.Response.Send("pong");
                }));
            WebInstallApi.Register(Instance, requests);
            Instance.Start(requests.Token);
        }

        internal static Task StopAsync()
        {
            TaskCompletionSource completion;
            WebserverLite server;
            ServerRequestBarrier? requestBarrier;
            lock (LifecycleLock)
            {
                if (shutdownTask is not null)
                    return shutdownTask;

                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                shutdownTask = completion.Task;
                server = Instance;
                requestBarrier = requests;
            }

            _ = CompleteShutdownAsync(completion, server, requestBarrier);
            return completion.Task;
        }

        static async Task CompleteShutdownAsync(
            TaskCompletionSource completion,
            WebserverLite server,
            ServerRequestBarrier? requestBarrier)
        {
            try
            {
                await ShutdownCoreAsync(server, requestBarrier);
                completion.SetResult();
            }
            catch (OperationCanceledException ex)
            {
                completion.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }

        internal static Task ShutdownCoreAsync(WebserverLite server, ServerRequestBarrier? requestBarrier)
        {
            var drained = Task.CompletedTask;
            return UmamusumeResponseAnalyzer.RunCleanupAsync(
                null,
                [
                    () =>
                    {
                        drained = requestBarrier?.StopAsync() ?? Task.CompletedTask;
                        return ValueTask.CompletedTask;
                    },
                    () =>
                    {
                        if (server.IsListening)
                            server.Stop();
                        return ValueTask.CompletedTask;
                    },
                    () => new ValueTask(drained),
                    () =>
                    {
                        server.Dispose();
                        return ValueTask.CompletedTask;
                    },
                    () =>
                    {
                        requestBarrier?.Dispose();
                        return ValueTask.CompletedTask;
                    },
                ],
                "HTTP server shutdown 失败。");
        }

        static async Task HandleNotificationAsync(
            AnalyzerKind kind,
            HttpContextBase ctx,
            CancellationToken cancellationToken)
        {
            var buffer = ctx.Request.DataAsBytes;
            var canonicalUrl = ctx.Request.Headers[CanonicalUrlHeaderName];
            if (string.IsNullOrWhiteSpace(canonicalUrl))
                throw new InvalidOperationException($"缺少 canonical URL header: {CanonicalUrlHeaderName}");

            var headers = new GameHttpHeaders(
                ctx.Request.Headers["X-Hachimi-sid"],
                ctx.Request.Headers["X-Hachimi-app-ver"],
                ctx.Request.Headers["X-Hachimi-res-ver"],
                ctx.Request.Headers["X-Hachimi-viewerid"],
                ctx.Request.Headers["X-Hachimi-device"],
                ctx.Request.Headers["X-Hachimi-device-subtype"]);
            await DispatchPacket(kind, canonicalUrl, buffer, headers);
            cancellationToken.ThrowIfCancellationRequested();
            await ctx.Response.Send(string.Empty);
        }

        internal static bool TryResolveEndpoint(string canonicalUrl, out GameEndpointDescriptor descriptor)
        {
            var path = ExtractEndpointPath(canonicalUrl);
            var prefixed = path.StartsWith(GameEndpointPathPrefix + "/", StringComparison.Ordinal);
            var firstPath = prefixed ? path : GameEndpointPathPrefix + path;
            var secondPath = prefixed ? path[GameEndpointPathPrefix.Length..] : path;
            return GameEndpointCatalog.ByPath.TryGetValue(firstPath, out descriptor!)
                || GameEndpointCatalog.ByPath.TryGetValue(secondPath, out descriptor!);
        }

        static string ExtractEndpointPath(string canonicalUrl)
        {
            var value = canonicalUrl.Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                value = uri.AbsolutePath;
            else
            {
                var queryStart = value.IndexOfAny(['?', '#']);
                if (queryStart >= 0)
                    value = value[..queryStart];
            }

            if (!value.StartsWith('/'))
                throw new FormatException($"canonical URL 必须包含绝对 path: {canonicalUrl}");

            return value;
        }

        internal static ValueTask DispatchRequest(string canonicalUrl, byte[] buffer, GameHttpHeaders? headers = null)
            => DispatchPacket(AnalyzerKind.Request, canonicalUrl, buffer, headers ?? GameHttpHeaders.Empty);

        internal static ValueTask DispatchResponse(string canonicalUrl, byte[] buffer, GameHttpHeaders? headers = null)
            => DispatchPacket(AnalyzerKind.Response, canonicalUrl, buffer, headers ?? GameHttpHeaders.Empty);

        static async ValueTask DispatchPacket(AnalyzerKind kind, string canonicalUrl, byte[] buffer, GameHttpHeaders headers)
        {
            try
            {
                if (!TryResolveEndpoint(canonicalUrl, out var descriptor))
                    return;

                SaveDebugPacket(kind, canonicalUrl, buffer);

                using var registrations = PluginManager.SnapshotAnalyzerRegistrations(kind, descriptor.EndpointType);
                if (registrations.Count == 0)
                    return;

                var context = new AnalyzerDispatchContext(kind, descriptor, buffer, headers);
                for (var i = 0; i < registrations.Count; i++)
                    await InvokeAnalyzer(kind, registrations[i], context);
            }
            catch (Exception e)
            {
                var label = kind == AnalyzerKind.Request ? "请求分析失败" : I18N_ResponseAnalyzeFail;
                TerminalUi.Notify("Server", $"{label}: {e.Message}", UiSeverity.Error);
                TerminalUi.LogException("Server", e);
                throw;
            }
        }

        static void SaveDebugPacket(AnalyzerKind kind, string canonicalUrl, byte[] buffer)
        {
            if (!Config.Misc.SaveResponseForDebug)
                return;

            Directory.CreateDirectory("packets");

            CleanupOldDebugPackets();

            var suffix = kind == AnalyzerKind.Request ? "Q" : "R";
            var timestamp = DateTime.Now.ToString("yy-MM-dd HH-mm-ss-fff");
            var endpointName = ExtractEndpointPath(canonicalUrl)[1..].Replace('/', '-');
            File.WriteAllBytes($"packets/{timestamp}-{Guid.CreateVersion7():N}{suffix}-{endpointName}.msgpack", buffer);
#if DEBUG
            var debugJson = new JObject
            {
                ["url"] = canonicalUrl,
                ["payload"] = JToken.Parse(MessagePackSerializer.ConvertToJson(buffer)),
            };
            File.WriteAllText($"packets/{timestamp}{suffix}.json", debugJson.ToString(Newtonsoft.Json.Formatting.None));
#endif
        }

        static void CleanupOldDebugPackets()
        {
            lock (DebugPacketCleanupLock)
            {
                foreach (var i in Directory.GetFiles("packets"))
                {
                    var fileInfo = new FileInfo(i);
                    if (fileInfo.CreationTime.AddDays(1) >= DateTime.Now)
                        continue;

                    try
                    {
                        fileInfo.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        TerminalUi.Log("Server", $"debug packet 旧文件清理失败，已跳过 {Path.GetFileName(i)}: {ex.Message}", UiSeverity.Warning);
                    }
                }
            }
        }

        static async ValueTask InvokeAnalyzer(AnalyzerKind kind, AnalyzerRegistration registration, AnalyzerDispatchContext context)
        {
            using var callback = PluginManager.EnterPluginCallbackScope();
            using var owner = HotkeyManager.RegisterScope(registration.Plugin);
            try
            {
                await registration.Handler(context);
            }
            catch (Exception e)
            {
                var root = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                var label = kind == AnalyzerKind.Request ? "请求" : "响应";
                var failure = new InvalidOperationException(
                    $"{label}分析插件处理失败: plugin={PluginManager.InternalName(registration.Plugin)}, " +
                    PluginManager.DescribeException(root));
                _ = PluginManager.ReportPluginFailure(
                    registration.Method?.DeclaringType?.Name ?? registration.Source,
                    failure);
            }
        }

    }

}
