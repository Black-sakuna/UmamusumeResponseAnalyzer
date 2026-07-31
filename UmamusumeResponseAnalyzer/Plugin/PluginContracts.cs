using Gallop.Endpoints;
using Terminal.Gui.App;
using HttpMethod = WatsonWebserver.Core.HttpMethod;

namespace UmamusumeResponseAnalyzer.Plugin;

public interface IPluginContext
{
    IApplication Application { get; }
    IPluginHostEvents Events { get; }
    IPluginAnalyzerRegistry Analyzers { get; }
}

public interface IPluginHostEvents
{
    IDisposable OnStarted(Func<CancellationToken, ValueTask> handler);

    IDisposable OnStarted(Func<Task> handler)
        => OnStarted(_ => new ValueTask(handler()));
}

public interface IPlugin
{
    string Name { get; }
    string Author { get; }
    Version Version => GetType().Assembly.GetName().Version ?? new(0, 0, 0);
    string[] Targets { get; }

    void Initialize(IPluginContext context);

    void Dispose() { }

    Task ConfigPromptAsync(IApplication application, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

}

public enum AnalyzerKind
{
    Request,
    Response,
}

public sealed record GameHttpHeaders(
    string? Sid,
    string? AppVer,
    string? ResVer,
    string? ViewerId,
    string? Device,
    string? DeviceSubtype)
{
    public static GameHttpHeaders Empty { get; } = new(null, null, null, null, null, null);
}

public interface IPluginAnalyzerRegistry
{
    IDisposable RegisterRequest<TEndpoint>(
        Func<byte[], ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint;

    IDisposable RegisterRequest<TEndpoint>(
        Func<byte[], GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => throw new NotSupportedException("当前 analyzer registry 不支持带 GameHttpHeaders 的 raw request handler。");

    IDisposable RegisterResponse<TEndpoint>(
        Func<byte[], ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint;

    IDisposable RegisterResponse<TEndpoint>(
        Func<byte[], GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => throw new NotSupportedException("当前 analyzer registry 不支持带 GameHttpHeaders 的 raw response handler。");

    IDisposable RegisterRequest<TEndpoint, TRequest>(
        Func<TRequest, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint;

    IDisposable RegisterRequest<TEndpoint, TRequest>(
        Func<TRequest, GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => throw new NotSupportedException("当前 analyzer registry 不支持带 GameHttpHeaders 的 DTO request handler。");

    IDisposable RegisterResponse<TEndpoint, TResponse>(
        Func<TResponse, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint;

    IDisposable RegisterResponse<TEndpoint, TResponse>(
        Func<TResponse, GameHttpHeaders, ValueTask> handler,
        int priority = 0)
        where TEndpoint : IGameEndpoint
        => throw new NotSupportedException("当前 analyzer registry 不支持带 GameHttpHeaders 的 DTO response handler。");
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public abstract class AnalyzerAttribute : Attribute
{
    protected AnalyzerAttribute(Type endpointType, AnalyzerKind kind, int priority = 0)
    {
        EndpointType = endpointType;
        Kind = kind;
        Priority = priority;
    }

    public Type EndpointType { get; }
    public AnalyzerKind Kind { get; }
    public int Priority { get; }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class RequestAnalyzerAttribute<TEndpoint>(int priority = 0)
    : AnalyzerAttribute(typeof(TEndpoint), AnalyzerKind.Request, priority)
    where TEndpoint : IGameEndpoint;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ResponseAnalyzerAttribute<TEndpoint>(int priority = 0)
    : AnalyzerAttribute(typeof(TEndpoint), AnalyzerKind.Response, priority)
    where TEndpoint : IGameEndpoint;

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RouteAttribute(HttpMethod method, string path) : Attribute
{
    public HttpMethod Method { get; } = method;
    public string Path { get; } = path;
}

[AttributeUsage(AttributeTargets.Assembly, Inherited = false)]
public sealed class LoadInHostContextAttribute : Attribute;

[AttributeUsage(AttributeTargets.Assembly, Inherited = false, AllowMultiple = true)]
public sealed class SharedContextWithAttribute(params string[] pluginNames) : Attribute
{
    public string[] PluginNames { get; } = pluginNames;
}
