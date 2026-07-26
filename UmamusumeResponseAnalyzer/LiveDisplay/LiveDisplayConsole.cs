using Terminal.Gui.App;

namespace UmamusumeResponseAnalyzer.LiveDisplay;

public static class LiveDisplayConsole
{
    static IApplication? application;
    static UiHost? uiHost;
    static CancellationToken lifetimeCancellationToken;

    internal static LiveDisplayWorkspace? DefaultLogWorkspace { get; set; }
    internal static IApplication Application
        => application ?? throw new InvalidOperationException("Terminal.Gui application 尚未绑定。");

    internal static void Bind(
        UiHost host,
        IApplication app,
        CancellationToken cancellationToken = default)
    {
        uiHost = host;
        application = app;
        lifetimeCancellationToken = cancellationToken;
        TerminalGuiDialogs.BindOwner(
            app,
            SynchronizationContext.Current
                ?? throw new InvalidOperationException("Terminal.Gui owner SynchronizationContext 不存在。"));
    }

    internal static void Unbind(UiHost host)
    {
        if (!ReferenceEquals(uiHost, host))
            return;

        var app = application;
        uiHost = null;
        application = null;
        lifetimeCancellationToken = default;
        DefaultLogWorkspace = null;
        if (app is not null)
            TerminalGuiDialogs.UnbindOwner(app);
    }

    public static T Select<T>(
        string title,
        IEnumerable<T> choices,
        Func<T, string>? converter = null,
        CancellationToken cancellationToken = default)
        => WithCancellation(
            cancellationToken,
            token => TerminalGuiDialogs.Select(Application, title, choices, converter, token));

    internal static T Menu<T>(
        string title,
        IEnumerable<T> choices,
        Func<T, string>? converter = null,
        CancellationToken cancellationToken = default)
        => WithCancellation(
            cancellationToken,
            token => TerminalGuiDialogs.Menu(Application, title, choices, converter, token));

    public static IReadOnlyList<T> MultiSelect<T>(
        string title,
        IEnumerable<T> choices,
        IEnumerable<T>? selected = null,
        Func<T, string>? converter = null,
        CancellationToken cancellationToken = default)
        => WithCancellation(
            cancellationToken,
            token => TerminalGuiDialogs.MultiSelect(
                Application,
                title,
                choices,
                selected,
                converter,
                token));

    public static string Ask(
        string title,
        string? value = null,
        bool allowEmpty = false,
        CancellationToken cancellationToken = default)
        => WithCancellation(
            cancellationToken,
            token => TerminalGuiDialogs.Ask(Application, title, value, allowEmpty, token));

    public static bool Confirm(
        string title,
        bool defaultValue = false,
        CancellationToken cancellationToken = default)
        => WithCancellation(
            cancellationToken,
            token => TerminalGuiDialogs.Confirm(Application, title, defaultValue, token));

    public static bool Acknowledge(
        string title = "按 Enter 返回",
        CancellationToken cancellationToken = default)
        => WithCancellation(
            cancellationToken,
            token => TerminalGuiDialogs.Acknowledge(Application, title, token));

    internal static async Task RunProgressAsync(
        Func<IProgress<DownloadProgress>, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        await WithCancellationAsync(
            cancellationToken,
            token => TerminalGuiDialogs.RunProgressAsync(Application, action, token));
    }

    internal static CancellationToken LifetimeCancellationToken => lifetimeCancellationToken;

    static T WithCancellation<T>(
        CancellationToken cancellationToken,
        Func<CancellationToken, T> action)
    {
        if (!lifetimeCancellationToken.CanBeCanceled)
            return action(cancellationToken);
        if (!cancellationToken.CanBeCanceled)
            return action(lifetimeCancellationToken);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellationToken,
            cancellationToken);
        return action(linkedCts.Token);
    }

    static async Task WithCancellationAsync(
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> action)
    {
        if (!lifetimeCancellationToken.CanBeCanceled)
        {
            await action(cancellationToken);
            return;
        }
        if (!cancellationToken.CanBeCanceled)
        {
            await action(lifetimeCancellationToken);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeCancellationToken,
            cancellationToken);
        await action(linkedCts.Token);
    }

    public static void WriteLine(string text) => Log("URA", text);

    public static void WriteLine() => WriteLine(string.Empty);

    public static void WriteLine(string format, params object[] args)
        => WriteLine(string.Format(format, args));

    public static void WriteException(Exception ex)
    {
        if (uiHost is null)
        {
            Console.Error.WriteLine(ex);
            return;
        }

        LogException("URA", ex);
    }

    internal static void LogException(
        string source,
        Exception ex,
        LiveDisplaySeverity severity = LiveDisplaySeverity.Error)
    {
        var host = uiHost;
        if (host is null)
        {
            Console.Error.WriteLine(ex);
            return;
        }

        host.Log(new LiveDisplayLogLine(
            DefaultLogWorkspace,
            source,
            FormatExceptionLogMessage(ex),
            severity));
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
        LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
    {
        var host = uiHost;
        if (host is null)
        {
            Console.WriteLine($"[{source}] {text}");
            return;
        }

        host.Log(new LiveDisplayLogLine(null, source, text, severity));
    }

    public static void Notify(
        string source,
        string text,
        LiveDisplaySeverity severity = LiveDisplaySeverity.Info,
        TimeSpan? ttl = null)
    {
        var host = uiHost;
        if (host is null)
        {
            Console.WriteLine($"[{source}] {text}");
            return;
        }

        host.Notify(new LiveDisplayNotification(
            null,
            source,
            text,
            severity,
            LiveDisplayNotification.ExpiresAtFromNow(severity, ttl),
            []));
    }

    internal static void UnbindForTests()
    {
        var app = application;
        uiHost = null;
        application = null;
        lifetimeCancellationToken = default;
        DefaultLogWorkspace = null;
        if (app is not null)
            TerminalGuiDialogs.UnbindOwner(app);
    }
}
