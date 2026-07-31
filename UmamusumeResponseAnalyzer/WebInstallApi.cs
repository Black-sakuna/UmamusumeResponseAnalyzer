using Newtonsoft.Json;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;

namespace UmamusumeResponseAnalyzer
{
    /// <summary>
    /// URACloud 网页集成的 HTTP 面,挂在内置服务器 :4693 的 <c>/uracloud/*</c> 下:让 URACloud 前端
    /// 探测「本地是否在跑 URA」,并在用户点击时触发「安装某插件 + 热重载」。
    ///
    /// 这件事安全敏感(等于让网页驱动本地下载并加载执行插件代码),故单独收在这一个文件里便于审计。
    /// 三层主防线,缺一不可:
    ///   ① <b>CORS 白名单</b>:只放行 URACloud 前端来源(prod <c>https://ura.shuise.net</c> / dev
    ///      <c>http://localhost:5173</c>)。其余来源拿不到任何放行头,浏览器拦掉跨源请求。
    ///   ② <b>结构性</b>:端点只收 {author, internalName, version} 三段引用,<b>绝不接受 URL 或代码</b>;
    ///      真正的 zip 由 <see cref="PluginRepository.InstallByReferenceAsync"/> 从【硬编码的】URACloud
    ///      仓库经标准 TLS 下载。所以伪造的网页改不了下载源、投不了毒,顶多触发安装一个仓库里真实存在的插件。
    ///   ③ <b>本机确认</b>:下载/加载前必须在 URA 控制台确认,服务端不把 Origin 当鉴权。
    ///
    /// (进一步的 SSL/SPKI-pinning 见 PluginRepository:因 <see cref="ResourceUpdater.HttpClient"/> 为
    /// GitHub/数据/插件共用,pinning 需要独立的下载 client + 处理 acme.sh 证书轮换,作为后续硬化项。)
    /// </summary>
    internal static class WebInstallApi
    {
        // 允许的前端来源。改这里即调整白名单。
        static readonly HashSet<string> AllowedOrigins = new(StringComparer.Ordinal)
        {
            "https://ura.shuise.net",
            "http://localhost:5173",
        };
        internal static Func<string, string, string, CancellationToken, bool> ConfirmInstall = ConfirmInstallCore;

        public static void Register(WebserverLite server, ServerRequestBarrier requests)
        {
            var routes = server.Routes.PreAuthentication.Static;
            routes.Add(WatsonWebserver.Core.HttpMethod.OPTIONS, "/uracloud/status", requests.Wrap(Preflight));
            routes.Add(WatsonWebserver.Core.HttpMethod.OPTIONS, "/uracloud/install", requests.Wrap(Preflight));
            routes.Add(WatsonWebserver.Core.HttpMethod.GET, "/uracloud/status", requests.Wrap(StatusAsync));
            routes.Add(WatsonWebserver.Core.HttpMethod.POST, "/uracloud/install", requests.Wrap(InstallAsync));
        }

        // 仅对白名单来源回放行头;未命中则不加任何 CORS 头,浏览器自然拦截。
        static void ApplyCors(HttpContextBase ctx)
        {
            var origin = ctx.Request.Headers["Origin"];
            if (origin != null && AllowedOrigins.Contains(origin))
            {
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", origin);
                ctx.Response.Headers.Add("Vary", "Origin");
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                // Chrome 的 Private Network Access:public(https 站点)→ private(localhost) 的请求需此头放行预检。
                ctx.Response.Headers.Add("Access-Control-Allow-Private-Network", "true");
            }
        }

        static async Task Preflight(HttpContextBase ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCors(ctx);
            ctx.Response.StatusCode = 204;
            await ctx.Response.Send(string.Empty);
        }

        // GET /uracloud/status —— 探测用:返回 URA 版本 + 当前已加载插件(供前端标注「已安装/可更新」)。
        static async Task StatusAsync(HttpContextBase ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCors(ctx);
            ctx.Response.ContentType = "application/json";
            var plugins = PluginManager.SnapshotLoadedPlugins().Select(p => new
            {
                author = p.Author,
                internalName = PluginManager.InternalName(p),
                version = p.Version.ToString(),
            });
            var body = JsonConvert.SerializeObject(new
            {
                app = "UmamusumeResponseAnalyzer",
                version = typeof(WebInstallApi).Assembly.GetName().Version?.ToString() ?? "unknown",
                plugins,
            });
            ctx.Response.StatusCode = 200;
            await ctx.Response.Send(body);
        }

        // POST /uracloud/install  body: {author, internalName, version} —— 下载并热重载。
        static async Task InstallAsync(HttpContextBase ctx, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCors(ctx);
            ctx.Response.ContentType = "application/json";

            // Origin 只用于 CORS/浏览器约束;真正授权靠下面的本机确认。
            var origin = ctx.Request.Headers["Origin"];
            if (origin == null || !AllowedOrigins.Contains(origin))
            {
                await SendJson(ctx, 403, new { ok = false, error = "origin_not_allowed" });
                return;
            }

            InstallRequest? req;
            try { req = JsonConvert.DeserializeObject<InstallRequest>(ctx.Request.DataAsString ?? string.Empty); }
            catch { req = null; }
            if (req is null || string.IsNullOrWhiteSpace(req.Author)
                || string.IsNullOrWhiteSpace(req.InternalName) || string.IsNullOrWhiteSpace(req.Version))
            {
                await SendJson(ctx, 400, new { ok = false, error = "bad_request" });
                return;
            }

            var installedFork = PluginManager.SnapshotLoadedPlugins()
                .FirstOrDefault(p => string.Equals(PluginManager.InternalName(p), req.InternalName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p.Author, req.Author, StringComparison.OrdinalIgnoreCase));
            if (installedFork != null)
            {
                await SendJson(ctx, 409, new { ok = false, error = "author_conflict", installedAuthor = installedFork.Author });
                return;
            }

            if (!ConfirmInstall(req.Author, req.InternalName, req.Version, cancellationToken))
            {
                await SendJson(ctx, 409, new { ok = false, error = "cancelled" });
                return;
            }

            try
            {
                await PluginRepository.InstallByReferenceAsync(req.Author, req.InternalName, req.Version, cancellationToken);
                // 核心安装路由由 Server barrier 跟踪；热重载会等待插件 callback 排空。
                var result = (await PluginManager.ReloadPluginsAsync(req.InternalName)).Single();
                var needsRestart = result.Outcome switch
                {
                    PluginManager.PluginLifecycleOutcome.Succeeded => false,
                    PluginManager.PluginLifecycleOutcome.RestartRequired => true,
                    PluginManager.PluginLifecycleOutcome.Failed => throw new InvalidOperationException(
                        $"插件 {req.InternalName} 已安装，但加载失败。"),
                    _ => throw new InvalidOperationException($"未知插件 lifecycle 结果: {result.Outcome}")
                };
                cancellationToken.ThrowIfCancellationRequested();
                TerminalUi.Log("URA", $"URACloud 网页请求已安装插件 {req.InternalName} v{req.Version}");
                await SendJson(ctx, 200, new { ok = true, installed = req.InternalName, needsRestart });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ArgumentException)
            {
                await SendJson(ctx, 400, new { ok = false, error = "invalid_identifier" });
            }
            catch (Exception ex)
            {
                TerminalUi.Log("URA", $"URACloud 网页安装失败: {ex.Message}");
                await SendJson(ctx, 500, new { ok = false, error = ex.Message });
            }
        }

        static Task SendJson(HttpContextBase ctx, int status, object payload)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            return ctx.Response.Send(JsonConvert.SerializeObject(payload));
        }

        static bool ConfirmInstallCore(
            string author,
            string internalName,
            string version,
            CancellationToken cancellationToken)
        {
            return TerminalUi.Confirm(
                $"URACloud 请求安装插件 {author}/{internalName} v{version}，是否允许？",
                cancellationToken: cancellationToken);
        }

        sealed class InstallRequest
        {
            public string Author { get; set; } = string.Empty;
            public string InternalName { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
        }
    }
}
