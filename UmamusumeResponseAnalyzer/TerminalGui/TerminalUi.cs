using Terminal.Gui.App;

namespace UmamusumeResponseAnalyzer.TerminalGui;

public static class TerminalUi
{
    static readonly object initializationGate = new();
    static UiHost? uiHost;
    static Workspace? defaultExceptionWorkspace;

    internal static Workspace? DefaultExceptionWorkspace
    {
        get => Volatile.Read(ref defaultExceptionWorkspace);
        set => Volatile.Write(ref defaultExceptionWorkspace, value);
    }

    internal static IApplication Application => RequireHost().Application;

    internal static void Initialize(UiHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (initializationGate)
        {
            if (uiHost is not null)
                throw new InvalidOperationException("TerminalUi 已初始化；进程内不允许替换 UiHost。");

            host.EnsureAvailable();
            ModalDialogs.BindOwner(host.Application, host.OwnerContext);
            Volatile.Write(ref uiHost, host);
        }
    }

    internal static UiHost RequireHost()
    {
        var host = Volatile.Read(ref uiHost)
            ?? throw new InvalidOperationException("TerminalUi 尚未初始化。");
        host.EnsureAvailable();
        return host;
    }

    public static T Select<T>(
        string title,
        IEnumerable<T> choices,
        Func<T, string>? converter = null,
        CancellationToken cancellationToken = default)
    {
        var host = RequireHost();
        return WithCancellation(
            host.LifetimeToken,
            cancellationToken,
            token => ModalDialogs.Select(host.Application, title, choices, converter, token));
    }

    internal static T Menu<T>(
        string title,
        IEnumerable<T> choices,
        Func<T, string>? converter = null,
        CancellationToken cancellationToken = default)
    {
        var host = RequireHost();
        return WithCancellation(
            host.LifetimeToken,
            cancellationToken,
            token => ModalDialogs.Menu(host.Application, title, choices, converter, token));
    }

    public static IReadOnlyList<T> MultiSelect<T>(
        string title,
        IEnumerable<T> choices,
        IEnumerable<T>? selected = null,
        Func<T, string>? converter = null,
        CancellationToken cancellationToken = default)
    {
        var host = RequireHost();
        return WithCancellation(
            host.LifetimeToken,
            cancellationToken,
            token => ModalDialogs.MultiSelect(
                host.Application,
                title,
                choices,
                selected,
                converter,
                token));
    }

    public static string Ask(
        string title,
        string? value = null,
        bool allowEmpty = false,
        CancellationToken cancellationToken = default)
    {
        var host = RequireHost();
        return WithCancellation(
            host.LifetimeToken,
            cancellationToken,
            token => ModalDialogs.Ask(host.Application, title, value, allowEmpty, token));
    }

    public static bool Confirm(
        string title,
        bool defaultValue = false,
        CancellationToken cancellationToken = default)
    {
        var host = RequireHost();
        return WithCancellation(
            host.LifetimeToken,
            cancellationToken,
            token => ModalDialogs.Confirm(host.Application, title, defaultValue, token));
    }

    public static bool Acknowledge(
        string title = "按 Enter 返回",
        CancellationToken cancellationToken = default)
    {
        var host = RequireHost();
        return WithCancellation(
            host.LifetimeToken,
            cancellationToken,
            token => ModalDialogs.Acknowledge(host.Application, title, token));
    }

    internal static async Task RunProgressAsync(
        Func<IProgress<DownloadProgress>, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        var host = RequireHost();
        await WithCancellationAsync(
            host.LifetimeToken,
            cancellationToken,
            token => ModalDialogs.RunProgressAsync(host.Application, action, token));
    }

    static T WithCancellation<T>(
        CancellationToken lifetimeToken,
        CancellationToken cancellationToken,
        Func<CancellationToken, T> action)
    {
        if (!lifetimeToken.CanBeCanceled)
            return action(cancellationToken);
        if (!cancellationToken.CanBeCanceled)
            return action(lifetimeToken);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken,
            cancellationToken);
        return action(linkedCts.Token);
    }

    static async Task WithCancellationAsync(
        CancellationToken lifetimeToken,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action)
    {
        if (!lifetimeToken.CanBeCanceled)
        {
            await action(cancellationToken);
            return;
        }
        if (!cancellationToken.CanBeCanceled)
        {
            await action(lifetimeToken);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken,
            cancellationToken);
        await action(linkedCts.Token);
    }

    internal static void LogException(
        string source,
        Exception ex,
        UiSeverity severity = UiSeverity.Error)
    {
        ArgumentNullException.ThrowIfNull(ex);
        RequireHost().Log(
            DefaultExceptionWorkspace,
            $"[{source}] {FormatExceptionLogMessage(ex)}",
            severity,
            ex.ToString());
    }

    internal static string FormatExceptionLogMessage(Exception ex)
    {
        if (TryFormatPluginInitializationFailure(ex, out var pluginInitializationMessage))
            return pluginInitializationMessage;

        var messages = new List<string>();
        AppendMessages(ex, messages);
        return messages.Count == 0 ? ex.GetType().Name : string.Join(Environment.NewLine, messages);
    }

    static bool TryFormatPluginInitializationFailure(Exception ex, out string message)
    {
        const string prefix = "插件初始化失败: plugin=";

        var outerMessage = NormalizeExceptionMessage(ex.Message);
        if (!outerMessage.StartsWith(prefix, StringComparison.Ordinal))
        {
            message = string.Empty;
            return false;
        }

        var plugin = ParsePluginName(outerMessage[prefix.Length..]);
        var rootMessages = new List<string>();
        AppendMessages(ex.InnerException, rootMessages);
        if (plugin.Length == 0 || rootMessages.Count == 0)
        {
            message = string.Empty;
            return false;
        }

        message = $"{plugin}初始化失败：{rootMessages[0]}";
        if (rootMessages.Count > 1)
            message += Environment.NewLine + string.Join(Environment.NewLine, rootMessages.Skip(1));
        return true;
    }

    static void AppendMessages(Exception? exception, List<string> messages)
    {
        if (exception is null)
            return;

        if (exception is AggregateException { InnerExceptions.Count: > 0 } aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                AppendMessages(inner, messages);
            return;
        }

        var message = NormalizeExceptionMessage(exception.Message);
        if (message.Length > 0 && !messages.Contains(message, StringComparer.Ordinal))
            messages.Add(message);
        AppendMessages(exception.InnerException, messages);
    }

    static string ParsePluginName(string message)
    {
        var plugin = message;
        var displayNameMarker = plugin.IndexOf(" (", StringComparison.Ordinal);
        if (displayNameMarker >= 0)
            plugin = plugin[..displayNameMarker];
        var fieldMarker = plugin.IndexOf(',', StringComparison.Ordinal);
        if (fieldMarker >= 0)
            plugin = plugin[..fieldMarker];
        return plugin.Trim();
    }

    static string NormalizeExceptionMessage(string message)
        => RemoveConfigFileField(RemovePathField(message.Trim())).Trim();

    static string RemoveConfigFileField(string message)
    {
        foreach (var marker in new[] { "配置文件:", "配置文件：" })
        {
            var index = message.IndexOf(marker, StringComparison.Ordinal);
            if (index > 0)
                return message[..index];
        }
        return message;
    }

    static string RemovePathField(string message)
    {
        var marker = message.IndexOf(", path=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return message;

        var valueStart = marker + ", path=".Length;
        var nextField = new[] { ", phase=", ", type=" }
            .Select(x => message.IndexOf(x, valueStart, StringComparison.OrdinalIgnoreCase))
            .Where(x => x >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        return nextField < 0 ? message[..marker] : message[..marker] + message[nextField..];
    }

    public static void Log(
        string source,
        string text,
        UiSeverity severity = UiSeverity.Info)
        => RequireHost().Log(null, $"[{source}] {text}", severity);

    public static void Notify(
        string source,
        string text,
        UiSeverity severity = UiSeverity.Info,
        TimeSpan? ttl = null)
        => RequireHost().Notify(null, $"[{source}] {text}", severity, ttl, []);
}
