using System.Reflection;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("PluginReload")]
public sealed class PluginCommandLifecycleTests : IDisposable
{
    readonly string originalCwd;
    readonly string tempDir;
    readonly TerminalGuiTestApp terminal;
    readonly UiHost host;
    Task? run;

    public PluginCommandLifecycleTests()
    {
        ResetHotkeyManager();
        SeedConfig();
        ResetPluginState();

        originalCwd = Directory.GetCurrentDirectory();
        tempDir = Path.Combine(Path.GetTempPath(), "ura-plugin-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "Plugins"));
        Directory.SetCurrentDirectory(tempDir);

        terminal = new(width: 120, height: 35);
        host = new(terminal.Application, static () => [], static _ => { });
        terminal.RunOnOwnerThread(() => TerminalUi.Bind(host, terminal.Application));
        HotkeyManager.OverlaySink = host;
        PluginManager.BindWorkspaceOutput(terminal.Application, plugin => host.ForPlugin(plugin.Name));
    }

    public void Dispose()
    {
        try
        {
            if (run is { IsCompleted: false })
            {
                host.RequestShutdown();
                if (!run.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Plugin command UI session did not stop.");
            }
        }
        finally
        {
            try
            {
                terminal.RunOnOwnerThread(() => TerminalUi.Unbind(host));
                terminal.Dispose();
            }
            finally
            {
                ResetHotkeyManager();
                ResetPluginState();
                Directory.SetCurrentDirectory(originalCwd);
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task PluginCommand_ReloadSuccessShowsProgressPopup()
    {
        var pluginPath = Path.Combine(tempDir, "Plugins", "CommandPopup.dll");
        PluginCompiler.Compile(PluginSource("CommandPopup"), "CommandPopup", pluginPath);
        PluginManager.Init();
        var workspace = host.CreateWorkspace("插件");
        host.SetPanel(new WorkspacePanel(
            workspace,
            "Plugin",
            "main",
            "插件",
            WorkspaceContent.Text("PluginBody"),
            DateTimeOffset.Now));
        run = await terminal.StartAsync(host);

        await host.HandleCommandAsync("/plugin reload CommandPopup");
        await terminal.WaitForScreenAsync("CommandPopup 已重载");
        var screen = await terminal.CaptureScreenAsync();
        var popup = await terminal.InvokeAsync(host.GetHotkeyPopupForTests);

        Assert.NotNull(popup);
        Assert.Contains(popup.Lines, line => line.Text == "Plugin command");
        Assert.Contains(popup.Lines, line =>
            line.Text.Contains("CommandPopup 已重载", StringComparison.Ordinal));
        Assert.Contains("PluginBody", screen);
        Assert.DoesNotContain("用法: /plugin", screen);
    }

    [Fact]
    public async Task ReloadedPlugin_CanInitializeAndPostOutputWhileMainLoopRuns()
    {
        var pluginPath = Path.Combine(tempDir, "Plugins", "CommandOutput.dll");
        PluginCompiler.Compile(
            PluginSource("CommandOutput", writesConsoleOutput: true),
            "CommandOutput",
            pluginPath);
        PluginManager.Init();
        run = await terminal.StartAsync(host);

        await host.HandleCommandAsync("/plugin reload CommandOutput")
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        PluginManager.InitializePlugin(
            Assert.Single(PluginManager.LoadedPlugins, plugin => plugin.Name == "CommandOutput"));
        await terminal.WaitForAsync(async () =>
            await terminal.InvokeAsync(() =>
                host.GetLogsForTests(null).Any(line =>
                    line.Text.Contains("PLUGIN_INIT_OUTPUT", StringComparison.Ordinal))));
        var logs = await terminal.InvokeAsync(() => host.GetLogsForTests(null).ToArray());

        Assert.True(host.IsRunning);
        Assert.Contains(
            logs,
            line => line.Text.Contains("PLUGIN_INIT_OUTPUT", StringComparison.Ordinal));
    }

    static string PluginSource(string pluginName, bool writesConsoleOutput = false)
    {
        var initialize = writesConsoleOutput
            ? "TerminalUi.Log(\"URA\", \"PLUGIN_INIT_OUTPUT\");"
            : string.Empty;
        return $$"""
            using UmamusumeResponseAnalyzer.TerminalGui;
            using UmamusumeResponseAnalyzer.Plugin;

            public sealed class {{pluginName}} : IPlugin
            {
                public string Name => "{{pluginName}}";
                public string Author => "Test";
                public string[] Targets => System.Array.Empty<string>();

                public void Initialize(IPluginContext context) { {{initialize}} }
            }
            """;
    }

    static void ResetHotkeyManager()
    {
        HotkeyManager.UnregisterAll();
        HotkeyManager.OverlaySink = null;
        HotkeyManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
        TerminalUi.UnbindForTests();
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
}
