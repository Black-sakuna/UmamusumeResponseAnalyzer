using System.Runtime.ExceptionServices;

namespace UmamusumeResponseAnalyzer.LiveDisplay
{
    public static class LiveDisplayConsole
    {
        static readonly AsyncLocal<ConsoleInteractionContext?> consoleInteraction = new();
        static readonly SemaphoreSlim consoleInputGate = new(1, 1);
        static int standaloneInputWarningLogged;
        static UiHost? uiHost;

        internal static LiveDisplayWorkspace? DefaultLogWorkspace { get; set; }
        internal static SemaphoreSlim ConsoleInputGate => consoleInputGate;

        internal static void Bind(UiHost host) => uiHost = host;

        internal static void Unbind(UiHost host)
        {
            if (!ReferenceEquals(uiHost, host))
                return;

            uiHost = null;
            DefaultLogWorkspace = null;
        }

        public static void Run(Action action)
            => RunAsync(() =>
            {
                action();
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();

        public static T Run<T>(Func<T> action)
        {
            T result = default!;
            Run(() => result = action());
            return result;
        }

        public static Task RunAsync(Func<Task> action)
        {
            if (CurrentConsoleInteraction is not null)
                return action();

            var host = uiHost;
            if (host is null)
                return RunDirectConsoleInteractionAsync(action);

            return host.TryQueueConsoleInteraction(action, out var queued)
                ? queued
                : RunDirectConsoleInteractionAsync(action);
        }

        static async Task RunDirectConsoleInteractionAsync(Func<Task> action)
        {
            await consoleInputGate.WaitAsync();
            var gateOwned = true;
            try
            {
                var host = uiHost;
                if (host is not null && host.TryQueueConsoleInteraction(action, out var queued))
                {
                    consoleInputGate.Release();
                    gateOwned = false;
                    await queued;
                    return;
                }

                gateOwned = false;
                await ExecuteConsoleInteractionWithGateAsync(
                    action,
                    configureInput: KeyboardManager.IsRunning,
                    CancellationToken.None,
                    releaseGate: true);
            }
            finally
            {
                if (gateOwned)
                    consoleInputGate.Release();
            }
        }

        static T RunInput<T>(Func<T> action)
        {
            T result = default!;
            RunInputAsync(() =>
            {
                result = action();
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            return result;
        }

        internal static async Task RunInputAsync(Func<Task> action)
        {
            var interaction = CurrentConsoleInteraction;
            if (interaction is { InputConfigured: true })
            {
                await action();
                return;
            }

            if (interaction is { OwnsGate: true })
            {
                await ExecuteConsoleInteractionWithGateAsync(
                    action,
                    configureInput: true,
                    CancellationToken.None,
                    releaseGate: false);
                return;
            }

            await consoleInputGate.WaitAsync();
            var gateOwned = true;
            try
            {
                var host = uiHost;
                if (host is not null && host.TryQueueConsoleInteraction(action, out var queued))
                {
                    consoleInputGate.Release();
                    gateOwned = false;
                    await queued;
                    return;
                }

                gateOwned = false;
                await ExecuteConsoleInteractionWithGateAsync(
                    action,
                    configureInput: true,
                    CancellationToken.None,
                    releaseGate: true);
            }
            finally
            {
                if (gateOwned)
                    consoleInputGate.Release();
            }
        }

        public static T Select<T>(string title, IEnumerable<T> choices, Func<T, string>? converter = null)
            => RunInput(() => TerminalGuiDialogs.Select(title, choices, converter));

        public static IReadOnlyList<T> MultiSelect<T>(
            string title,
            IEnumerable<T> choices,
            IEnumerable<T>? selected = null,
            Func<T, string>? converter = null)
            => RunInput(() => TerminalGuiDialogs.MultiSelect(title, choices, selected, converter));

        public static string Ask(string title, string? value = null, bool allowEmpty = false)
            => RunInput(() => TerminalGuiDialogs.Ask(title, value, allowEmpty));

        public static bool Confirm(string title, bool defaultValue = true)
            => RunInput(() => TerminalGuiDialogs.Confirm(title, defaultValue));

        internal static Task RunProgressAsync(Func<IProgress<DownloadProgress>, Task> action)
            => RunAsync(() => TerminalGuiDialogs.RunProgressAsync(action));

        public static void Clear() => Run(Console.Clear);

        public static void WriteLine(string text) => Run(() => Console.WriteLine(text));

        public static void WriteLine() => Run(Console.WriteLine);

        public static void WriteLine(string format, params object[] args)
            => Run(() => Console.WriteLine(format, args));

        public static ConsoleKeyInfo ReadKey(bool intercept = false)
            => RunInput(() => ReadKeyCore(intercept));

        static ConsoleKeyInfo ReadKeyCore(bool intercept)
        {
            var interaction = CurrentConsoleInteraction;
            if (interaction?.InputSession is null)
                return Console.ReadKey(intercept);

            while (true)
            {
                interaction.LifetimeToken.ThrowIfCancellationRequested();
                if (!interaction.InputSession.TryReadSuspendedKey(out var keyInfo))
                {
                    Thread.Sleep(10);
                    continue;
                }

                if (!intercept && keyInfo.KeyChar != '\0')
                    Console.Write(keyInfo.KeyChar);
                return keyInfo;
            }
        }

        public static string ReadLine() => RunInput(ReadLineCore);

        static string ReadLineCore()
        {
            if (CurrentConsoleInteraction?.InputSession is null)
                return Console.ReadLine() ?? string.Empty;

            var value = new System.Text.StringBuilder();
            while (true)
            {
                var keyInfo = ReadKeyCore(intercept: true);
                switch (keyInfo.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        return value.ToString();
                    case ConsoleKey.Backspace when value.Length > 0:
                        value.Length--;
                        Console.Write("\b \b");
                        break;
                    default:
                        if (!char.IsControl(keyInfo.KeyChar))
                        {
                            value.Append(keyInfo.KeyChar);
                            Console.Write(keyInfo.KeyChar);
                        }
                        break;
                }
            }
        }

        public static void WriteException(Exception ex) => Run(() => Console.Error.WriteLine(ex));

        internal static void LogException(
            string source,
            Exception ex,
            LiveDisplaySeverity severity = LiveDisplaySeverity.Error)
        {
            var host = uiHost;
            if (CurrentConsoleInteraction is not null || host is null)
            {
                WriteException(ex);
                return;
            }

            host.Log(new LiveDisplayLogLine(DefaultLogWorkspace, source, FormatExceptionLogMessage(ex), severity));
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

        public static void Log(string source, string text, LiveDisplaySeverity severity = LiveDisplaySeverity.Info)
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

        internal static async Task ExecuteConsoleInputInteractionAsync(
            Func<Task> action,
            CancellationToken cancellationToken)
        {
            await consoleInputGate.WaitAsync(cancellationToken);
            await ExecuteConsoleInteractionWithGateAsync(action, true, cancellationToken, true);
        }

        static async Task ExecuteConsoleInteractionWithGateAsync(
            Func<Task> action,
            bool configureInput,
            CancellationToken cancellationToken,
            bool releaseGate)
        {
            IDisposable? inputSuspension = null;
            IDisposable? interaction = null;
            ExceptionDispatchInfo? failure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (configureInput)
                    inputSuspension = KeyboardManager.SuspendInput();
                interaction = EnterConsoleInteraction(configureInput, cancellationToken, ownsGate: true);
                await action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }

            if (interaction is not null)
                CaptureCleanupFailure(interaction.Dispose, ref failure);
            if (inputSuspension is not null)
                CaptureCleanupFailure(inputSuspension.Dispose, ref failure);
            if (releaseGate)
                CaptureCleanupFailure(() =>
                {
                    _ = consoleInputGate.Release();
                }, ref failure);
            failure?.Throw();
        }

        static void CaptureCleanupFailure(Action cleanup, ref ExceptionDispatchInfo? failure)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                if (failure is null)
                    failure = ExceptionDispatchInfo.Capture(ex);
                else
                    LogSecondaryInputFailure("清理", ex);
            }
        }

        static IDisposable EnterConsoleInteraction(
            bool configureInput = false,
            CancellationToken cancellationToken = default,
            bool ownsGate = false)
        {
            var previous = CurrentConsoleInteraction;
            IConsoleInputSession? ownedInputSession = null;
            var inputSession = configureInput ? KeyboardManager.GetSuspendedConsoleInputSession() : null;
            if (configureInput && inputSession is null)
            {
                inputSession = TryCreateStandaloneInputSession();
                ownedInputSession = inputSession;
                if (inputSession is null)
                    KeyboardManager.ForceManagedInputForProcess();
            }

            var current = new ConsoleInteractionContext(
                configureInput || previous is { InputConfigured: true },
                ownsGate || previous is { OwnsGate: true },
                inputSession ?? previous?.InputSession,
                cancellationToken,
                KeyboardManager.GetInputCancellationToken(),
                previous?.LifetimeToken ?? CancellationToken.None);
            consoleInteraction.Value = current;
            return new ConsoleInteractionScope(current, previous, ownedInputSession);
        }

        static IConsoleInputSession? TryCreateStandaloneInputSession()
        {
            var factory = KeyboardManager.ConsoleInputSessionFactoryOverrideForTests;
            if (factory is null && KeyboardManager.IsManagedInputForced)
                return null;

            IConsoleInputSession? inputSession;
            try
            {
                if (factory is not null)
                    inputSession = factory();
                else if (!WindowsConsoleInputSession.TryCreate(out inputSession))
                    return null;
            }
            catch (WindowsConsoleInputException ex) when (ex.SecondaryNativeErrorCode is null)
            {
                LogStandaloneInputFallback(ex);
                return null;
            }

            if (inputSession is null)
                return null;

            try
            {
                inputSession.Suspend();
                return inputSession;
            }
            catch (WindowsConsoleInputException ex)
            {
                try
                {
                    inputSession.Dispose();
                }
                catch (WindowsConsoleInputException restoreFailure)
                {
                    throw new WindowsConsoleInputException(ex.Stage, ex.NativeErrorCode, restoreFailure.NativeErrorCode);
                }
                LogStandaloneInputFallback(ex);
                return null;
            }
        }

        static void LogStandaloneInputFallback(WindowsConsoleInputException exception)
        {
            if (Interlocked.Exchange(ref standaloneInputWarningLogged, 1) != 0)
                return;

            Console.Error.WriteLine(
                $"[Keyboard] Windows console input 初始化失败，已使用 managed keyboard：" +
                $"stage={exception.Stage}, error={exception.NativeErrorCode}, {exception.Message}");
        }

        internal static void UnbindForTests()
        {
            uiHost = null;
            DefaultLogWorkspace = null;
        }

        sealed class ConsoleInteractionScope(
            ConsoleInteractionContext current,
            ConsoleInteractionContext? previous,
            IConsoleInputSession? ownedInputSession) : IDisposable
        {
            int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                ExceptionDispatchInfo? failure = null;
                CaptureCleanupFailure(current.Deactivate, ref failure);
                consoleInteraction.Value = previous is { IsActive: true } ? previous : null;
                if (ownedInputSession is not null)
                    CaptureCleanupFailure(ownedInputSession.Dispose, ref failure);
                failure?.Throw();
            }
        }

        sealed class ConsoleInteractionContext(
            bool inputConfigured,
            bool ownsGate,
            IConsoleInputSession? inputSession,
            params CancellationToken[] cancellationTokens)
        {
            readonly CancellationTokenSource lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationTokens);
            int active = 1;

            internal bool IsActive => Volatile.Read(ref active) != 0;
            internal bool InputConfigured { get; } = inputConfigured;
            internal bool OwnsGate { get; } = ownsGate;
            internal IConsoleInputSession? InputSession { get; } = inputSession;
            internal CancellationToken LifetimeToken => lifetimeCts.Token;

            internal void Deactivate()
            {
                if (Interlocked.Exchange(ref active, 0) == 0)
                    return;
                try
                {
                    lifetimeCts.Cancel();
                }
                finally
                {
                    lifetimeCts.Dispose();
                }
            }
        }

        static ConsoleInteractionContext? CurrentConsoleInteraction
            => consoleInteraction.Value is { IsActive: true } interaction ? interaction : null;

        internal static void LogSecondaryInputFailure(string stage, Exception exception)
        {
            try
            {
                Console.Error.WriteLine(
                    $"[Keyboard] Console input {stage}发生 secondary failure：" +
                    $"{exception.GetType().Name}: {exception.Message}");
            }
            catch
            {
            }
        }
    }
}
