using System.Reflection;
using UmamusumeResponseAnalyzer.LiveDisplay;
using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    [Collection("PluginReload")]
    public sealed class PluginCommandLifecycleTests : IDisposable
    {
        readonly string _originalCwd;
        readonly string _tempDir;

        public PluginCommandLifecycleTests()
        {
            ResetKeyboardManager();
            SeedConfig();
            ResetPluginState();

            _originalCwd = Directory.GetCurrentDirectory();
            _tempDir = Path.Combine(Path.GetTempPath(), "ura-plugin-command-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "Plugins"));
            Directory.SetCurrentDirectory(_tempDir);
        }

        public void Dispose()
        {
            ResetKeyboardManager();
            ResetPluginState();
            Directory.SetCurrentDirectory(_originalCwd);
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public async Task PluginCommand_ReloadSuccessShowsProgressPopup()
        {
            var pluginPath = Path.Combine(_tempDir, "Plugins", "CommandPopup.dll");
            PluginCompiler.Compile(PluginSource("CommandPopup"), "CommandPopup", pluginPath);
            PluginManager.Init();

            var uiHost = new UiHost();
            KeyboardManager.OverlaySink = uiHost;
            var workspace = uiHost.CreateWorkspace("插件");
            uiHost.SetPanel(new LiveDisplayPanel(
                workspace,
                "Plugin",
                "main",
                "插件",
                LiveDisplayContent.Text("PluginBody"),
                DateTimeOffset.Now));

            await uiHost.HandleCommandAsync("/plugin reload CommandPopup");
            var output = Render(uiHost, width: 120, height: 35);
            var popup = uiHost.GetKeyboardPopupForTests();

            Assert.NotNull(popup);
            Assert.Contains(popup.Lines, line => line.Text == "Plugin command");
            Assert.Contains(popup.Lines, line => line.Text.Contains("CommandPopup 已重载", StringComparison.Ordinal));
            Assert.Contains("CommandPopup", output);
            Assert.Contains("已重载", output);
            Assert.DoesNotContain("用法: /plugin", output);
        }

        [Fact]
        public async Task PluginCommand_ReloadAllowsPluginConsoleOutputWhileHostRuns()
        {
            var pluginPath = Path.Combine(_tempDir, "Plugins", "CommandOutput.dll");
            PluginCompiler.Compile(PluginSource("CommandOutput", writesConsoleOutput: true), "CommandOutput", pluginPath);
            PluginManager.Init();

            var uiHost = new UiHost();
            KeyboardManager.OverlaySink = uiHost;
            UiHost.HasInteractiveConsoleOverrideForTests = false;
            LiveDisplayConsole.Bind(uiHost);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var uiRun = uiHost.RunAsync(cts.Token);

            try
            {
                while (!uiHost.IsRunning)
                    await Task.Delay(10, cts.Token);

                await uiHost.HandleCommandAsync("/plugin reload CommandOutput")
                    .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                Assert.True(uiHost.IsRunning);
            }
            finally
            {
                uiHost.RequestShutdown();
                cts.Cancel();
                try { await uiRun.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { }
                LiveDisplayConsole.Unbind(uiHost);
                UiHost.HasInteractiveConsoleOverrideForTests = null;
            }
        }

        static string PluginSource(string pluginName, bool writesConsoleOutput = false)
        {
            var initialize = writesConsoleOutput
                ? "LiveDisplayConsole.WriteLine(\"PLUGIN_INIT_OUTPUT\");"
                : string.Empty;
            return $$"""
                using System.Threading.Tasks;
                using UmamusumeResponseAnalyzer.LiveDisplay;
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

        static void ResetKeyboardManager()
        {
            using (KeyboardManager.SuspendInput())
            {
            }

            KeyboardManager.UnregisterAll();
            KeyboardManager.SetCommandHandler(null);
            KeyboardManager.OverlaySink = null;
            KeyboardManager.PopupAutoCloseDelay = TimeSpan.FromSeconds(3);
            LiveDisplayConsole.UnbindForTests();
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

        static string Render(UiHost uiHost, int width, int height)
            => uiHost.RenderSnapshotForTests(width, height);
    }
}
