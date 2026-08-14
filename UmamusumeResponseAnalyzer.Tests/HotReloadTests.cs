using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using UmamusumeResponseAnalyzer;
using UmamusumeResponseAnalyzer.TerminalGui;
using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests
{
    // 这个 collection 改 CWD（进程级）并驱动 PluginManager 全局静态状态，必须与所有其它测试串行隔离
    [CollectionDefinition("PluginReload", DisableParallelization = true)]
    public class PluginReloadCollection : ICollectionFixture<PluginRuntimeFixture> { }

    public sealed class PluginRuntimeFixture : IDisposable
    {
        readonly string configDirectory;
        readonly string originalCwd;
        readonly PropertyInfo configCurrent;
        readonly object? originalConfig;
        readonly CultureInfo originalCulture;
        readonly CultureInfo originalUiCulture;
        readonly (FieldInfo Field, object? Value)[] originalResourceCultures;
        readonly Task run;

        public PluginRuntimeFixture()
        {
            originalCwd = Directory.GetCurrentDirectory();
            configCurrent = typeof(Config).GetProperty(
                "Current",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            originalConfig = configCurrent.GetValue(null);
            originalCulture = Thread.CurrentThread.CurrentCulture;
            originalUiCulture = Thread.CurrentThread.CurrentUICulture;
            originalResourceCultures = typeof(Config).Assembly.GetTypes()
                .Where(type => type.Namespace?.StartsWith(
                    "UmamusumeResponseAnalyzer.Localization",
                    StringComparison.Ordinal) == true)
                .Select(type => type.GetField(
                    "resourceCulture",
                    BindingFlags.NonPublic | BindingFlags.Static))
                .OfType<FieldInfo>()
                .Select(field => (field, field.GetValue(null)))
                .ToArray();

            configDirectory = Path.Combine(
                Path.GetTempPath(),
                $"ura-plugin-runtime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(configDirectory);
            Directory.SetCurrentDirectory(configDirectory);

            TerminalGuiTestApp? terminal = null;
            UiHost? host = null;
            Task? startedRun = null;
            try
            {
                Config.Initialize();

                terminal = new TerminalGuiTestApp();
                terminal.RunOnOwnerThread(() =>
                {
                    var createdHost = new UiHost(
                        terminal.Application,
                        SynchronizationContext.Current!,
                        CancellationToken.None);
                    TerminalUi.Initialize(createdHost);
                    host = createdHost;
                });
                var initializedHost = host
                    ?? throw new InvalidOperationException("UiHost fixture initialization did not complete.");
                HotkeyManager.OverlaySink = initializedHost;
                var activeRun = terminal.StartAsync(initializedHost).GetAwaiter().GetResult();
                startedRun = activeRun;

                Terminal = terminal;
                Host = initializedHost;
                run = activeRun;
            }
            catch
            {
                try
                {
                    if (terminal is not null)
                    {
                        try
                        {
                            if (host is not null && startedRun is not null)
                                terminal.StopAsync(host, startedRun).GetAwaiter().GetResult();
                        }
                        finally
                        {
                            terminal.Dispose();
                        }
                    }
                }
                finally
                {
                    RestoreConfigState();
                    Directory.SetCurrentDirectory(originalCwd);
                    Directory.Delete(configDirectory, recursive: true);
                }
                throw;
            }
        }

        internal TerminalGuiTestApp Terminal { get; }
        internal UiHost Host { get; }
        internal IApplication Application => Terminal.Application;

        public void Dispose()
        {
            try
            {
                try
                {
                    Terminal.StopAsync(Host, run).GetAwaiter().GetResult();
                }
                finally
                {
                    Terminal.Dispose();
                }
            }
            finally
            {
                RestoreConfigState();
                Directory.SetCurrentDirectory(originalCwd);
                Directory.Delete(configDirectory, recursive: true);
            }
        }

        void RestoreConfigState()
        {
            configCurrent.SetValue(null, originalConfig);
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            foreach (var (field, value) in originalResourceCultures)
                field.SetValue(null, value);
        }

    }

    /// <summary>
    /// 热重载端到端集成测试：用 Roslyn 现场编译严格 ZIP package，经 manifest Dependencies
    /// 验证独立插件和共享依赖组的 collectible ALC 加载、卸载与重载。
    /// PluginManager 是进程级静态状态，collection 内串行且每个用例前后显式清理。
    /// </summary>
    [Collection("PluginReload")]
    public sealed class HotReloadTests : IDisposable
    {
        readonly string _tempDir;
        readonly string _logPath;
        readonly string _originalCwd;
        readonly PluginRuntimeFixture runtime;

        public HotReloadTests(PluginRuntimeFixture runtime)
        {
            this.runtime = runtime;
            ResetPluginState();
            HotkeyManager.UnregisterAll();
            HotkeyManager.OverlaySink = runtime.Host;

            _originalCwd = Directory.GetCurrentDirectory();
            _tempDir = Path.Combine(Path.GetTempPath(), "ura-hotreload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_tempDir, "Plugins")); // PluginManager 从相对目录 Plugins/ 加载
            _logPath = Path.Combine(_tempDir, "lifecycle-log.txt");
            Directory.SetCurrentDirectory(_tempDir);
        }

        public void Dispose()
        {
            ResetPluginState();
            HotkeyManager.UnregisterAll();
            Directory.SetCurrentDirectory(_originalCwd);
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* 进程仍持有内存中的程序集，文件残留无妨 */ }
        }

        [Fact]
        public async Task HotReload_Works_ForStandalonePluginAndManifestDependencyGroup()
        {
            // 加载（独立插件 v1 + manifest 依赖组 Anchor/Member）并重载；辅助方法返回后旧 ALC 临时引用离栈。
            var (weakStandalone, weakGroup) = await LoadThenReloadAsync();

            // 核心断言①：两个旧 ALC（独立插件的 + 共享组的）都被回收 —— 零引用泄漏、真卸载
            for (var i = 0; i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.False(weakStandalone.IsAlive, "独立插件旧 ALC 未被回收 —— 存在引用泄漏");
            Assert.False(weakGroup.IsAlive, "manifest 依赖组旧 ALC 未被回收 —— 存在引用泄漏");

            // 核心断言②：三个插件都重新注册
            foreach (var name in (string[])["Standalone", "Anchor", "Member"])
                Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == name);

            // 核心断言③：重载后的新实例完成 Initialize。
            var log = File.ReadAllText(_logPath);
            Assert.Contains("Standalone-v2:initialize", log);
            Assert.Contains("Anchor:initialize", log);
            Assert.Contains("Member:initialize", log);

            // 核心断言④：模拟插件仓库 install 一个新成员到已加载的 manifest 依赖组。
            // 回归点：必须整组重载进【单个】共享 ALC；绝不能因组 key 变化把锚点 Anchor 加载进新旧两个 ALC（共享库单例会失效）。
            var pluginsDir = Path.Combine(_tempDir, "Plugins");
            CompilePlugin("Member2", "Member2", "Anchor");
            var member2Result = await PluginManager.LoadPluginsAsync("Member2");

            AssertLifecycleOutcome(member2Result, "Member2", PluginManager.PluginLifecycleOutcome.Succeeded);
            foreach (var n in (string[])["Anchor", "Member", "Member2"])
                Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == n);
            // 含 Anchor 的 collectible 上下文有且仅一个（双加载会出现两个）
            var anchorContexts = PluginManager.Contexts.Keys.Where(k => k.Split('&').Contains("Anchor")).ToList();
            Assert.Single(anchorContexts);
            // 新成员并入了同一个组（同一 ALC），而非新建一个把 Anchor 重复加载
            var groupMembers = anchorContexts[0].Split('&');
            Assert.Contains("Member", groupMembers);
            Assert.Contains("Member2", groupMembers);

            // 新成员并组后完成 Initialize。
            var log2 = File.ReadAllText(_logPath);
            Assert.Contains("Anchor:initialize", log2);
            Assert.Contains("Member:initialize", log2);
            Assert.Contains("Member2:initialize", log2);

            // 回归:manifest InternalName 是运行时身份，并按 OrdinalIgnoreCase 解析。
            CompilePlugin("InternalNamePlugin", "internal-v1");
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("InternalNamePlugin"), "InternalNamePlugin", PluginManager.PluginLifecycleOutcome.Succeeded);
            CompilePlugin("InternalNamePlugin", "internal-v2");
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("InternalNamePlugin"), "InternalNamePlugin", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Equal(
                "internal-v2:initialize",
                File.ReadAllLines(_logPath).Last(line => line.StartsWith("internal-", StringComparison.Ordinal)));

            // 回归:旧依赖组拆开后,被整组卸载的其它旧成员也必须按新拓扑重载回来。
            CompilePlugin("TopologyAnchor", "topology-anchor");
            CompilePlugin("TopologyMember", "topology-member-v1", "TopologyAnchor");
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("TopologyMember"), "TopologyMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            CompilePlugin("TopologyMember", "topology-member-v2");
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("TopologyMember"), "TopologyMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == "TopologyAnchor");
            Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == "TopologyMember");

            // 回归:依赖组中一个成员被删除时,应卸掉该成员并把仍存在的成员重建回来。
            CompilePlugin("DeleteAnchor", "delete-anchor");
            CompilePlugin("DeleteMember", "delete-member", "DeleteAnchor");
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("DeleteMember"), "DeleteMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == "DeleteAnchor");
            Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == "DeleteMember");

            File.Delete(Path.Combine(pluginsDir, "DeleteMember.zip"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("DeleteMember"), "DeleteMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == "DeleteAnchor");
            Assert.DoesNotContain(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == "DeleteMember");
            Assert.False(Server.IsRunning, "前置条件:测试中 server 未启动");
        }

        [Fact]
        public void StatusSnapshotDoesNotRescanPluginDirectory()
        {
            const string pluginName = "StatusOnlyPlugin";
            PluginManager.Init();
            CompilePlugin(pluginName, "status-only");

            Assert.DoesNotContain(
                PluginManager.SnapshotPluginStatuses(),
                status => status.InternalName == pluginName);
            Assert.Contains(
                PluginManager.InspectPluginStatuses(),
                status => status.InternalName == pluginName &&
                          status.DisplayName == pluginName &&
                          status.Author == "Tests" &&
                          status.IsAvailable &&
                          !status.IsLoaded);
        }

        [Fact]
        public async Task RuntimeLifecycle_LoadUnloadReload_UsesInternalNameAndKeepsPluginFiles()
        {
            var pluginsDir = Path.Combine(_tempDir, "Plugins");
            var standalonePath = Path.Combine(pluginsDir, "RuntimeStandalone.zip");
            CompilePlugin("RuntimeStandalone", "runtime-standalone");
            CompilePlugin("RuntimeAnchor", "runtime-anchor");
            CompilePlugin("RuntimeMember", "runtime-member", "RuntimeAnchor");
            PluginManager.Init();

            Assert.Contains(PluginManager.InspectPluginStatuses(), x =>
                x.InternalName == "RuntimeStandalone" &&
                x.DisplayName == "RuntimeStandalone" &&
                x.Author == "Tests" &&
                x.IsLoaded);
            Assert.False(Server.IsRunning, "前置条件:初始插件阶段完成后 HTTP server 尚未启动");
            PluginManager.InitializeLoadedPlugins();
            var initialLog = File.ReadAllText(_logPath);
            Assert.Contains("runtime-standalone:initialize", initialLog);
            Assert.Contains("runtime-anchor:initialize", initialLog);
            Assert.Contains("runtime-member:initialize", initialLog);

            AssertLifecycleOutcome(await PluginManager.UnloadPluginsAsync("runtimestandalone"), "runtimestandalone", PluginManager.PluginLifecycleOutcome.Succeeded);

            Assert.True(File.Exists(standalonePath));
            Assert.DoesNotContain(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeStandalone");
            Assert.Contains(PluginManager.InspectPluginStatuses(), x =>
                x.InternalName == "RuntimeStandalone" && x.IsAvailable && !x.IsLoaded);
            AssertLifecycleOutcome(await PluginManager.LoadPluginsAsync("RuntimeStandalone"), "RuntimeStandalone", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeStandalone");

            AssertLifecycleOutcome(await PluginManager.UnloadPluginsAsync("RuntimeMember"), "RuntimeMember", PluginManager.PluginLifecycleOutcome.Succeeded);

            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeAnchor");
            Assert.DoesNotContain(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeMember");
            AssertLifecycleOutcome(await PluginManager.LoadPluginsAsync("RuntimeMember"), "RuntimeMember", PluginManager.PluginLifecycleOutcome.Succeeded);

            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeAnchor");
            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeMember");
        }

        [Fact]
        public async Task OptionalLinkage_UsesStagedGroupSnapshotAndReloadsSurvivor()
        {
            const string provider = "OptionalProvider";
            const string consumer = "OptionalConsumer";
            var log = Path.Combine(_tempDir, "optional-linkage.log");
            var providerReference = Path.Combine(_tempDir, $"{provider}.dll");
            var providerSource = $$"""
                using System;
                using System.IO;
                using UmamusumeResponseAnalyzer.Plugin;

                public static class ProviderApi
                {
                    public static string Value() => "provider-api";
                }

                public sealed class ProviderPlugin : IPlugin
                {
                    public void Initialize(IPluginContext context)
                        => File.AppendAllText(@"{{log}}", "provider-initialize" + Environment.NewLine);
                }
                """;
            PluginCompiler.Compile(providerSource, provider, providerReference);
            PluginCompiler.CompilePackage(
                $$"""
                using System;
                using System.IO;
                using System.Runtime.CompilerServices;
                using UmamusumeResponseAnalyzer.Plugin;

                public sealed class ConsumerPlugin : IPlugin
                {
                    public void Initialize(IPluginContext context)
                    {
                        var available = context.IsPluginAvailable("{{provider}}");
                        File.AppendAllText(
                            @"{{log}}",
                            $"consumer-available={available};value={(available ? ReadProvider() : "missing")}" + Environment.NewLine);
                    }

                    [MethodImpl(MethodImplOptions.NoInlining)]
                    static string ReadProvider() => ProviderApi.Value();
                }
                """,
                consumer,
                Path.Combine(_tempDir, "Plugins", $"{consumer}.zip"),
                dependencies: [provider],
                referencePaths: [providerReference]);

            PluginManager.Init();
            PluginManager.InitializeLoadedPlugins();
            Assert.Equal(["consumer-available=False;value=missing"], File.ReadAllLines(log));
            Assert.Empty(PluginManager.FailedPlugins);

            PluginCompiler.CompilePackage(
                providerSource,
                provider,
                Path.Combine(_tempDir, "Plugins", $"{provider}.zip"));
            AssertLifecycleOutcome(
                await PluginManager.LoadPluginsAsync(provider),
                provider,
                PluginManager.PluginLifecycleOutcome.Succeeded);
            var installed = File.ReadAllLines(log);
            Assert.Equal("provider-initialize", installed[^2]);
            Assert.Equal("consumer-available=True;value=provider-api", installed[^1]);
            Assert.Single(
                PluginManager.Contexts.Keys,
                key => key.Split('&').Contains(provider) && key.Split('&').Contains(consumer));

            AssertLifecycleOutcome(
                await PluginManager.UnloadPluginsAsync(provider),
                provider,
                PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.DoesNotContain(PluginManager.LoadedPlugins, plugin => PluginManager.InternalName(plugin) == provider);
            Assert.Contains(PluginManager.LoadedPlugins, plugin => PluginManager.InternalName(plugin) == consumer);
            Assert.Equal("consumer-available=False;value=missing", File.ReadAllLines(log)[^1]);
        }

        [Fact]
        public void InitializeLoadedPlugins_FailingDependencyMemberRollsBackWholeGroup()
        {
            var context = InitializeFailingDependencyGroup();

            for (var i = 0; i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(context.IsAlive, "依赖组成员 Initialize 失败后整组 collectible ALC 未回收");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        WeakReference InitializeFailingDependencyGroup()
        {
            const string anchor = "InitializeAnchor";
            const string member = "InitializeMember";
            var lifecycleLog = Path.Combine(_tempDir, "dependency-initialize.log");
            CompileDependencyInitializationPlugin(anchor, lifecycleLog, dependency: null, fail: false);
            CompileDependencyInitializationPlugin(member, lifecycleLog, anchor, fail: true);

            PluginManager.Init();
            var key = Assert.Single(
                PluginManager.Contexts.Keys,
                key => key.Split('&').Contains(anchor) && key.Split('&').Contains(member));
            var context = new WeakReference(PluginManager.Contexts[key]);

            PluginManager.InitializeLoadedPlugins();

            Assert.DoesNotContain(PluginManager.SnapshotLoadedPlugins(), plugin =>
                PluginManager.InternalName(plugin) is anchor or member);
            Assert.DoesNotContain(PluginManager.Contexts.Keys, candidate => candidate == key);
            Assert.DoesNotContain(
                PluginManager.ResponseAnalyzerMethods,
                registration => PluginManager.InternalName(registration.Plugin) is anchor or member);
            var lifecycle = File.ReadAllLines(lifecycleLog);
            Assert.Equal([$"{anchor}:initialize", $"{member}:initialize"], lifecycle[..2]);
            Assert.True(
                Array.IndexOf(lifecycle, $"{member}:dispose") < Array.IndexOf(lifecycle, $"{anchor}:dispose"),
                "依赖者必须先于依赖项 Dispose");
            Assert.Equal(1, lifecycle.Count(line => line == $"{anchor}:dispose"));
            Assert.Equal(1, lifecycle.Count(line => line == $"{member}:dispose"));
            return context;
        }

        [Fact]
        public async Task HotReload_TransientDelegatesReleaseReloadedContextAndUnloadRestoresPersistentHotkey()
        {
            const string scenario = "hotreload-transient-delegates";
            if (!TerminalUiLifecycleChildProcess.IsChild(scenario))
            {
                Assert.Equal(
                    "ok",
                    await TerminalUiLifecycleProcessTests.RunChildAsync(
                        scenario,
                        typeof(HotReloadTests),
                        nameof(HotReload_TransientDelegatesReleaseReloadedContextAndUnloadRestoresPersistentHotkey)));
                return;
            }

            const string workspaceTitle = "Transient shortcut";
            const string panelKey = "canonical-panel";
            var original = Workspace.Create(workspaceTitle);
            original.SetPanel(panelKey, "Canonical", WorkspaceContent.Text("host-owned"), switchToWorkspace: false);
            original.SwitchTo();
            try
            {
                var oldContext = await ExerciseTransientShortcutReloadAndUnloadAsync();

                for (var i = 0; i < 10; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                Assert.False(oldContext.IsAlive, "reload 后旧 notification shortcut 仍强引用 collectible ALC");
                Assert.Same(original, Workspace.Create(workspaceTitle));
                Assert.True(original.RemovePanel(panelKey));
            }
            finally
            {
                HotkeyManager.UnregisterAll();
                runtime.Host.RemoveWorkspace(original);
                await runtime.Host.FlushAsync();
            }

            TerminalUiLifecycleChildProcess.WriteResult("ok");
        }

        /// <summary>
        /// 编译并加载 3 个严格 package，把独立插件升级到 v2 并重载依赖组，返回两个旧 ALC 的弱引用。
        /// NoInlining：让本帧产生的所有指向旧 ALC 的临时引用随返回而释放（测 collectible ALC 卸载的标准手法）。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<(WeakReference Standalone, WeakReference Group)> LoadThenReloadAsync()
        {
            // 独立插件
            CompilePlugin("Standalone", "Standalone-v1");
            // 共享上下文组：Member 通过 manifest Dependencies 与 Anchor 同进一个 collectible ALC
            CompilePlugin("Anchor", "Anchor");
            CompilePlugin("Member", "Member", "Anchor");

            PluginManager.Init();
            Assert.False(Server.IsRunning, "前置条件:热重载测试中 HTTP server 未启动");
            PluginManager.InitializeLoadedPlugins();

            // 都加载了
            foreach (var name in (string[])["Standalone", "Anchor", "Member"])
                Assert.Contains(PluginManager.LoadedPlugins, p => PluginManager.InternalName(p) == name);
            // Anchor 与 Member 在【同一个】context（共享 ALC），独立插件自成一组
            Assert.True(PluginManager.Contexts.Keys.Any(k => k.Contains("Anchor") && k.Contains("Member")),
                "Anchor 与 Member 应在同一个共享 collectible ALC");
            Assert.True(PluginManager.Contexts.ContainsKey("Standalone"));

            // 三个插件均完成初始化。
            var first = File.ReadAllText(_logPath);
            Assert.Contains("Standalone-v1:initialize", first);
            Assert.Contains("Anchor:initialize", first);
            Assert.Contains("Member:initialize", first);
            Assert.Equal(
                ["Anchor", "Member", "Standalone"],
                PluginManager.ResponseAnalyzerMethods
                    .Where(registration => registration.Priority == 0)
                    .Select(registration => PluginManager.InternalName(registration.Plugin))
                    .ToArray());

            // 捕获两个旧 ALC 的弱引用（表达式临时，不落进局部强引用）
            var weakStandalone = new WeakReference(PluginManager.Contexts["Standalone"]);
            var weakGroup = new WeakReference(
                PluginManager.Contexts.First(kv => kv.Key.Contains("Anchor") && kv.Key.Contains("Member")).Value);

            // 独立插件升级到 v2 并重载
            CompilePlugin("Standalone", "Standalone-v2");
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("Standalone"), "Standalone", PluginManager.PluginLifecycleOutcome.Succeeded);

            // 重载共享组（重载任一成员都会整组卸载+重载）
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("Anchor"), "Anchor", PluginManager.PluginLifecycleOutcome.Succeeded);
            var lifecycle = File.ReadAllLines(_logPath);
            Assert.True(
                Array.IndexOf(lifecycle, "Member:dispose") < Array.IndexOf(lifecycle, "Anchor:dispose"),
                "依赖者必须先于依赖项 Dispose");
            Assert.Equal(1, lifecycle.Count(line => line == "Member:dispose"));
            Assert.Equal(1, lifecycle.Count(line => line == "Anchor:dispose"));

            return (weakStandalone, weakGroup);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<WeakReference> ExerciseTransientShortcutReloadAndUnloadAsync()
        {
            const string pluginName = "TransientShortcutPlugin";
            var pluginPath = Path.Combine(_tempDir, "Plugins", $"{pluginName}.zip");
            var shortcutLog = Path.Combine(_tempDir, "shortcut-log.txt");
            HotkeyManager.OverlaySink = runtime.Host;
            var persistentInvocations = 0;
            HotkeyManager.Register(ConsoleKey.F8, "host persistent", () =>
            {
                persistentInvocations++;
                return Task.CompletedTask;
            });

            PluginCompiler.CompilePackage(
                TransientShortcutPluginSource(pluginName, "v1", shortcutLog),
                pluginName,
                pluginPath);
            PluginManager.Init();
            Assert.False(Server.IsRunning, "前置条件:快捷键热重载发生在 HTTP server 启动前");
            PluginManager.InitializeLoadedPlugins();
            var firstPlugin = Assert.Single(
                PluginManager.SnapshotLoadedPlugins(),
                plugin => PluginManager.InternalName(plugin) == pluginName);
            await runtime.Host.FlushAsync();
            Assert.Same(
                firstPlugin,
                HotkeyManager.Hotkeys[(ConsoleKey.F6, ConsoleModifiers.None)].Owner);
            var oldContext = new WeakReference(PluginManager.Contexts[pluginName]);
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            HotkeyManager.HandleKeyAsync(Key.F7).GetAwaiter().GetResult();
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            Assert.Equal(["v1-notification", "v1-popup"], File.ReadAllLines(shortcutLog));
            Assert.Equal(0, persistentInvocations);

            File.Delete(pluginPath);
            PluginCompiler.CompilePackage(
                TransientShortcutPluginSource(pluginName, "v2", shortcutLog),
                pluginName,
                pluginPath);
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync(pluginName), pluginName, PluginManager.PluginLifecycleOutcome.Succeeded);

            await runtime.Host.FlushAsync();
            var reloadedPlugin = Assert.Single(
                PluginManager.SnapshotLoadedPlugins(),
                plugin => PluginManager.InternalName(plugin) == pluginName);
            Assert.NotSame(firstPlugin, reloadedPlugin);
            Assert.Same(
                reloadedPlugin,
                HotkeyManager.Hotkeys[(ConsoleKey.F6, ConsoleModifiers.None)].Owner);
            await runtime.Terminal.WaitForScreenAsync("v2-notification-visible");
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            HotkeyManager.HandleKeyAsync(Key.F7).GetAwaiter().GetResult();
            await runtime.Host.FlushAsync();
            await runtime.Terminal.WaitForScreenAsync("v2-popup-visible");
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            Assert.Equal(
                ["v1-notification", "v1-popup", "v2-notification", "v2-popup"],
                File.ReadAllLines(shortcutLog));
            Assert.Equal(0, persistentInvocations);

            AssertLifecycleOutcome(await PluginManager.UnloadPluginsAsync(pluginName), pluginName, PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.DoesNotContain(
                (ConsoleKey.F6, ConsoleModifiers.None),
                HotkeyManager.Hotkeys.Keys);
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            Assert.Equal(1, persistentInvocations);
            return oldContext;
        }

        void CompilePlugin(
            string pluginName,
            string marker,
            string? dependency = null)
        {
            var packagePath = Path.Combine(_tempDir, "Plugins", $"{pluginName}.zip");
            File.Delete(packagePath);
            PluginCompiler.CompilePackage(
                PluginSource(pluginName, marker),
                pluginName,
                packagePath,
                dependency is null ? [] : [dependency]);
        }

        void CompileDependencyInitializationPlugin(
            string pluginName,
            string lifecycleLog,
            string? dependency,
            bool fail)
        {
            var packagePath = Path.Combine(_tempDir, "Plugins", $"{pluginName}.zip");
            File.Delete(packagePath);
            PluginCompiler.CompilePackage(
                DependencyInitializationPluginSource(pluginName, lifecycleLog, fail),
                pluginName,
                packagePath,
                dependency is null ? [] : [dependency]);
        }

        static void AssertLifecycleOutcome(
            IReadOnlyList<PluginManager.PluginLifecycleResult> results,
            string pluginName,
            PluginManager.PluginLifecycleOutcome outcome)
        {
            var result = Assert.Single(results);
            Assert.Equal(pluginName, result.PluginName, ignoreCase: true);
            Assert.Equal(outcome, result.Outcome);
        }

        /// <summary>生成一个最小 IPlugin 插件源码；Initialize 把 <paramref name="marker"/> 写进 lifecycle 日志。</summary>
        string PluginSource(
            string pluginName,
            string marker)
        {
            return $$"""
                using System;
                using System.IO;
                using System.Threading.Tasks;
                using UmamusumeResponseAnalyzer.Plugin;

                namespace {{pluginName}}Ns
                {
                    public class {{pluginName}}Plugin : IPlugin
                    {
                        public void Initialize(IPluginContext context)
                        {
                            File.AppendAllText(@"{{_logPath}}", "{{marker}}:initialize" + Environment.NewLine);
                            context.Analyzers.Register<ReadOnlyMemory<byte>>(
                                AnalyzerKind.Response,
                                [EndpointPattern.Exact("/umamusume/account/index")],
                                static _ => ValueTask.CompletedTask);
                        }

                        public void Dispose()
                            => File.AppendAllText(@"{{_logPath}}", "{{marker}}:dispose" + Environment.NewLine);
                    }
                }
                """;
        }

        static string DependencyInitializationPluginSource(
            string pluginName,
            string lifecycleLog,
            bool fail)
        {
            var failure = fail
                ? "throw new InvalidOperationException(\"initialize failed\");"
                : string.Empty;
            return $$"""
                using System;
                using System.IO;
                using System.Threading.Tasks;
                using UmamusumeResponseAnalyzer.Plugin;

                namespace {{pluginName}}Ns;

                public sealed class Plugin : IPlugin
                {
                    public void Initialize(IPluginContext context)
                    {
                        File.AppendAllText(@"{{lifecycleLog}}", "{{pluginName}}:initialize" + Environment.NewLine);
                        context.Analyzers.Register<ReadOnlyMemory<byte>>(
                            AnalyzerKind.Response,
                            [EndpointPattern.Exact("/umamusume/account/index")],
                            static _ => ValueTask.CompletedTask);
                        {{failure}}
                    }

                    public void Dispose()
                        => File.AppendAllText(@"{{lifecycleLog}}", "{{pluginName}}:dispose" + Environment.NewLine);
                }
                """;
        }

        static string TransientShortcutPluginSource(string pluginName, string marker, string shortcutLog)
        {
            return $$"""
                using System;
                using System.IO;
                using System.Threading.Tasks;
                using UmamusumeResponseAnalyzer;
                using UmamusumeResponseAnalyzer.TerminalGui;
                using UmamusumeResponseAnalyzer.Plugin;

                namespace {{pluginName}}Ns
                {
                    public class {{pluginName}} : IPlugin
                    {
                        public void Initialize(IPluginContext context)
                        {
                            var workspace = Workspace.Create("Transient shortcut");
                            workspace.BindHotkey(ConsoleKey.F6, description: "{{marker}} workspace");
                            workspace.Notify(
                                "{{marker}}-notification-visible",
                                ttl: TimeSpan.FromMinutes(5),
                                shortcuts: new UiShortcut(ConsoleKey.F8, HandleNotificationAsync));
                            HotkeyManager.Register(ConsoleKey.F7, "popup", popup =>
                            {
                                popup.AddLine("{{marker}}-popup-visible")
                                    .BindShortcut(new UiShortcut(ConsoleKey.F8, HandlePopupAsync));
                                return Task.CompletedTask;
                            });
                        }

                        static Task HandleNotificationAsync()
                        {
                            File.AppendAllText(@"{{shortcutLog}}", "{{marker}}-notification" + Environment.NewLine);
                            return Task.CompletedTask;
                        }

                        static Task HandlePopupAsync()
                        {
                            File.AppendAllText(@"{{shortcutLog}}", "{{marker}}-popup" + Environment.NewLine);
                            return Task.CompletedTask;
                        }
                    }
                }
                """;
        }

        static void ResetPluginState()
            => PluginManager.ShutdownAsync().GetAwaiter().GetResult();
    }
}
