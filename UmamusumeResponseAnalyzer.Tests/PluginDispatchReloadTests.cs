using System.Reflection;
using System.Runtime.CompilerServices;
using Terminal.Gui.Input;
using UmamusumeResponseAnalyzer.Plugin;
using UmamusumeResponseAnalyzer.TerminalGui;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("PluginReload")]
public sealed class PluginDispatchReloadTests : IDisposable
{
    const string PluginName = "DisposeBarrierPlugin";
    const string StartedPluginName = "StartedBarrierPlugin";

    readonly string originalCwd = Directory.GetCurrentDirectory();
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "ura-dispatch-reload-" + Guid.NewGuid().ToString("N"));
    readonly string disposeLog;
    readonly string disposeEntered;
    readonly string disposeRelease;
    readonly string callbackLog;
    readonly string statusGetterLog;
    readonly string statusReloadGuard;
    readonly string startedDisposeEntered;
    readonly string startedDisposeRelease;
    readonly string startedDisposeLog;
    readonly string startedCallbackLog;

    public PluginDispatchReloadTests(PluginRuntimeFixture runtime)
    {
        SeedConfig();
        ResetPluginState();
        HotkeyManager.UnregisterAll();
        HotkeyManager.OverlaySink = runtime.Host;

        Directory.CreateDirectory(Path.Combine(tempDir, "Plugins"));
        Directory.SetCurrentDirectory(tempDir);
        disposeLog = Path.Combine(tempDir, "dispose.log");
        disposeEntered = Path.Combine(tempDir, "dispose-entered");
        disposeRelease = Path.Combine(tempDir, "dispose-release");
        callbackLog = Path.Combine(tempDir, "callback.log");
        statusGetterLog = Path.Combine(tempDir, "status-getter.log");
        statusReloadGuard = Path.Combine(tempDir, "status-reload-guard.log");
        startedDisposeEntered = Path.Combine(tempDir, "started-dispose-entered");
        startedDisposeRelease = Path.Combine(tempDir, "started-dispose-release");
        startedDisposeLog = Path.Combine(tempDir, "started-dispose.log");
        startedCallbackLog = Path.Combine(tempDir, "started-callback.log");
    }

    public void Dispose()
    {
        ResetPluginState();
        HotkeyManager.UnregisterAll();
        Directory.SetCurrentDirectory(originalCwd);
        try { Directory.Delete(tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ReloadClosesAdmissionBeforeDisposeAndUnloadsContext()
    {
        var oldContext = await ReloadWhileDisposeIsBlockedAsync();

        for (var i = 0; oldContext.IsAlive && i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(oldContext.IsAlive, "reload 后旧 collectible ALC 未被回收");
    }

    [Fact]
    public async Task AnalyzerSnapshotLeasesLaterGenerationUntilBlockedCallbackCompletes()
    {
        var laterContext = await UnloadSnapshottedAnalyzerBehindBlockedCallbackAsync();

        for (var i = 0; laterContext.IsAlive && i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(laterContext.IsAlive, "unload 返回后 analyzer snapshot 仍钉住后续 plugin ALC");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    async Task<WeakReference> UnloadSnapshottedAnalyzerBehindBlockedCallbackAsync()
    {
        const string blockingName = "SnapshotBlockingAnalyzer";
        const string laterName = "SnapshotLaterAnalyzer";
        var entered = Path.Combine(tempDir, "snapshot-entered");
        var release = Path.Combine(tempDir, "snapshot-release");
        var lifecycle = Path.Combine(tempDir, "snapshot-lifecycle.log");
        PluginCompiler.Compile(
            SnapshotAnalyzerPluginSource(blockingName, lifecycle, priority: -10, entered, release),
            blockingName,
            Path.Combine(tempDir, "Plugins", $"{blockingName}.dll"));
        PluginCompiler.Compile(
            SnapshotAnalyzerPluginSource(laterName, lifecycle, priority: 10),
            laterName,
            Path.Combine(tempDir, "Plugins", $"{laterName}.dll"));
        PluginManager.Init();
        PluginManager.InitializeLoadedPlugins();
        var laterContext = new WeakReference(PluginManager.Contexts[laterName]);

        var dispatch = Task.Run(async () =>
            await Server.DispatchResponse("/umamusume/account/index", [0xC0]));
        await WaitUntilAsync(() => File.Exists(entered));
        var unload = Task.Run(() => PluginManager.UnloadPluginsAsync(laterName));
        Assert.False(unload.IsCompleted);
        Assert.Equal([$"{blockingName}:entered"], File.ReadAllLines(lifecycle));

        File.WriteAllText(release, "release");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        AssertLifecycleOutcome(
            await unload.WaitAsync(TimeSpan.FromSeconds(5)),
            laterName);
        Assert.Equal(
            [
                $"{blockingName}:entered",
                $"{blockingName}:released",
                $"{laterName}:callback",
                $"{laterName}:dispose",
            ],
            File.ReadAllLines(lifecycle));
        return laterContext;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    async Task<WeakReference> ReloadWhileDisposeIsBlockedAsync()
    {
        PluginCompiler.Compile(
            PluginSource(),
            PluginName,
            Path.Combine(tempDir, "Plugins", $"{PluginName}.dll"));
        PluginManager.Init();
        PluginManager.InitializeLoadedPlugins();

        var initialStatus = Assert.Single(
            PluginManager.SnapshotPluginStatuses(),
            status => status.InternalName == PluginName);
        Assert.True(initialStatus.IsLoaded);
        Assert.Contains("插件回调内禁止执行热重载", File.ReadAllText(statusReloadGuard), StringComparison.Ordinal);
        var statusGettersBeforeReload = File.ReadAllLines(statusGetterLog);

        var oldContext = new WeakReference(PluginManager.Contexts[PluginName]);
        await Server.DispatchResponse("/umamusume/account/index", [0xC0]);
        Assert.Equal(["entered"], File.ReadAllLines(callbackLog));

        var reload = Task.Run(async () =>
            await PluginManager.ReloadPluginsAsync(PluginName));
        var reloadWaited = false;
        Exception? observationError = null;
        Exception? lateDispatchError = null;
        string[] callbacksWhileDisposing = [];
        PluginRuntimeStatus? statusWhileDisposing = null;
        string[] statusGettersWhileDisposing = [];
        try
        {
            await WaitUntilAsync(() => File.Exists(disposeEntered));
            reloadWaited = !reload.IsCompleted;
            statusWhileDisposing = Assert.Single(
                PluginManager.SnapshotPluginStatuses(),
                status => status.InternalName == PluginName);
            statusGettersWhileDisposing = File.ReadAllLines(statusGetterLog);
            lateDispatchError = await Record.ExceptionAsync(async () =>
                await Server.DispatchResponse("/umamusume/account/index", [0xC0]).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            callbacksWhileDisposing = File.ReadAllLines(callbackLog);
        }
        catch (Exception ex)
        {
            observationError = ex;
        }
        finally
        {
            File.WriteAllText(disposeRelease, "release");
        }

        IReadOnlyList<PluginManager.PluginLifecycleResult>? reloadResults = null;
        var reloadError = await Record.ExceptionAsync(async () =>
        {
            reloadResults = await reload.WaitAsync(TimeSpan.FromSeconds(5));
        });

        Assert.Null(observationError);
        Assert.True(reloadWaited, "plugin Dispose 尚未返回时 reload 不应完成");
        Assert.NotNull(statusWhileDisposing);
        Assert.False(statusWhileDisposing.IsLoaded);
        Assert.Equal(statusGettersBeforeReload, statusGettersWhileDisposing);
        Assert.Null(lateDispatchError);
        Assert.Equal(["entered"], callbacksWhileDisposing);
        Assert.IsNotType<SynchronizationLockException>(reloadError);
        Assert.Null(reloadError);
        AssertLifecycleOutcome(reloadResults!, PluginName);
        Assert.Equal("disposed", File.ReadAllText(disposeLog));
        return oldContext;
    }

    [Fact]
    public async Task StartedEventIsRejectedWhileUnloadDisposeIsBlocked()
    {
        PluginCompiler.Compile(
            StartedPluginSource(),
            StartedPluginName,
            Path.Combine(tempDir, "Plugins", $"{StartedPluginName}.dll"));
        PluginManager.Init();
        PluginManager.InitializeLoadedPlugins();
        var plugin = Assert.Single(
            PluginManager.LoadedPlugins,
            plugin => PluginManager.InternalName(plugin) == StartedPluginName);

        var unload = Task.Run(async () => await PluginManager.UnloadPluginsAsync(StartedPluginName));
        Exception? observationError = null;
        Exception? lateStartedError = null;
        var unloadWaited = false;
        string[] callbackOutput = [];
        try
        {
            await WaitUntilAsync(() => File.Exists(startedDisposeEntered));
            unloadWaited = !unload.IsCompleted;
            lateStartedError = await Record.ExceptionAsync(async () =>
                await PluginManager.TriggerStartedForPluginsAsync([plugin])
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            callbackOutput = File.Exists(startedCallbackLog)
                ? File.ReadAllLines(startedCallbackLog)
                : [];
        }
        catch (Exception ex)
        {
            observationError = ex;
        }
        finally
        {
            File.WriteAllText(startedDisposeRelease, "release");
        }

        var unloadResults = await unload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(observationError);
        Assert.True(unloadWaited, "plugin Dispose 尚未返回时 unload 不应完成");
        Assert.Null(lateStartedError);
        Assert.Empty(callbackOutput);
        AssertLifecycleOutcome(unloadResults, StartedPluginName);
        Assert.Equal("disposed", File.ReadAllText(startedDisposeLog));
    }

    [Fact]
    public async Task BatchUnloadContinuesAfterPluginDisposeFailure()
    {
        const string failingName = "FailingDisposePlugin";
        const string healthyName = "HealthyDisposePlugin";
        var failingLog = Path.Combine(tempDir, "failing-dispose.log");
        var healthyLog = Path.Combine(tempDir, "healthy-dispose.log");
        PluginCompiler.Compile(
            DisposePluginSource(failingName, failingLog, fail: true),
            failingName,
            Path.Combine(tempDir, "Plugins", $"{failingName}.dll"));
        PluginCompiler.Compile(
            DisposePluginSource(healthyName, healthyLog, fail: false),
            healthyName,
            Path.Combine(tempDir, "Plugins", $"{healthyName}.dll"));
        PluginManager.Init();
        PluginManager.InitializeLoadedPlugins();

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => PluginManager.UnloadPluginsAsync(failingName, healthyName));

        Assert.Contains(error.InnerExceptions, ex => ex.Message.Contains(failingName, StringComparison.Ordinal));
        Assert.Equal(["disposed"], File.ReadAllLines(failingLog));
        Assert.Equal(["disposed"], File.ReadAllLines(healthyLog));
        Assert.DoesNotContain(PluginManager.SnapshotLoadedPlugins(), plugin =>
            PluginManager.InternalName(plugin) is failingName or healthyName);
    }

    [Fact]
    public async Task InitializeLoadedPluginsExcludesConcurrentUnloadAndDisposesOnce()
    {
        const string pluginName = "BlockingInitializePlugin";
        var initializeEntered = Path.Combine(tempDir, "initialize-entered");
        var initializeRelease = Path.Combine(tempDir, "initialize-release");
        var disposeOutput = Path.Combine(tempDir, "initialize-dispose.log");
        PluginCompiler.Compile(
            BlockingInitializePluginSource(pluginName, initializeEntered, initializeRelease, disposeOutput),
            pluginName,
            Path.Combine(tempDir, "Plugins", $"{pluginName}.dll"));
        PluginManager.Init();

        var initialize = Task.Run(PluginManager.InitializeLoadedPlugins);
        Exception? concurrentUnloadError = null;
        try
        {
            await WaitUntilAsync(() => File.Exists(initializeEntered));
            concurrentUnloadError = await Record.ExceptionAsync(
                () => PluginManager.UnloadPluginsAsync(pluginName));
            Assert.False(File.Exists(disposeOutput));
        }
        finally
        {
            File.WriteAllText(initializeRelease, "release");
        }

        await initialize.WaitAsync(TimeSpan.FromSeconds(5));
        var transactionError = Assert.IsType<InvalidOperationException>(concurrentUnloadError);
        Assert.Contains("已有插件热重载事务", transactionError.Message, StringComparison.Ordinal);

        AssertLifecycleOutcome(await PluginManager.UnloadPluginsAsync(pluginName), pluginName);
        Assert.Equal(["disposed"], File.ReadAllLines(disposeOutput));
    }

    [Fact]
    public async Task PluginOwnedHotkeyInFlightBlocksUnloadAndCloseRejectsNewCallback()
    {
        const string pluginName = "HotkeyBarrierPlugin";
        var callbackEntered = Path.Combine(tempDir, "hotkey-callback-entered");
        var callbackRelease = Path.Combine(tempDir, "hotkey-callback-release");
        var lifecycleLog = Path.Combine(tempDir, "hotkey-lifecycle.log");
        PluginCompiler.Compile(
            HotkeyBarrierPluginSource(pluginName, callbackEntered, callbackRelease, lifecycleLog),
            pluginName,
            Path.Combine(tempDir, "Plugins", $"{pluginName}.dll"));
        PluginManager.Init();
        Assert.False(Server.IsRunning, "前置条件:Hotkey unload barrier 测试中 HTTP server 未启动");
        PluginManager.InitializeLoadedPlugins();
        var plugin = Assert.Single(
            PluginManager.SnapshotLoadedPlugins(),
            candidate => PluginManager.InternalName(candidate) == pluginName);
        var entry = HotkeyManager.Hotkeys[(ConsoleKey.F6, 0)];

        var callback = Task.Run(async () =>
        {
            using var lease = PluginManager.EnterPluginCallback(plugin);
            using var callbackScope = PluginManager.EnterPluginCallbackScope();
            using var ownerScope = HotkeyManager.RegisterScope(plugin);
            await entry.Handler();
        });
        await WaitUntilAsync(() => File.Exists(callbackEntered));
        var unload = Task.Run(() => PluginManager.UnloadPluginsAsync(pluginName));
        await WaitUntilAsync(() => !PluginManager.SnapshotPluginStatuses()
            .Single(status => status.InternalName == pluginName)
            .IsLoaded);

        Assert.True(await HotkeyManager.HandleKeyAsync(Key.F6).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(unload.IsCompleted);

        File.WriteAllText(callbackRelease, "release");
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        AssertLifecycleOutcome(
            await unload.WaitAsync(TimeSpan.FromSeconds(5)),
            pluginName);
        Assert.Equal(
            ["callback-entered", "callback-released", "disposed"],
            File.ReadAllLines(lifecycleLog));
        Assert.False(await HotkeyManager.HandleKeyAsync(Key.F6));
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    static void AssertLifecycleOutcome(
        IReadOnlyList<PluginManager.PluginLifecycleResult> results,
        string pluginName)
    {
        var result = Assert.Single(results);
        Assert.Equal(pluginName, result.PluginName);
        Assert.Equal(PluginManager.PluginLifecycleOutcome.Succeeded, result.Outcome);
    }

    string PluginSource() => $$"""
        using System;
        using System.IO;
        using System.Threading;
        using System.Threading.Tasks;
        using Gallop.Endpoints;
        using UmamusumeResponseAnalyzer.Plugin;

        namespace {{PluginName}}Ns;

        public sealed class Plugin : IPlugin
        {
            public string Name
            {
                get
                {
                    File.AppendAllText(@"{{statusGetterLog}}", "Name" + Environment.NewLine);
                    try
                    {
                        PluginManager.ReloadPluginsAsync("{{PluginName}}").GetAwaiter().GetResult();
                    }
                    catch (InvalidOperationException ex)
                    {
                        File.WriteAllText(@"{{statusReloadGuard}}", ex.Message);
                    }
                    return "{{PluginName}}";
                }
            }

            public string Author
            {
                get
                {
                    File.AppendAllText(@"{{statusGetterLog}}", "Author" + Environment.NewLine);
                    return "test";
                }
            }

            public Version Version
            {
                get
                {
                    File.AppendAllText(@"{{statusGetterLog}}", "Version" + Environment.NewLine);
                    return new Version(1, 0);
                }
            }
            public string[] Targets => Array.Empty<string>();

            public void Initialize(IPluginContext context) { }

            [ResponseAnalyzer<GameApi.Account.Index>]
            public ValueTask Analyze(byte[] payload)
            {
                File.AppendAllText(@"{{callbackLog}}", "entered" + Environment.NewLine);
                return ValueTask.CompletedTask;
            }

            public void Dispose()
            {
                File.WriteAllText(@"{{disposeEntered}}", "entered");
                while (!File.Exists(@"{{disposeRelease}}"))
                    Thread.Sleep(10);
                File.WriteAllText(@"{{disposeLog}}", "disposed");
            }
        }
        """;

    static string DisposePluginSource(string pluginName, string disposeLog, bool fail) => $$"""
        using System;
        using System.IO;
        using UmamusumeResponseAnalyzer.Plugin;

        namespace {{pluginName}}Ns;

        public sealed class Plugin : IPlugin
        {
            public string Name => "{{pluginName}}";
            public string Author => "test";
            public string[] Targets => Array.Empty<string>();

            public void Initialize(IPluginContext context) { }

            public void Dispose()
            {
                File.AppendAllText(@"{{disposeLog}}", "disposed" + Environment.NewLine);
                if ({{fail.ToString().ToLowerInvariant()}})
                    throw new DisposeFailureException();
            }
        }

        sealed class DisposeFailureException : Exception
        {
            public override string Message => throw new InvalidOperationException("Message getter failed");
            public override string ToString() => throw new InvalidOperationException("ToString failed");
        }
        """;

    static string BlockingInitializePluginSource(
        string pluginName,
        string initializeEntered,
        string initializeRelease,
        string disposeOutput) => $$"""
        using System;
        using System.IO;
        using System.Threading;
        using UmamusumeResponseAnalyzer.Plugin;

        namespace {{pluginName}}Ns;

        public sealed class Plugin : IPlugin
        {
            public string Name => "{{pluginName}}";
            public string Author => "test";
            public string[] Targets => Array.Empty<string>();

            public void Initialize(IPluginContext context)
            {
                File.WriteAllText(@"{{initializeEntered}}", "entered");
                while (!File.Exists(@"{{initializeRelease}}"))
                    Thread.Sleep(10);
            }

            public void Dispose()
                => File.AppendAllText(@"{{disposeOutput}}", "disposed" + Environment.NewLine);
        }
        """;

    string StartedPluginSource() => $$"""
        using System;
        using System.IO;
        using System.Threading;
        using System.Threading.Tasks;
        using UmamusumeResponseAnalyzer.Plugin;

        namespace {{StartedPluginName}}Ns;

        public sealed class Plugin : IPlugin
        {
            public string Name => "{{StartedPluginName}}";
            public string Author => "test";
            public string[] Targets => Array.Empty<string>();

            public void Initialize(IPluginContext context)
            {
                context.Events.OnStarted(_ =>
                {
                    File.AppendAllText(@"{{startedCallbackLog}}", "started" + Environment.NewLine);
                    return ValueTask.CompletedTask;
                });
            }

            public void Dispose()
            {
                File.WriteAllText(@"{{startedDisposeEntered}}", "entered");
                while (!File.Exists(@"{{startedDisposeRelease}}"))
                    Thread.Sleep(10);
                File.WriteAllText(@"{{startedDisposeLog}}", "disposed");
            }
        }
        """;

    static string SnapshotAnalyzerPluginSource(
        string pluginName,
        string lifecycle,
        int priority,
        string? entered = null,
        string? release = null)
    {
        var callback = entered is null
            ? $$"""
                File.AppendAllText(@"{{lifecycle}}", "{{pluginName}}:callback" + Environment.NewLine);
                """
            : $$"""
                File.AppendAllText(@"{{lifecycle}}", "{{pluginName}}:entered" + Environment.NewLine);
                File.WriteAllText(@"{{entered}}", "entered");
                while (!File.Exists(@"{{release}}"))
                    await Task.Delay(10);
                File.AppendAllText(@"{{lifecycle}}", "{{pluginName}}:released" + Environment.NewLine);
                """;
        return $$"""
            using System;
            using System.IO;
            using System.Threading.Tasks;
            using Gallop.Endpoints;
            using UmamusumeResponseAnalyzer.Plugin;

            namespace {{pluginName}}Ns;

            public sealed class Plugin : IPlugin
            {
                public string Name => "{{pluginName}}";
                public string Author => "test";
                public string[] Targets => Array.Empty<string>();
                public void Initialize(IPluginContext context) { }

                [ResponseAnalyzer<GameApi.Account.Index>({{priority}})]
                public async ValueTask Analyze(byte[] payload)
                {
                    {{callback}}
                }

                public void Dispose()
                    => File.AppendAllText(@"{{lifecycle}}", "{{pluginName}}:dispose" + Environment.NewLine);
            }
            """;
    }

    static string HotkeyBarrierPluginSource(
        string pluginName,
        string callbackEntered,
        string callbackRelease,
        string lifecycleLog) => $$"""
        using System;
        using System.IO;
        using System.Threading.Tasks;
        using UmamusumeResponseAnalyzer.Plugin;
        using UmamusumeResponseAnalyzer.TerminalGui;

        namespace {{pluginName}}Ns;

        public sealed class Plugin : IPlugin
        {
            public string Name => "{{pluginName}}";
            public string Author => "test";
            public string[] Targets => Array.Empty<string>();

            public void Initialize(IPluginContext context)
            {
                HotkeyManager.Register(ConsoleKey.F6, "blocking plugin callback", async () =>
                {
                    File.AppendAllText(@"{{lifecycleLog}}", "callback-entered" + Environment.NewLine);
                    File.WriteAllText(@"{{callbackEntered}}", "entered");
                    while (!File.Exists(@"{{callbackRelease}}"))
                        await Task.Delay(10);
                    File.AppendAllText(@"{{lifecycleLog}}", "callback-released" + Environment.NewLine);
                });
            }

            public void Dispose()
                => File.AppendAllText(@"{{lifecycleLog}}", "disposed" + Environment.NewLine);
        }
        """;

    static void SeedConfig()
    {
        var current = typeof(Config).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static)!;
        if (current.GetValue(null) is null)
            current.SetValue(null, new YamlConfig
            {
                Core = new(),
                Repository = new(),
                Plugin = new(),
                Updater = new(),
                Language = new(),
                Misc = new(),
            });
    }

    static void ResetPluginState()
    {
        PluginManager.RequestAnalyzerMethods.Clear();
        PluginManager.ResponseAnalyzerMethods.Clear();
        PluginManager.ClearHostEventSubscriptions();
        PluginManager.Metadatas.Clear();
        PluginManager.AssemblyMetadatas.Clear();
        PluginManager.FailedPlugins.Clear();
        PluginManager.ContextGroups.Clear();
        foreach (var context in PluginManager.Contexts.Values)
            context.Unload();
        PluginManager.Contexts.Clear();
        PluginManager.AssemblyMap.Clear();
        PluginManager.Assemblies.Clear();
        foreach (var plugin in PluginManager.LoadedPlugins.ToList())
            HotkeyManager.UnregisterByOwner(plugin);
        PluginManager.LoadedPlugins.Clear();
    }
}
