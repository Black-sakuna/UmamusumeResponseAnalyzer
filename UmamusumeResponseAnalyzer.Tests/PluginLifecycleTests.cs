using System.IO.Compression;
using System.Reflection;
using Gallop;
using Gallop.Endpoints;
using Terminal.Gui.App;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using WatsonWebserver.Core;
using WatsonHttpMethod = WatsonWebserver.Core.HttpMethod;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    [Collection("PluginReload")]
    public sealed class PluginLifecycleTests : IDisposable
    {
        readonly IApplication application;

        public PluginLifecycleTests(PluginRuntimeFixture runtime)
        {
            application = runtime.Application;
            SeedConfig();
            ResetPluginState();
            HotkeyManager.OverlaySink = runtime.Host;
        }

        public void Dispose()
        {
            ResetPluginState();
        }

        [Fact]
        public void RegisterMethods_WhenLaterAnalyzerIsInvalid_RollsBackAllPluginRegistrations()
        {
            var plugin = new PartiallyInvalidPlugin();

            var ex = Assert.Throws<InvalidOperationException>(() => PluginManager.RegisterMethods(plugin));

            Assert.Contains(nameof(PartiallyInvalidPlugin.Invalid), ex.Message, StringComparison.Ordinal);
            Assert.Empty(PluginManager.ResponseAnalyzerMethods);
            Assert.False(RouteExists(plugin, "valid"));
        }

        [Fact]
        public void RegisterMethods_RouteInstanceMethod_RegistersRouteForPluginInstance()
        {
            var plugin = new RoutePlugin();

            PluginManager.RegisterMethods(plugin);

            Assert.True(RouteExists(plugin, "ok"));
        }

        [Fact]
        public void RegisterMethods_RouteWithWrongSignature_FailsFastAndDoesNotRegisterRoute()
        {
            var plugin = new InvalidRoutePlugin();

            var ex = Assert.Throws<InvalidOperationException>(() => PluginManager.RegisterMethods(plugin));

            Assert.Contains("Route", ex.Message, StringComparison.Ordinal);
            Assert.False(RouteExists(plugin, "bad"));
        }

        [Fact]
        public void InitializePlugin_CallsContextInitializeEntrypoint()
        {
            var context = new ContextInitializePlugin();

            PluginManager.InitializePlugin(context);

            Assert.True(context.Initialized);
            Assert.NotNull(context.Context);
            Assert.Same(application, context.Context.Application);
            Assert.Same(context.Context, context.Context.Events);
            Assert.NotNull(context.Context.Analyzers);
        }

        [Fact]
        public async Task InitializeLoadedPlugins_QuarantinesFailingPluginAndContinues()
        {
            var failing = new FailingInitializePlugin();
            var healthy = new ContextInitializePlugin();
            Assert.Throws<InvalidOperationException>(() => failing.Equals(healthy));
            PluginManager.RegisterMethods(failing);
            PluginManager.LoadedPlugins.Add(failing);
            PluginManager.LoadedPlugins.Add(healthy);

            PluginManager.InitializeLoadedPlugins();

            Assert.DoesNotContain(PluginManager.LoadedPlugins, x => ReferenceEquals(x, failing));
            Assert.Contains(PluginManager.LoadedPlugins, x => ReferenceEquals(x, healthy));
            Assert.True(healthy.Initialized);
            Assert.Contains(failing.Name, PluginManager.FailedPlugins);
            Assert.False(RouteExists(failing, "boom"));
            Assert.DoesNotContain(
                PluginManager.ResponseAnalyzerMethods.Values.SelectMany(x => x),
                x => ReferenceEquals(x.Plugin, failing));

            await PluginManager.TriggerStartedForPluginsAsync([failing]);
            Assert.Equal(0, failing.StartedCalls);
        }

        [Fact]
        public async Task TriggerStartedForPluginsAsync_LogsHandlerFailureAndContinues()
        {
            var failing = new StartedFailurePlugin();
            var counter = new StartedCounterPlugin();
            PluginManager.InitializePlugin(failing);
            PluginManager.InitializePlugin(counter);

            var ex = await Record.ExceptionAsync(() => PluginManager.TriggerStartedForPluginsAsync([failing, counter]));

            Assert.Null(ex);
            Assert.Equal(1, counter.StartedCalls);
        }

        [Fact]
        public async Task TriggerStartedForPluginsAsync_RethrowsRequestedCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            var plugin = new CanceledStartedPlugin(cancellation);
            PluginManager.InitializePlugin(plugin);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PluginManager.TriggerStartedForPluginsAsync([plugin], cancellation.Token));
        }

        [Fact]
        public async Task TriggerStartedForPluginsAsync_ChecksCancellationBetweenSubscriptions()
        {
            using var cancellation = new CancellationTokenSource();
            var lateOutput = Path.Combine(Path.GetTempPath(), "ura-started-cancellation-" + Guid.NewGuid().ToString("N"));
            var plugin = new CancelBetweenStartedSubscriptionsPlugin(cancellation, lateOutput);
            PluginManager.InitializePlugin(plugin);
            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    PluginManager.TriggerStartedForPluginsAsync([plugin], cancellation.Token));

                Assert.False(File.Exists(lateOutput));
            }
            finally
            {
                if (File.Exists(lateOutput))
                    File.Delete(lateOutput);
            }
        }

        [Fact]
        public async Task DisposeHostEventSubscriptions_PreventsLaterStartedInvocation()
        {
            var plugin = new StartedCounterPlugin();
            PluginManager.InitializePlugin(plugin);

            PluginManager.DisposeHostEventSubscriptions(plugin);
            await PluginManager.TriggerStartedForPluginsAsync([plugin]);

            Assert.Equal(0, plugin.StartedCalls);
        }

        [Fact]
        public async Task ReloadPlugins_FailsFastInsidePluginCallback()
        {
            using var callback = PluginManager.EnterPluginCallbackScope();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginManager.ReloadPluginsAsync("AnyPlugin"));

            Assert.Contains("插件回调内禁止执行热重载", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PluginConfigPrompt_RunAsync_DelegatesToPluginOwnedPrompt()
        {
            var plugin = new ConfigPromptPlugin();
            LoadTestPlugin(plugin);
            using var cancellation = new CancellationTokenSource();

            await PluginConfigPrompt.RunAsync(plugin, cancellation.Token);

            Assert.Equal(1, plugin.ConfigPromptCalls);
            Assert.Same(application, plugin.Application);
            Assert.Equal(cancellation.Token, plugin.CancellationToken);
        }

        [Fact]
        public async Task PluginConfigPrompt_RunAsync_NoCustomPromptReturnsWithoutHostEditor()
        {
            var plugin = new NoConfigPromptPlugin();
            LoadTestPlugin(plugin);

            await PluginConfigPrompt.RunAsync(plugin);
        }

        [Fact]
        public async Task PluginConfigPrompt_RunAsync_BlocksHotReloadInsidePrompt()
        {
            var plugin = new ReloadingConfigPromptPlugin();
            LoadTestPlugin(plugin);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PluginConfigPrompt.RunAsync(plugin));

            Assert.Contains("插件回调内禁止执行热重载", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void FailedInitializeContext_RejectsLateAnalyzerAndEventRegistration()
        {
            var plugin = new CapturingFailingInitializePlugin();
            PluginManager.LoadedPlugins.Add(plugin);

            PluginManager.InitializeLoadedPlugins();

            var context = Assert.IsAssignableFrom<IPluginContext>(plugin.Context);
            var analyzerError = Assert.Throws<InvalidOperationException>(() =>
                context.Analyzers.RegisterResponse<GameApi.Account.Index>(_ => ValueTask.CompletedTask));
            var eventError = Assert.Throws<InvalidOperationException>(() =>
                context.Events.OnStarted(_ => ValueTask.CompletedTask));
            Assert.Contains("不接受注册", analyzerError.Message, StringComparison.Ordinal);
            Assert.Contains("不接受注册", eventError.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task PluginConfigPrompt_RunAsync_RejectsStalePluginInstance()
        {
            var loaded = new ConfigPromptPlugin();
            var stale = new ConfigPromptPlugin();
            LoadTestPlugin(loaded);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PluginConfigPrompt.RunAsync(stale));

            Assert.Contains("插件已卸载", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, stale.ConfigPromptCalls);
        }

        [Fact]
        public void PluginConfig_BuildPluginChoices_AllowsDuplicateDisplayNames()
        {
            IPlugin[] plugins =
            [
                new DuplicateDisplayNamePlugin("Alice"),
                new DuplicateDisplayNamePlugin("Bob"),
            ];

            var choices = PluginConfig.BuildPluginChoices(plugins);

            Assert.Equal(2, choices.Count);
            Assert.Equal(2, choices.Keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(choices.Values, plugin => Assert.Equal("Same Display Name", plugin.Name));
        }

        [Fact]
        public void PluginConfig_BuildPluginChoices_AllowsDuplicateDisplayNamesAndAuthors()
        {
            IPlugin[] plugins =
            [
                new DuplicateDisplayNamePlugin("Same Author"),
                new DuplicateDisplayNamePlugin("Same Author"),
            ];

            var choices = PluginConfig.BuildPluginChoices(plugins);

            Assert.Equal(2, choices.Count);
            Assert.Equal(2, choices.Keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(choices.Values, plugin => Assert.Equal("Same Display Name", plugin.Name));
        }

        [Fact]
        public void LoadIntoContext_DoesNotCreatePluginDataDirectoryOrSettingsFile()
        {
            using var fixture = new CompiledSettingsPluginFixture();
            var ctx = new PluginManager.PluginLoadContext("SettingsPlugin");

            Assert.True(PluginManager.LoadIntoContext(ctx, fixture.Metadata));

            Assert.False(Directory.Exists(fixture.PluginDataDirectory));
            Assert.False(File.Exists(fixture.SettingsPath));
            ctx.Unload();
        }

        [Fact]
        public void LoadIntoContext_DoesNotReadOrRewritePluginSettingsFile()
        {
            using var fixture = new CompiledSettingsPluginFixture();
            const string settingsYaml = "Value: 99\n";
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.SettingsPath)!);
            File.WriteAllText(fixture.SettingsPath, settingsYaml);
            var ctx = new PluginManager.PluginLoadContext("SettingsPlugin");

            Assert.True(PluginManager.LoadIntoContext(ctx, fixture.Metadata));

            var plugin = Assert.Single(PluginManager.LoadedPlugins, x => x.Name == "SettingsPlugin");
            var value = (int)plugin.GetType().GetProperty("Value")!.GetValue(plugin)!;
            Assert.Equal(1, value);
            Assert.Equal(settingsYaml, File.ReadAllText(fixture.SettingsPath).Replace("\r\n", "\n"));
            ctx.Unload();
        }

        [Fact]
        public void LoadMetadatas_ZipPackage_RegistersOnlyPackageNamedRootDllAsPlugin()
        {
            var originalCwd = Directory.GetCurrentDirectory();
            var tempDir = Path.Combine(Path.GetTempPath(), "ura-plugin-zip-" + Guid.NewGuid().ToString("N"));
            try
            {
                var pluginsDir = Path.Combine(tempDir, "Plugins");
                Directory.CreateDirectory(pluginsDir);

                var pluginDll = Path.Combine(tempDir, "Notifications.dll");
                var dependencyDll = Path.Combine(tempDir, "Newtonsoft.Json.dll");
                PluginCompiler.Compile(
                    """
                    using System.Threading.Tasks;
                    using UmamusumeResponseAnalyzer.Plugin;

                    public sealed class NotificationsPlugin : IPlugin
                    {
                        public string Name => "Notifications";
                        public string Author => "Test";
                        public string[] Targets => System.Array.Empty<string>();

                        public void Initialize(IPluginContext context) { }
                    }
                    """,
                    "Notifications",
                    pluginDll);
                PluginCompiler.Compile(
                    "public sealed class DependencyType { }",
                    "Newtonsoft.Json",
                    dependencyDll);

                using (var zip = ZipFile.Open(Path.Combine(pluginsDir, "Notifications.zip"), ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(pluginDll, "Notifications.dll");
                    zip.CreateEntryFromFile(dependencyDll, "Newtonsoft.Json.dll");
                }

                Directory.SetCurrentDirectory(tempDir);

                PluginManager.LoadMetadatas();
                PluginManager.BuildGroups();
                PluginManager.LoadPlugins();

                Assert.True(PluginManager.Metadatas.ContainsKey("Notifications"));
                Assert.False(PluginManager.Metadatas.ContainsKey("Newtonsoft.Json"));
                Assert.True(PluginManager.AssemblyMetadatas.ContainsKey("Newtonsoft.Json"));
                Assert.DoesNotContain(PluginManager.FailedPlugins, x => x.Contains("Newtonsoft.Json", StringComparison.Ordinal));
                Assert.Contains(PluginManager.LoadedPlugins, x => x.Name == "Notifications");
            }
            finally
            {
                Directory.SetCurrentDirectory(originalCwd);
                try { Directory.Delete(tempDir, recursive: true); } catch { }
                ResetPluginState();
            }
        }

        static bool RouteExists(IPlugin plugin, string route)
            => Server.Instance.Routes.PreAuthentication.Static.Exists(WatsonHttpMethod.GET, $"/{plugin.Name}/{route}");

        static void LoadTestPlugin(IPlugin plugin)
        {
            PluginManager.RegisterMethods(plugin);
            PluginManager.LoadedPlugins.Add(plugin);
            PluginManager.InitializeLoadedPlugins();
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
            {
                HotkeyManager.UnregisterByOwner(plugin);
            }
            PluginManager.LoadedPlugins.Clear();
            RemoveRouteIfExists("/PartiallyInvalidPlugin/valid");
            RemoveRouteIfExists("/RoutePlugin/ok");
            RemoveRouteIfExists("/InvalidRoutePlugin/bad");
            RemoveRouteIfExists("/FailingInitializePlugin/boom");
        }

        static void RemoveRouteIfExists(string path)
        {
            if (Server.Instance.Routes.PreAuthentication.Static.Exists(WatsonHttpMethod.GET, path))
                Server.Instance.Routes.PreAuthentication.Static.Remove(WatsonHttpMethod.GET, path);
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

        abstract class TestPlugin(string name) : IPlugin
        {
            public string Name { get; } = name;
            public string Author => "Test";
            public string[] Targets => [];

            public virtual void Initialize(IPluginContext context)
            {
            }

            public virtual Task ConfigPromptAsync(
                IApplication application,
                CancellationToken cancellationToken = default)
                => Task.CompletedTask;

        }

        sealed class CompiledSettingsPluginFixture : IDisposable
        {
            readonly string originalCwd = Directory.GetCurrentDirectory();
            readonly string tempDir = Path.Combine(Path.GetTempPath(), "ura-plugin-settings-" + Guid.NewGuid().ToString("N"));

            public CompiledSettingsPluginFixture()
            {
                Directory.CreateDirectory(Path.Combine(tempDir, "Plugins"));
                Directory.SetCurrentDirectory(tempDir);
                var dllPath = Path.Combine(tempDir, "Plugins", "SettingsPlugin.dll");
                PluginCompiler.Compile(
                    """
                    using System.Threading.Tasks;
                    using UmamusumeResponseAnalyzer.Plugin;

                    public sealed class SettingsPlugin : IPlugin
                    {
                        public string Name => "SettingsPlugin";
                        public string Author => "Test";
                        public string[] Targets => System.Array.Empty<string>();

                        public int Value { get; set; } = 1;

                        public void Initialize(IPluginContext context) { }
                    }
                    """,
                    "SettingsPlugin",
                    dllPath);

                Metadata = new PluginManager.PluginMetadata(dllPath, "SettingsPlugin", loadInHost: false, shared: [], isFromZip: false);
                PluginDataDirectory = Path.Combine(tempDir, "PluginData", "SettingsPlugin");
                SettingsPath = Path.Combine(PluginDataDirectory, "settings.yaml");
            }

            public PluginManager.PluginMetadata Metadata { get; }
            public string PluginDataDirectory { get; }
            public string SettingsPath { get; }

            public void Dispose()
            {
                ResetPluginState();
                Directory.SetCurrentDirectory(originalCwd);
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        sealed class PartiallyInvalidPlugin : TestPlugin
        {
            public PartiallyInvalidPlugin() : base("PartiallyInvalidPlugin")
            {
            }

            [Route(WatsonHttpMethod.GET, "valid")]
            public Task ValidRoute(HttpContextBase ctx)
                => Task.CompletedTask;

            [ResponseAnalyzer<GameApi.Account.Index>]
            public ValueTask Valid(byte[] payload)
            {
                return ValueTask.CompletedTask;
            }

            [ResponseAnalyzer<GameApi.Account.Index>]
            public void Invalid(DataLinkIndexResponse response)
            {
            }
        }

        sealed class RoutePlugin : TestPlugin
        {
            public RoutePlugin() : base("RoutePlugin")
            {
            }

            [Route(WatsonHttpMethod.GET, "ok")]
            public Task Ok(HttpContextBase ctx)
                => Task.CompletedTask;
        }

        sealed class InvalidRoutePlugin : TestPlugin
        {
            public InvalidRoutePlugin() : base("InvalidRoutePlugin")
            {
            }

            [Route(WatsonHttpMethod.GET, "bad")]
            public Task Bad()
                => Task.CompletedTask;
        }

        sealed class ContextInitializePlugin : TestPlugin
        {
            public ContextInitializePlugin() : base("ContextInitializePlugin")
            {
            }

            public bool Initialized { get; private set; }
            public IPluginContext? Context { get; private set; }

            public override void Initialize(IPluginContext context)
            {
                Initialized = true;
                Context = context;
            }
        }

        sealed class FailingInitializePlugin : TestPlugin
        {
            public FailingInitializePlugin() : base("FailingInitializePlugin")
            {
            }

            public int StartedCalls { get; private set; }

            [Route(WatsonHttpMethod.GET, "boom")]
            public Task Boom(HttpContextBase ctx)
                => Task.CompletedTask;

            [ResponseAnalyzer<GameApi.Account.Index>]
            public ValueTask Analyze(byte[] payload)
                => ValueTask.CompletedTask;

            public override void Initialize(IPluginContext context)
            {
                context.Events.OnStarted(_ =>
                {
                    StartedCalls++;
                    return ValueTask.CompletedTask;
                });
                context.Analyzers.RegisterResponse<GameApi.Account.Index>(_ => ValueTask.CompletedTask);
                throw new InvalidOperationException("plugin initialize failed");
            }

            public override bool Equals(object? obj)
                => throw new InvalidOperationException("plugin equality must not be used");

            public override int GetHashCode()
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
        }

        sealed class StartedFailurePlugin : TestPlugin
        {
            public StartedFailurePlugin() : base("StartedFailurePlugin")
            {
            }

            public override void Initialize(IPluginContext context)
            {
                context.Events.OnStarted(_ => throw new HostileMessageException());
            }
        }

        sealed class HostileMessageException : Exception
        {
            public override string Message => throw new InvalidOperationException("Message getter failed");
            public override string ToString() => throw new InvalidOperationException("ToString failed");
        }

        sealed class StartedCounterPlugin : TestPlugin
        {
            public StartedCounterPlugin() : base("StartedCounterPlugin")
            {
            }

            public int StartedCalls { get; private set; }

            public override void Initialize(IPluginContext context)
            {
                context.Events.OnStarted(_ =>
                {
                    StartedCalls++;
                    return ValueTask.CompletedTask;
                });
            }
        }

        sealed class CanceledStartedPlugin(CancellationTokenSource cancellation)
            : TestPlugin("CanceledStartedPlugin")
        {
            public override void Initialize(IPluginContext context)
            {
                context.Events.OnStarted(cancellationToken =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromCanceled(cancellationToken);
                });
            }
        }

        sealed class CancelBetweenStartedSubscriptionsPlugin(
            CancellationTokenSource cancellation,
            string lateOutput) : TestPlugin("CancelBetweenStartedSubscriptionsPlugin")
        {
            public override void Initialize(IPluginContext context)
            {
                context.Events.OnStarted(_ =>
                {
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                });
                context.Events.OnStarted(_ =>
                {
                    File.WriteAllText(lateOutput, "invoked");
                    return ValueTask.CompletedTask;
                });
            }
        }

        sealed class CapturingFailingInitializePlugin : TestPlugin
        {
            public CapturingFailingInitializePlugin() : base("CapturingFailingInitializePlugin")
            {
            }

            public IPluginContext? Context { get; private set; }

            public override void Initialize(IPluginContext context)
            {
                Context = context;
                throw new InvalidOperationException("initialize failed");
            }
        }

        sealed class ConfigPromptPlugin : TestPlugin
        {
            public ConfigPromptPlugin() : base("ConfigPromptPlugin")
            {
            }

            public int ConfigPromptCalls { get; private set; }
            public IApplication? Application { get; private set; }
            public CancellationToken CancellationToken { get; private set; }

            public override Task ConfigPromptAsync(
                IApplication application,
                CancellationToken cancellationToken = default)
            {
                ConfigPromptCalls++;
                Application = application;
                CancellationToken = cancellationToken;
                return Task.CompletedTask;
            }
        }

        sealed class NoConfigPromptPlugin : TestPlugin
        {
            public NoConfigPromptPlugin() : base("NoConfigPromptPlugin")
            {
            }

        }

        sealed class ReloadingConfigPromptPlugin : TestPlugin
        {
            public ReloadingConfigPromptPlugin() : base("ReloadingConfigPromptPlugin")
            {
            }

            public override async Task ConfigPromptAsync(
                IApplication application,
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                await PluginManager.ReloadPluginsAsync("AnyPlugin");
            }
        }

        sealed class DuplicateDisplayNamePlugin(string author) : IPlugin
        {
            public string Name => "Same Display Name";
            public string Author => author;
            public string[] Targets => [];
            public void Initialize(IPluginContext context) { }
        }

    }
}
