using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UmamusumeResponseAnalyzer.Plugin;
using WatsonWebserver.Core;
using WatsonWebserver.Lite;

namespace UmamusumeResponseAnalyzer;

/// <summary>Local URACloud integration: numeric references, allowed origins, and Host-side confirmation.</summary>
internal static class WebInstallApi
{
    static readonly HashSet<string> AllowedOrigins = new(StringComparer.Ordinal)
    {
        "https://ura.shuise.net",
        "http://localhost:5173",
    };
    static readonly JsonSerializerSettings JsonSettings = new() { ContractResolver = new CamelCasePropertyNamesContractResolver() };

    public static void Register(WebserverLite server, ServerRequestBarrier requests)
    {
        var routes = server.Routes.PreAuthentication.Static;
        routes.Add(WatsonWebserver.Core.HttpMethod.OPTIONS, "/uracloud/status", requests.Wrap(Preflight));
        routes.Add(WatsonWebserver.Core.HttpMethod.OPTIONS, "/uracloud/install", requests.Wrap(Preflight));
        routes.Add(WatsonWebserver.Core.HttpMethod.GET, "/uracloud/status", requests.Wrap(StatusAsync));
        routes.Add(WatsonWebserver.Core.HttpMethod.POST, "/uracloud/install", requests.Wrap(InstallAsync));
    }

    static void ApplyCors(HttpContextBase ctx)
    {
        var origin = ctx.Request.Headers["Origin"];
        if (origin != null && AllowedOrigins.Contains(origin))
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", origin);
            ctx.Response.Headers.Add("Vary", "Origin");
            ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
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

    static Task StatusAsync(HttpContextBase ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyCors(ctx);
        var plugins = PluginRepository.ReadInstalledPlugins().Select(p => new
        {
            author = p.Manifest?.Author,
            internalName = p.Manifest?.InternalName ?? Path.GetFileNameWithoutExtension(p.Path),
            version = p.Manifest?.RawVersion,
            loaded = p.Manifest is not null && PluginManager.FindLoadedPlugin(p.Manifest.InternalName) is not null,
            source = p.Source,
            error = p.Error,
        }).ToArray();
        return SendJson(ctx, 200, new
        {
            app = "UmamusumeResponseAnalyzer",
            version = typeof(WebInstallApi).Assembly.GetName().Version!.ToString(),
            plugins,
        });
    }

    static async Task InstallAsync(HttpContextBase ctx, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyCors(ctx);
        var origin = ctx.Request.Headers["Origin"];
        if (origin is null || !AllowedOrigins.Contains(origin))
        {
            await SendJson(ctx, 403, new { ok = false, error = "origin_not_allowed" });
            return;
        }
        InstallRequest? request;
        try { request = JsonConvert.DeserializeObject<InstallRequest>(ctx.Request.DataAsString ?? ""); }
        catch (JsonException) { request = null; }
        if (request is null || request.RepositoryId <= 0 || request.ReleaseId <= 0)
        {
            await SendJson(ctx, 400, new { ok = false, error = "invalid_repository_or_release_id" });
            return;
        }
        try
        {
            var result = await PluginRepository.InstallByReferenceAsync(request.RepositoryId, request.ReleaseId, cancellationToken);
            await SendJson(ctx, result.Ok ? 200 : 409, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { await SendJson(ctx, 500, new { ok = false, error = ex.Message }); }
    }

    static Task SendJson(HttpContextBase ctx, int status, object payload)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.Send(JsonConvert.SerializeObject(payload, JsonSettings));
    }

    sealed record InstallRequest(long RepositoryId, long ReleaseId);
}
