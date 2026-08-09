using Gallop.Endpoints;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using UmamusumeResponseAnalyzer.TerminalGui;
using WatsonWebserver.Core;

namespace UmamusumeResponseAnalyzer.Plugin;

internal sealed record AnalyzerRegistration(
    IPlugin Plugin,
    MethodInfo? Method,
    Type EndpointType,
    AnalyzerKind Kind,
    int Priority,
    Func<AnalyzerDispatchContext, ValueTask> Handler,
    string Source);

internal sealed class PluginScopedAnalyzerRegistry(IPlugin plugin) : IPluginAnalyzerRegistry
{
    public IDisposable RegisterRequest<TEndpoint>(
        Func<byte[], ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterRaw(AnalyzerKind.Request, typeof(TEndpoint), (payload, _) => handler(payload), priority);
    }

    public IDisposable RegisterRequest<TEndpoint>(
        Func<byte[], GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => RegisterRaw(AnalyzerKind.Request, typeof(TEndpoint), handler, priority);

    public IDisposable RegisterResponse<TEndpoint>(
        Func<byte[], ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterRaw(AnalyzerKind.Response, typeof(TEndpoint), (payload, _) => handler(payload), priority);
    }

    public IDisposable RegisterResponse<TEndpoint>(
        Func<byte[], GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => RegisterRaw(AnalyzerKind.Response, typeof(TEndpoint), handler, priority);

    public IDisposable RegisterRequest<TEndpoint, TRequest>(
        Func<TRequest, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterDto<TRequest>(AnalyzerKind.Request, typeof(TEndpoint), (payload, _) => handler(payload), priority);
    }

    public IDisposable RegisterRequest<TEndpoint, TRequest>(
        Func<TRequest, GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => RegisterDto(AnalyzerKind.Request, typeof(TEndpoint), handler, priority);

    public IDisposable RegisterResponse<TEndpoint, TResponse>(
        Func<TResponse, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterDto<TResponse>(AnalyzerKind.Response, typeof(TEndpoint), (payload, _) => handler(payload), priority);
    }

    public IDisposable RegisterResponse<TEndpoint, TResponse>(
        Func<TResponse, GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => RegisterDto(AnalyzerKind.Response, typeof(TEndpoint), handler, priority);

    IDisposable RegisterRaw(
        AnalyzerKind kind,
        Type endpointType,
        Func<byte[], GameHttpHeaders, ValueTask> handler,
        int priority)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return PluginManager.RegisterProgrammaticAnalyzer(
            plugin,
            kind,
            endpointType,
            typeof(byte[]),
            priority,
            context => handler(context.Payload, context.Headers),
            "programmatic raw analyzer");
    }

    IDisposable RegisterDto<TPayload>(
        AnalyzerKind kind,
        Type endpointType,
        Func<TPayload, GameHttpHeaders, ValueTask> handler,
        int priority)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (typeof(TPayload) == typeof(byte[]))
            throw new InvalidOperationException("DTO analyzer 不能使用 byte[]；raw analyzer 请使用单泛型 RegisterRequest/RegisterResponse overload。");

        return PluginManager.RegisterProgrammaticAnalyzer(
            plugin,
            kind,
            endpointType,
            typeof(TPayload),
            priority,
            context => handler((TPayload)context.GetDto(), context.Headers),
            "programmatic DTO analyzer");
    }
}

internal sealed class AnalyzerRegistrationHandle(AnalyzerRegistration registration) : IDisposable
{
    int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        PluginManager.RemoveAnalyzerRegistration(registration);
    }
}

internal sealed class RouteRegistration(
    IPlugin plugin,
    WatsonWebserver.Core.HttpMethod method,
    string path,
    Func<HttpContextBase, Task> handler)
{
    int removed;

    public IPlugin Plugin { get; } = plugin;
    public WatsonWebserver.Core.HttpMethod Method { get; } = method;
    public string Path { get; } = path;

    public async Task InvokeAsync(HttpContextBase ctx)
    {
        using var generation = PluginManager.EnterPluginCallback(Plugin);
        if (Volatile.Read(ref removed) != 0)
            throw new ObjectDisposedException(Path, $"插件路由已卸载: plugin={PluginManager.InternalName(Plugin)}, path={Path}");

        using var callback = PluginManager.EnterPluginCallbackScope();
        using var owner = HotkeyManager.RegisterScope(Plugin);
        try
        {
            await handler(ctx);
        }
        catch (Exception ex)
        {
            var failure = new InvalidOperationException(
                $"插件路由处理失败: plugin={PluginManager.InternalName(Plugin)}, path={Path}, " +
                PluginManager.DescribeException(ex));
            if (PluginManager.ReportPluginFailure("Plugin", failure) is { } diagnosticsError)
                throw new AggregateException("插件路由处理及 diagnostics 失败。", failure, diagnosticsError);
            throw failure;
        }
    }

    public void MarkRemoved()
        => Interlocked.Exchange(ref removed, 1);
}

internal sealed record PluginRegistrationPlan(
    List<AnalyzerRegistration> Analyzers,
    List<RouteRegistration> Routes);

internal static partial class PluginManager
{
    internal static void RegisterMethods(IPlugin plugin)
    {
        var plan = CreateRegistrationPlan(plugin);
        try
        {
            CommitRegistrationPlan(plan);
        }
        catch (Exception primary)
        {
            List<Exception> failures = [primary];
            try { RemoveAnalyzerMethods(plugin); }
            catch (Exception cleanupError) { failures.Add(cleanupError); }
            RemoveRoutes(plugin, failures);

            if (failures.Count != 1)
                throw new AggregateException("插件 registration 提交失败。", failures);
            ExceptionDispatchInfo.Capture(primary).Throw();
            throw;
        }
    }

    static PluginRegistrationPlan CreateRegistrationPlan(IPlugin plugin)
    {
        var analyzers = new List<AnalyzerRegistration>();
        var routes = new List<RouteRegistration>();
        foreach (var method in plugin.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (var analyzer in method.GetCustomAttributes<AnalyzerAttribute>())
                analyzers.Add(CreateAnalyzerRegistration(plugin, method, analyzer));

            var route = method.GetCustomAttribute<RouteAttribute>();
            if (route is not null)
                routes.Add(CreateRouteRegistration(plugin, method, route));
        }

        return new(analyzers, routes);
    }

    static RouteRegistration CreateRouteRegistration(IPlugin plugin, MethodInfo method, RouteAttribute route)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != 1 || parameters[0].ParameterType != typeof(HttpContextBase) || method.ReturnType != typeof(Task))
        {
            var actualParameters = parameters.Length == 0
                ? "<none>"
                : string.Join(", ", parameters.Select(x => x.ParameterType.FullName ?? x.ParameterType.Name));
            throw new InvalidOperationException(
                $"插件 Route 签名无效: plugin={plugin.Name} ({InternalName(plugin)}), " +
                $"method={method.DeclaringType?.FullName}.{method.Name}, path={route.Path}, " +
                $"expected=Task {nameof(HttpContextBase)}, actual={method.ReturnType.FullName} ({actualParameters})");
        }

        var handler = method.IsStatic
            ? method.CreateDelegate<Func<HttpContextBase, Task>>()
            : method.CreateDelegate<Func<HttpContextBase, Task>>(plugin);
        return new(plugin, route.Method, $"/{plugin.Name}/{route.Path}", handler);
    }

    static void CommitRegistrationPlan(PluginRegistrationPlan plan)
    {
        foreach (var registration in plan.Analyzers)
            CommitAnalyzerRegistration(registration);

        foreach (var route in plan.Routes)
        {
            Server.Instance.Routes.PreAuthentication.Static.Add(route.Method, route.Path, route.InvokeAsync);
            if (!PluginRoutes.TryGetValue(route.Plugin, out var routes))
            {
                routes = [];
                PluginRoutes[route.Plugin] = routes;
            }
            routes.Add(route);
        }
    }

    internal static IPluginAnalyzerRegistry AnalyzersFor(IPlugin plugin)
        => new PluginScopedAnalyzerRegistry(plugin);

    internal static IDisposable RegisterProgrammaticAnalyzer(
        IPlugin plugin,
        AnalyzerKind kind,
        Type endpointType,
        Type payloadType,
        int priority,
        Func<AnalyzerDispatchContext, ValueTask> handler,
        string source)
    {
        using var admission = EnterPluginRegistration(plugin);
        var registration = RegisterAnalyzerCore(plugin, kind, endpointType, payloadType, priority, handler, source, method: null);
        CommitAnalyzerRegistration(registration);
        return new AnalyzerRegistrationHandle(registration);
    }

    static AnalyzerRegistration CreateAnalyzerRegistration(IPlugin plugin, MethodInfo method, AnalyzerAttribute analyzer)
    {
        var (payloadType, hasHeaders) = ValidateAttributeAnalyzerSignature(plugin, method, analyzer);
        var source = $"{method.DeclaringType?.FullName}.{method.Name}";
        Func<AnalyzerDispatchContext, ValueTask> handler = payloadType == typeof(byte[])
            ? context => InvokeAttributeAnalyzer(plugin, method, context.Payload, hasHeaders ? context.Headers : null)
            : context => InvokeAttributeAnalyzer(plugin, method, context.GetDto(), hasHeaders ? context.Headers : null);

        return RegisterAnalyzerCore(
            plugin,
            analyzer.Kind,
            analyzer.EndpointType,
            payloadType,
            analyzer.Priority,
            handler,
            source,
            method,
            attribute: analyzer);
    }

    static (Type PayloadType, bool HasHeaders) ValidateAttributeAnalyzerSignature(IPlugin plugin, MethodInfo method, AnalyzerAttribute analyzer)
    {
        var parameters = method.GetParameters();
        if ((parameters.Length is not 1 and not 2) || method.ReturnType != typeof(ValueTask))
            throw AnalyzerRegistrationException(
                plugin,
                method,
                analyzer.EndpointType,
                analyzer.Kind,
                "<signature>",
                "ValueTask analyzer(TPayload payload) or ValueTask analyzer(TPayload payload, GameHttpHeaders headers)",
                DescribeAnalyzerSignature(method));

        if (parameters.Length == 2 && parameters[1].ParameterType != typeof(GameHttpHeaders))
            throw AnalyzerRegistrationException(
                plugin,
                method,
                analyzer.EndpointType,
                analyzer.Kind,
                "<signature>",
                "ValueTask analyzer(TPayload payload, GameHttpHeaders headers)",
                DescribeAnalyzerSignature(method));

        return (parameters[0].ParameterType, parameters.Length == 2);
    }

    static AnalyzerRegistration RegisterAnalyzerCore(
        IPlugin plugin,
        AnalyzerKind kind,
        Type endpointType,
        Type payloadType,
        int priority,
        Func<AnalyzerDispatchContext, ValueTask> handler,
        string source,
        MethodInfo? method,
        AnalyzerAttribute? attribute = null)
    {
        if (!GameEndpointCatalog.ByEndpointType.TryGetValue(endpointType, out var endpoint))
            throw AnalyzerRegistrationException(
                plugin,
                method,
                endpointType,
                kind,
                "<signature>",
                "catalog endpoint",
                $"未在 {nameof(GameEndpointCatalog)}.{nameof(GameEndpointCatalog.ByEndpointType)} 注册");

        var expected = payloadType == typeof(byte[])
            ? typeof(byte[])
            : kind == AnalyzerKind.Request
                ? endpoint.RequestType
                : endpoint.ResponseType;
        if (payloadType != expected)
            throw AnalyzerRegistrationException(
                plugin,
                method,
                endpointType,
                kind,
                payloadType == typeof(byte[]) ? "raw" : "DTO",
                expected.FullName ?? expected.Name,
                method is null
                    ? payloadType.FullName ?? payloadType.Name
                    : DescribeAnalyzerSignature(method));

        return new(
            plugin,
            method,
            endpointType,
            kind,
            priority,
            handler,
            attribute is null ? source : $"{source} [{attribute.GetType().Name}]");
    }

    static string DescribeAnalyzerSignature(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var parameterText = parameters.Length == 0
            ? "<none>"
            : string.Join(", ", parameters.Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));
        var asyncVoid = method.GetCustomAttribute<AsyncStateMachineAttribute>() is null ? string.Empty : ", async-state-machine";
        return $"return={method.ReturnType.FullName ?? method.ReturnType.Name}, parameters=({parameterText}){asyncVoid}";
    }

    static ValueTask InvokeAttributeAnalyzer(IPlugin plugin, MethodInfo method, object payload, GameHttpHeaders? headers)
    {
        var target = method.IsStatic ? null : plugin;
        object?[] args = headers is null ? [payload] : [payload, headers];
        return (ValueTask)method.Invoke(target, args)!;
    }

    static void CommitAnalyzerRegistration(AnalyzerRegistration registration)
    {
        lock (AnalyzerGate)
        {
            var dict = registration.Kind == AnalyzerKind.Response ? ResponseAnalyzerMethods : RequestAnalyzerMethods;
            if (!dict.TryGetValue(registration.Priority, out var list))
            {
                list = [];
                dict[registration.Priority] = list;
            }

            list.Add(registration);
        }
    }

    internal static void RemoveAnalyzerRegistration(AnalyzerRegistration registration)
    {
        lock (AnalyzerGate)
            RemoveAnalyzerRegistrationLocked(registration);
    }

    static void RemoveAnalyzerRegistrationLocked(AnalyzerRegistration registration)
    {
        var dict = registration.Kind == AnalyzerKind.Response ? ResponseAnalyzerMethods : RequestAnalyzerMethods;
        if (!dict.TryGetValue(registration.Priority, out var list))
            return;

        list.Remove(registration);
        if (list.Count == 0)
            dict.Remove(registration.Priority);
    }

    internal static PluginCallbackSnapshot<AnalyzerRegistration> SnapshotAnalyzerRegistrations(AnalyzerKind kind, Type endpointType)
    {
        lock (AnalyzerGate)
        {
            var dict = kind == AnalyzerKind.Request ? RequestAnalyzerMethods : ResponseAnalyzerMethods;
            return PluginCallbackSnapshot<AnalyzerRegistration>.Create(
                dict
                    .SelectMany(x => x.Value)
                    .Where(x => x.EndpointType == endpointType),
                static registration => registration.Plugin);
        }
    }

    static InvalidOperationException AnalyzerRegistrationException(
        IPlugin plugin,
        MethodInfo? method,
        Type endpointType,
        AnalyzerKind kind,
        string payload,
        string expected,
        string actual)
    {
        var methodName = method is null
            ? "<programmatic>"
            : $"{method.DeclaringType?.FullName}.{method.Name}";
        return new InvalidOperationException(
            $"插件 analyzer 签名无效: plugin={plugin.Name} ({InternalName(plugin)}), " +
            $"method={methodName}, endpoint={endpointType.FullName}, kind={kind}, payload={payload}, " +
            $"expected={expected}, actual={actual}");
    }

    static void RemoveAnalyzerMethods(IPlugin plugin)
    {
        lock (AnalyzerGate)
        {
            foreach (var dict in (SortedDictionary<int, List<AnalyzerRegistration>>[])[RequestAnalyzerMethods, ResponseAnalyzerMethods])
            {
                foreach (var priority in dict.Keys.ToList())
                {
                    var list = dict[priority];
                    list.RemoveAll(x => ReferenceEquals(x.Plugin, plugin));
                    if (list.Count == 0)
                        dict.Remove(priority);
                }
            }
        }
    }

    static void RemoveRoutes(IPlugin plugin, List<Exception> failures)
    {
        if (!PluginRoutes.Remove(plugin, out var routes))
            return;
        foreach (var route in routes)
            RemoveRoute(route, "RegistrationRollback", failures);
    }

    static void RemoveRoute(
        RouteRegistration route,
        string phase,
        List<Exception> failures)
    {
        try
        {
            route.MarkRemoved();
            if (Server.Instance.Routes.PreAuthentication.Static.Exists(route.Method, route.Path))
                Server.Instance.Routes.PreAuthentication.Static.Remove(route.Method, route.Path);
        }
        catch (Exception cleanupError)
        {
            failures.Add(new InvalidOperationException(
                $"插件 route 清理失败: plugin={InternalName(route.Plugin)}, phase={phase}, path={route.Path}",
                cleanupError));
        }
    }
}
