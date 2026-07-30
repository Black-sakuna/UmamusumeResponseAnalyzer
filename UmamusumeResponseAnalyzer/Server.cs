using MessagePack;
using Gallop.Endpoints;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
        TaskCompletionSource? drained;
        int inFlight;
        bool stopping;

        internal CancellationToken Token => lifetime.Token;

        internal Func<HttpContextBase, Task> Wrap(Func<HttpContextBase, CancellationToken, Task> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            return ctx => InvokeAsync(ctx, handler);
        }

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
                    if (--inFlight == 0)
                        drained?.TrySetResult();
                }
            }
        }

        internal Task StopAsync()
        {
            Task wait;
            var cancel = false;
            lock (gate)
            {
                if (!stopping)
                {
                    stopping = true;
                    cancel = true;
                }

                wait = inFlight == 0
                    ? Task.CompletedTask
                    : (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }

            if (cancel)
                lifetime.Cancel();
            return wait;
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
        bool dtoInitialized;

        public byte[] Payload { get; } = payload;
        public GameHttpHeaders Headers { get; } = headers;

        public object GetDto()
        {
            if (dtoInitialized)
                return dto!;

            dto = DeserializeDto();
            dtoInitialized = true;
            return dto;
        }

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

    public static class Server
    {
        const string GameEndpointPathPrefix = "/umamusume";
        internal const string CanonicalUrlHeaderName = "X-Hachimi-Game-Url";
        internal const string SidHeaderName = "X-Hachimi-sid";
        internal const string AppVerHeaderName = "X-Hachimi-app-ver";
        internal const string ResVerHeaderName = "X-Hachimi-res-ver";
        internal const string ViewerIdHeaderName = "X-Hachimi-viewerid";
        internal const string DeviceHeaderName = "X-Hachimi-device";
        internal const string DeviceSubtypeHeaderName = "X-Hachimi-device-subtype";
        static readonly object DebugPacketCleanupLock = new();
        static readonly object LifecycleLock = new();
        static ServerRequestBarrier? requests;
        static Task? shutdownTask;
        internal static WebserverLite Instance = new(new WebserverSettings(Config.Core.ListenAddress, Config.Core.ListenPort), (ctx) => { return ctx.Response.Send(string.Empty); });
        public static bool IsRunning => Instance.IsListening;
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
                requests.Wrap(async (ctx, cancellationToken) =>
                {
                    var buffer = ctx.Request.DataAsBytes;
                    var canonicalUrl = ReadCanonicalUrl(ctx);
                    var headers = ReadGameHttpHeaders(ctx);
                    await DispatchResponse(canonicalUrl, buffer, headers);
                    cancellationToken.ThrowIfCancellationRequested();
                    await ctx.Response.Send(string.Empty);
                }));
            Instance.Routes.PreAuthentication.Static.Add(
                WatsonWebserver.Core.HttpMethod.POST,
                "/notify/request",
                requests.Wrap(async (ctx, cancellationToken) =>
                {
                    var buffer = ctx.Request.DataAsBytes;
                    var canonicalUrl = ReadCanonicalUrl(ctx);
                    var headers = ReadGameHttpHeaders(ctx);
                    await DispatchRequest(canonicalUrl, buffer, headers);
                    cancellationToken.ThrowIfCancellationRequested();
                    await ctx.Response.Send(string.Empty);
                }));
            Instance.Routes.PreAuthentication.Static.Add(
                WatsonWebserver.Core.HttpMethod.GET,
                "/notify/ping",
                requests.Wrap(async (ctx, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TerminalUi.Log("Server", I18N_PingReceived, UiSeverity.Trace);
                    await ctx.Response.Send("pong");
                }));
            WebInstallApi.Register(Instance, requests);
            Instance.Start(requests.Token);
        }

        internal static Task StopAsync()
        {
            lock (LifecycleLock)
            {
                return shutdownTask ??= ShutdownCoreAsync(Instance, requests);
            }
        }

        internal static async Task ShutdownCoreAsync(WebserverLite server, ServerRequestBarrier? requestBarrier)
        {
            List<Exception>? failures = null;
            Task drained = Task.CompletedTask;

            if (requestBarrier is not null)
            {
                try
                {
                    drained = requestBarrier.StopAsync();
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            try
            {
                if (server.IsListening)
                    server.Stop();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                await drained;
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                server.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                requestBarrier?.Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (failures is [var failure])
                ExceptionDispatchInfo.Capture(failure).Throw();
            if (failures is { Count: > 1 })
                throw new AggregateException("HTTP server shutdown 失败。", failures);
        }
        internal static string ReadCanonicalUrl(HttpContextBase ctx)
        {
            var canonicalUrl = ctx.Request.Headers[CanonicalUrlHeaderName];
            if (string.IsNullOrWhiteSpace(canonicalUrl))
                throw new InvalidOperationException($"缺少 canonical URL header: {CanonicalUrlHeaderName}");
            return canonicalUrl;
        }

        internal static GameHttpHeaders ReadGameHttpHeaders(HttpContextBase ctx)
            => new(
                ctx.Request.Headers[SidHeaderName],
                ctx.Request.Headers[AppVerHeaderName],
                ctx.Request.Headers[ResVerHeaderName],
                ctx.Request.Headers[ViewerIdHeaderName],
                ctx.Request.Headers[DeviceHeaderName],
                ctx.Request.Headers[DeviceSubtypeHeaderName]);

        internal static bool TryResolveEndpoint(string canonicalUrl, out GameEndpointDescriptor descriptor)
        {
            var path = ExtractEndpointPath(canonicalUrl);
            var triedPaths = ResolveEndpointPathCandidates(path);
            foreach (var triedPath in triedPaths)
            {
                if (GameEndpointCatalog.ByPath.TryGetValue(triedPath, out descriptor!))
                    return true;
            }

            descriptor = null!;
            return false;
        }

        static string[] ResolveEndpointPathCandidates(string path)
            => path.StartsWith(GameEndpointPathPrefix + "/", StringComparison.Ordinal)
                ? [path, path[GameEndpointPathPrefix.Length..]]
                : [GameEndpointPathPrefix + path, path];

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

                using var callback = await PluginManager.EnterPluginCallbackAsync();
                await DispatchPacketLocked(kind, descriptor, buffer, headers);
            }
            catch (Exception e)
            {
                ReportDispatchError(kind, e);
                throw;
            }
        }

        static void SaveDebugPacket(AnalyzerKind kind, string canonicalUrl, byte[] buffer)
        {
            if (!Config.Misc.SaveResponseForDebug)
                return;

            if (!Directory.Exists("packets"))
                Directory.CreateDirectory("packets");

            CleanupOldDebugPackets();

            var suffix = kind == AnalyzerKind.Request ? "Q" : "R";
            var timestamp = DateTime.Now.ToString("yy-MM-dd HH-mm-ss-fff");
            var endpointName = FormatDebugPacketEndpoint(canonicalUrl);
            File.WriteAllBytes($"packets/{timestamp}{suffix}-{endpointName}.msgpack", buffer);
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
                    if (fileInfo.CreationTime.AddDays(1) < DateTime.Now)
                    {
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
        }

        static string FormatDebugPacketEndpoint(string canonicalUrl)
        {
            var endpointPath = ExtractEndpointPath(canonicalUrl);
            if (endpointPath.StartsWith('/'))
                endpointPath = endpointPath[1..];
            return endpointPath.Replace('/', '-');
        }

        static async ValueTask DispatchPacketLocked(AnalyzerKind kind, GameEndpointDescriptor descriptor, byte[] buffer, GameHttpHeaders headers)
        {
            var registrations = PluginManager.SnapshotAnalyzerRegistrations(kind, descriptor.EndpointType);
            if (registrations.Count == 0)
                return;

            var context = new AnalyzerDispatchContext(kind, descriptor, buffer, headers);
            foreach (var registration in registrations)
                await InvokeAnalyzer(kind, registration, context);
        }

        static async ValueTask InvokeAnalyzer(AnalyzerKind kind, AnalyzerRegistration registration, AnalyzerDispatchContext context)
        {
            try
            {
                using var callback = PluginManager.EnterPluginCallbackScope();
                await registration.Handler(context);
            }
            catch (Exception e)
            {
                var root = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                var label = kind == AnalyzerKind.Request ? "请求" : "响应";
                TerminalUi.Notify("Plugin", $"{label}分析插件处理失败: {root.Message}", UiSeverity.Error);
                TerminalUi.LogException(registration.Method?.DeclaringType?.Name ?? registration.Source, root);
            }
        }

        static void ReportDispatchError(AnalyzerKind kind, Exception ex)
        {
            var label = kind == AnalyzerKind.Request ? "请求分析失败" : I18N_ResponseAnalyzeFail;
            TerminalUi.Notify("Server", $"{label}: {ex.Message}", UiSeverity.Error);
            TerminalUi.LogException("Server", ex);
        }
    }

}
