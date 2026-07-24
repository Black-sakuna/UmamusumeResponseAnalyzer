using System.Reflection;
using System.Runtime.CompilerServices;
using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("PluginReload")]
public sealed class PluginDispatchReloadTests : IDisposable
{
    const string PluginName = "YieldingDispatchPlugin";

    readonly string originalCwd = Directory.GetCurrentDirectory();
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "ura-dispatch-reload-" + Guid.NewGuid().ToString("N"));
    readonly string disposeLog;

    public PluginDispatchReloadTests()
    {
        SeedConfig();
        ResetPluginState();
        KeyboardManager.UnregisterAll();

        Directory.CreateDirectory(Path.Combine(tempDir, "Plugins"));
        Directory.SetCurrentDirectory(tempDir);
        disposeLog = Path.Combine(tempDir, "dispose.log");
    }

    public void Dispose()
    {
        ResetPluginState();
        KeyboardManager.UnregisterAll();
        KeyboardManager.OverlaySink = null;
        Directory.SetCurrentDirectory(originalCwd);
        try { Directory.Delete(tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task ReloadWaitsForYieldingAnalyzerBeforeDisposingAndUnloadingContext()
    {
        var oldContext = await DispatchAndReloadAsync();

        for (var i = 0; oldContext.IsAlive && i < 20; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(oldContext.IsAlive, "reload 后旧 collectible ALC 未被回收");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    async Task<WeakReference> DispatchAndReloadAsync()
    {
        PluginCompiler.Compile(
            PluginSource(),
            PluginName,
            Path.Combine(tempDir, "Plugins", $"{PluginName}.dll"));
        PluginManager.Init();

        var plugin = Assert.Single(
            PluginManager.LoadedPlugins,
            plugin => PluginManager.InternalName(plugin) == PluginName);
        var entered = GetSignal(plugin, "Entered");
        var release = GetSignal(plugin, "Release");
        var oldContext = new WeakReference(PluginManager.Contexts[PluginName]);

        var dispatch = Server.DispatchResponse("/umamusume/account/index", [0xC0]).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reload = Task.Run(async () =>
        {
            reloadStarted.TrySetResult();
            return await PluginManager.ReloadPluginsAsync(PluginName);
        });
        await reloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        var reloadWaited = !reload.IsCompleted;
        var disposedBeforeRelease = File.Exists(disposeLog);

        release.TrySetResult();
        IReadOnlyList<string>? needRestart = null;
        var error = await Record.ExceptionAsync(async () =>
        {
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
            needRestart = await reload.WaitAsync(TimeSpan.FromSeconds(5));
        });

        Assert.True(reloadWaited, "analyzer callback 尚未返回时 reload 不应完成");
        Assert.False(disposedBeforeRelease, "analyzer callback 尚未返回时 plugin 不应 Dispose");
        Assert.IsNotType<SynchronizationLockException>(error);
        Assert.Null(error);
        Assert.Empty(needRestart!);
        Assert.Equal("disposed", File.ReadAllText(disposeLog));
        return oldContext;
    }

    static TaskCompletionSource GetSignal(IPlugin plugin, string propertyName)
        => Assert.IsType<TaskCompletionSource>(
            plugin.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static)!.GetValue(null));

    string PluginSource() => $$"""
        using System;
        using System.IO;
        using System.Threading.Tasks;
        using Gallop.Endpoints;
        using UmamusumeResponseAnalyzer.Plugin;

        namespace {{PluginName}}Ns;

        public sealed class Plugin : IPlugin
        {
            public static TaskCompletionSource Entered { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public static TaskCompletionSource Release { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public string Name => "{{PluginName}}";
            public string Author => "test";
            public string[] Targets => Array.Empty<string>();

            public void Initialize(IPluginContext context) { }

            [ResponseAnalyzer<GameApi.Account.Index>]
            public async ValueTask Analyze(byte[] payload)
            {
                await Task.Yield();
                Entered.TrySetResult();
                await Release.Task;
            }

            public void Dispose() => File.WriteAllText(@"{{disposeLog}}", "disposed");
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
            KeyboardManager.UnregisterByOwner(plugin);
        PluginManager.LoadedPlugins.Clear();
    }
}
