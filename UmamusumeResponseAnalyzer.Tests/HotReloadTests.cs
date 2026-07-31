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
        readonly string configPath;
        readonly string originalConfigPath;
        readonly PropertyInfo configCurrent;
        readonly object? originalConfig;
        readonly CultureInfo originalCulture;
        readonly CultureInfo originalUiCulture;
        readonly (FieldInfo Field, object? Value)[] originalResourceCultures;
        readonly Task run;

        public PluginRuntimeFixture()
        {
            originalConfigPath = Config.CONFIG_FILEPATH;
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

            configPath = Path.Combine(
                Path.GetTempPath(),
                $"ura-plugin-runtime-{Guid.NewGuid():N}.yaml");

            TerminalGuiTestApp? terminal = null;
            UiHost? host = null;
            Task? startedRun = null;
            try
            {
                Config.CONFIG_FILEPATH = configPath;
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
                    File.Delete(configPath);
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
                File.Delete(configPath);
            }
        }

        void RestoreConfigState()
        {
            Config.CONFIG_FILEPATH = originalConfigPath;
            configCurrent.SetValue(null, originalConfig);
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
            foreach (var (field, value) in originalResourceCultures)
                field.SetValue(null, value);
        }

    }

    /// <summary>
    /// 热重载端到端集成测试：用 Roslyn 现场编译插件，经真实 analyzer 分发路径验证两种重载场景——
    /// ① 独立插件 v1→v2：旧 collectible ALC 被 GC（零泄漏）、重载后命中 v2 新代码；
    /// ② 共享上下文组（<c>[SharedContextWith]</c>，对应 EventLoggerPlugin + 依赖者那种结构）：
    ///    整组进一个 collectible ALC，重载任一成员会整组卸载（共享 ALC 被 GC）并重载。
    /// 全部断言基于真实加载/分发路径。
    /// PluginManager 是静态单例，故所有验证放进一次 Init() 里完成（避免跨测试方法的静态状态串扰）。
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
            _logPath = Path.Combine(_tempDir, "analyze-log.txt");         // 插件跨 ALC 写这里，测试读它观测
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
        public async Task HotReload_Works_ForStandalonePluginAndSharedContextGroup()
        {
            // 加载（独立插件 v1 + 共享组 Anchor/Member）→ analyzer dispatch → 重载，全部在不内联的辅助方法里完成，
            // 它返回后持有过旧插件/MethodInfo 的栈帧消失，GC 才能如实反映卸载结果。
            var (weakStandalone, weakGroup) = await LoadDispatchThenReloadAsync();

            // 核心断言①：两个旧 ALC（独立插件的 + 共享组的）都被回收 —— 零引用泄漏、真卸载
            for (var i = 0; (weakStandalone.IsAlive || weakGroup.IsAlive) && i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(weakStandalone.IsAlive, "独立插件旧 ALC 未被回收 —— 存在引用泄漏");
            Assert.False(weakGroup.IsAlive, "共享组旧 ALC 未被回收 —— 存在引用泄漏（整组卸载没卸干净）");

            // 核心断言②：三个插件都重新注册
            foreach (var name in (string[])["Standalone", "Anchor", "Member"])
                Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == name);

            // 核心断言③：重载后再跑 analyzer 分发，独立插件命中 v2 新代码、共享组两成员都重新分发
            File.Delete(_logPath); // 只保留重载后的记录
            Dispatch();
            var log = File.ReadAllText(_logPath);
            Assert.Contains("Standalone-v2:", log); // 独立插件换上了新代码
            Assert.Contains("Anchor:", log);        // 组成员（锚点）重载后仍分发
            Assert.Contains("Member:", log);        // 组成员（依赖者）重载后仍分发

            // 核心断言④：模拟插件仓库 install 一个【新成员】到一个【已加载】的共享组（PluginManager.LoadPluginsAsync）。
            // 回归点：必须整组重载进【单个】共享 ALC；绝不能因组 key 变化把锚点 Anchor 加载进新旧两个 ALC（共享库单例会失效）。
            var pluginsDir = Path.Combine(_tempDir, "Plugins");
            PluginCompiler.Compile(PluginSource("Member2", "Member2", sharedWith: "Anchor"), "Member2", Path.Combine(pluginsDir, "Member2.dll"));
            var member2Result = await PluginManager.LoadPluginsAsync("Member2");

            AssertLifecycleOutcome(member2Result, "Member2", PluginManager.PluginLifecycleOutcome.Succeeded);
            foreach (var n in (string[])["Anchor", "Member", "Member2"])
                Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == n);
            // 含 Anchor 的 collectible 上下文有且仅一个（双加载会出现两个）
            var anchorContexts = PluginManager.Contexts.Keys.Where(k => k.Split('&').Contains("Anchor")).ToList();
            Assert.Single(anchorContexts);
            // 新成员并入了同一个组（同一 ALC），而非新建一个把 Anchor 重复加载
            var groupMembers = anchorContexts[0].Split('&');
            Assert.Contains("Member", groupMembers);
            Assert.Contains("Member2", groupMembers);

            // 再跑 analyzer 分发：新老成员都分发，证明同组共享且新成员生效
            File.Delete(_logPath);
            Dispatch();
            var log2 = File.ReadAllText(_logPath);
            Assert.Contains("Anchor:", log2);
            Assert.Contains("Member:", log2);
            Assert.Contains("Member2:", log2);

            // 回归:之前某个 [SharedContextWith] 插件因缺少锚点加载失败后，会在 ContextGroups 里留下无 ALC 的失败组。
            // 之后热重载一个无关插件时，不能顺手尝试加载这个失败组，否则会绕过 LoadGroup 的缺失锚点处理并崩溃。
            const string staleMember = "MissingSharedMember";
            const string staleAnchor = "MissingSharedAnchor";
            var staleMeta = new PluginManager.PluginMetadata(
                $"X:/nonexistent/{staleMember}.zip|{staleMember}.dll", staleMember,
                loadInHost: false, shared: [staleAnchor], isFromZip: true);
            PluginManager.Metadatas[staleMember] = staleMeta;
            var staleGroup = new HashSet<string> { staleMember, staleAnchor };
            var staleKey = string.Join("&", staleGroup);
            PluginManager.ContextGroups.Add(staleGroup);
            PluginManager.LoadGroup(staleGroup);
            Assert.Contains(staleMeta.FilePath, PluginManager.FailedPlugins);

            PluginCompiler.Compile(PluginSource("Other", "Other"), "Other", Path.Combine(pluginsDir, "Other.dll"));
            var ex = await Record.ExceptionAsync(() => PluginManager.ReloadPluginsAsync("Other"));

            Assert.Null(ex);
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == "Other");
            Assert.False(PluginManager.Contexts.ContainsKey(staleKey), "缺失锚点的失败组不应被无关热重载建出幽灵 ALC");

            // 回归:缺锚点失败后,若后来补装锚点,重载原成员应直接重建该失败组,而不是尝试卸载一个不存在的 ALC。
            const string waitingMember = "WaitingMember";
            const string waitingAnchor = "WaitingAnchor";
            PluginCompiler.Compile(PluginSource(waitingMember, waitingMember, sharedWith: waitingAnchor), waitingMember, Path.Combine(pluginsDir, $"{waitingMember}.dll"));
            var missingAnchorResult = await PluginManager.ReloadPluginsAsync(waitingMember);
            AssertLifecycleOutcome(missingAnchorResult, waitingMember, PluginManager.PluginLifecycleOutcome.Failed);
            Assert.DoesNotContain(PluginManager.LoadedPlugins, p => p.Name == waitingMember);

            PluginCompiler.Compile(PluginSource(waitingAnchor, waitingAnchor), waitingAnchor, Path.Combine(pluginsDir, $"{waitingAnchor}.dll"));
            var fixedAnchorResult = await PluginManager.ReloadPluginsAsync(waitingMember);
            AssertLifecycleOutcome(fixedAnchorResult, waitingMember, PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == waitingMember);
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == waitingAnchor);

            // 回归:宿主内部身份是程序集名(InternalName),不能用 IPlugin.Name(显示名)卸载。
            PluginCompiler.Compile(PluginSource("InternalNamePlugin", "internal-v1", displayName: "显示名"), "InternalNamePlugin", Path.Combine(pluginsDir, "InternalNamePlugin.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("InternalNamePlugin"), "InternalNamePlugin", PluginManager.PluginLifecycleOutcome.Succeeded);
            PluginCompiler.Compile(PluginSource("InternalNamePlugin", "internal-v2", displayName: "显示名"), "InternalNamePlugin", Path.Combine(pluginsDir, "InternalNamePlugin.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("InternalNamePlugin"), "InternalNamePlugin", PluginManager.PluginLifecycleOutcome.Succeeded);
            File.Delete(_logPath);
            Dispatch();
            var internalNameLog = File.ReadAllText(_logPath);
            Assert.DoesNotContain("internal-v1:", internalNameLog);
            Assert.Contains("internal-v2:", internalNameLog);

            // 回归:旧共享组拆开后,被整组卸载的其它旧成员也必须按新拓扑重载回来。
            PluginCompiler.Compile(PluginSource("TopologyAnchor", "topology-anchor"), "TopologyAnchor", Path.Combine(pluginsDir, "TopologyAnchor.dll"));
            PluginCompiler.Compile(PluginSource("TopologyMember", "topology-member-v1", sharedWith: "TopologyAnchor"), "TopologyMember", Path.Combine(pluginsDir, "TopologyMember.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("TopologyMember"), "TopologyMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            PluginCompiler.Compile(PluginSource("TopologyMember", "topology-member-v2"), "TopologyMember", Path.Combine(pluginsDir, "TopologyMember.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("TopologyMember"), "TopologyMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == "TopologyAnchor");
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == "TopologyMember");

            // 回归:共享组中一个成员被删除时,应卸掉该成员并把仍存在的成员重建回来。
            PluginCompiler.Compile(PluginSource("DeleteAnchor", "delete-anchor"), "DeleteAnchor", Path.Combine(pluginsDir, "DeleteAnchor.dll"));
            PluginCompiler.Compile(PluginSource("DeleteMember", "delete-member", sharedWith: "DeleteAnchor"), "DeleteMember", Path.Combine(pluginsDir, "DeleteMember.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("DeleteMember"), "DeleteMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == "DeleteAnchor");
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == "DeleteMember");

            File.Delete(Path.Combine(pluginsDir, "DeleteMember.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("DeleteMember"), "DeleteMember", PluginManager.PluginLifecycleOutcome.Succeeded);
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == "DeleteAnchor");
            Assert.DoesNotContain(PluginManager.LoadedPlugins, p => p.Name == "DeleteMember");
            File.Delete(_logPath);
            Dispatch();
            var deleteMemberLog = File.ReadAllText(_logPath);
            Assert.Contains("delete-anchor:", deleteMemberLog);
            Assert.DoesNotContain("delete-member:", deleteMemberLog);

            // 回归:新版本切到 LoadInHost 时不能先卸载旧 collectible 插件,否则“需重启”期间旧功能直接掉线。
            PluginCompiler.Compile(PluginSource("HostSwitch", "host-switch-v1"), "HostSwitch", Path.Combine(pluginsDir, "HostSwitch.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("HostSwitch"), "HostSwitch", PluginManager.PluginLifecycleOutcome.Succeeded);
            PluginCompiler.Compile(PluginSource("HostSwitch", "host-switch-v2", loadInHost: true), "HostSwitch", Path.Combine(pluginsDir, "HostSwitch.dll"));
            var hostSwitchResult = await PluginManager.ReloadPluginsAsync("HostSwitch");
            AssertLifecycleOutcome(hostSwitchResult, "HostSwitch", PluginManager.PluginLifecycleOutcome.RestartRequired);
            Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == "HostSwitch");
            File.Delete(_logPath);
            Dispatch();
            var hostSwitchLog = File.ReadAllText(_logPath);
            Assert.Contains("host-switch-v1:", hostSwitchLog);
            Assert.DoesNotContain("host-switch-v2:", hostSwitchLog);

            Assert.False(Server.IsRunning, "前置条件:测试中 server 未启动");
        }

        [Fact]
        public async Task RuntimeLifecycle_LoadUnloadReload_UsesInternalNameAndKeepsPluginFiles()
        {
            var pluginsDir = Path.Combine(_tempDir, "Plugins");
            var standalonePath = Path.Combine(pluginsDir, "RuntimeStandalone.dll");
            PluginCompiler.Compile(
                PluginSource("RuntimeStandalone", "runtime-standalone", displayName: "显示名"),
                "RuntimeStandalone",
                standalonePath);
            PluginCompiler.Compile(
                PluginSource("RuntimeAnchor", "runtime-anchor"),
                "RuntimeAnchor",
                Path.Combine(pluginsDir, "RuntimeAnchor.dll"));
            PluginCompiler.Compile(
                PluginSource("RuntimeMember", "runtime-member", sharedWith: "RuntimeAnchor"),
                "RuntimeMember",
                Path.Combine(pluginsDir, "RuntimeMember.dll"));
            PluginManager.Init();

            Assert.Contains(PluginManager.SnapshotPluginStatuses(), x =>
                x.InternalName == "RuntimeStandalone" && x.DisplayName == "显示名" && x.IsLoaded);
            Assert.False(Server.IsRunning, "前置条件:初始插件阶段完成后 HTTP server 尚未启动");
            PluginManager.InitializeLoadedPlugins();
            Dispatch();
            var initialLog = File.ReadAllText(_logPath);
            Assert.Contains("runtime-standalone:", initialLog);
            Assert.Contains("runtime-anchor:", initialLog);
            Assert.Contains("runtime-member:", initialLog);

            AssertLifecycleOutcome(await PluginManager.UnloadPluginsAsync("runtimestandalone"), "runtimestandalone", PluginManager.PluginLifecycleOutcome.Succeeded);

            Assert.True(File.Exists(standalonePath));
            Assert.DoesNotContain(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeStandalone");
            Assert.Contains(PluginManager.SnapshotPluginStatuses(), x =>
                x.InternalName == "RuntimeStandalone" && x.IsAvailable && !x.IsLoaded);
            File.Delete(_logPath);
            Dispatch();
            var unloadedLog = File.ReadAllText(_logPath);
            Assert.DoesNotContain("runtime-standalone:", unloadedLog);
            Assert.Contains("runtime-anchor:", unloadedLog);
            Assert.Contains("runtime-member:", unloadedLog);

            AssertLifecycleOutcome(await PluginManager.LoadPluginsAsync("RuntimeStandalone"), "RuntimeStandalone", PluginManager.PluginLifecycleOutcome.Succeeded);

            File.Delete(_logPath);
            Dispatch();
            var reloadedLog = File.ReadAllText(_logPath);
            Assert.Contains("runtime-standalone:", reloadedLog);

            AssertLifecycleOutcome(await PluginManager.UnloadPluginsAsync("RuntimeMember"), "RuntimeMember", PluginManager.PluginLifecycleOutcome.Succeeded);

            Assert.DoesNotContain(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeAnchor");
            Assert.DoesNotContain(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeMember");
            File.Delete(_logPath);
            Dispatch();
            var groupUnloadedLog = File.ReadAllText(_logPath);
            Assert.Contains("runtime-standalone:", groupUnloadedLog);
            Assert.DoesNotContain("runtime-anchor:", groupUnloadedLog);
            Assert.DoesNotContain("runtime-member:", groupUnloadedLog);

            AssertLifecycleOutcome(await PluginManager.LoadPluginsAsync("RuntimeMember"), "RuntimeMember", PluginManager.PluginLifecycleOutcome.Succeeded);

            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeAnchor");
            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeMember");
        }

        [Fact]
        public void InitializeLoadedPlugins_FailingSharedMemberUnloadsWholeGroup()
        {
            var context = InitializeFailingSharedGroup();

            for (var i = 0; context.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(context.IsAlive, "shared member Initialize 失败后整组 collectible ALC 未回收");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        WeakReference InitializeFailingSharedGroup()
        {
            const string anchor = "InitializeAnchor";
            const string member = "InitializeMember";
            var lifecycleLog = Path.Combine(_tempDir, "shared-initialize.log");
            var pluginsDir = Path.Combine(_tempDir, "Plugins");
            PluginCompiler.Compile(
                SharedInitializationPluginSource(anchor, lifecycleLog, sharedWith: null, fail: false),
                anchor,
                Path.Combine(pluginsDir, $"{anchor}.dll"));
            PluginCompiler.Compile(
                SharedInitializationPluginSource(member, lifecycleLog, anchor, fail: true),
                member,
                Path.Combine(pluginsDir, $"{member}.dll"));

            PluginManager.Init();
            var key = Assert.Single(
                PluginManager.Contexts.Keys,
                key => key.Split('&').Contains(anchor) && key.Split('&').Contains(member));
            var context = new WeakReference(PluginManager.Contexts[key]);

            PluginManager.InitializeLoadedPlugins();

            Assert.DoesNotContain(PluginManager.SnapshotLoadedPlugins(), plugin =>
                PluginManager.InternalName(plugin) is anchor or member);
            Assert.DoesNotContain(PluginManager.Contexts.Keys, candidate => candidate == key);
            var lifecycle = File.ReadAllLines(lifecycleLog);
            Assert.Equal([$"{anchor}:initialize", $"{member}:initialize"], lifecycle[..2]);
            Assert.Equal(1, lifecycle.Count(line => line == $"{anchor}:dispose"));
            Assert.Equal(1, lifecycle.Count(line => line == $"{member}:dispose"));
            return context;
        }

        [Fact]
        public async Task RuntimeLifecycle_LoadInHostContextUnloadAndReloadNeedRestart()
        {
            var pluginsDir = Path.Combine(_tempDir, "Plugins");
            PluginCompiler.Compile(
                PluginSource("RuntimeHostPlugin", "runtime-host", loadInHost: true),
                "RuntimeHostPlugin",
                Path.Combine(pluginsDir, "RuntimeHostPlugin.dll"));
            PluginManager.Init();

            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeHostPlugin");

            AssertLifecycleOutcome(await PluginManager.UnloadPluginsAsync("RuntimeHostPlugin"), "RuntimeHostPlugin", PluginManager.PluginLifecycleOutcome.RestartRequired);
            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeHostPlugin");

            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("RuntimeHostPlugin"), "RuntimeHostPlugin", PluginManager.PluginLifecycleOutcome.RestartRequired);
            Assert.Contains(PluginManager.LoadedPlugins, x => PluginManager.InternalName(x) == "RuntimeHostPlugin");
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

                for (var i = 0; oldContext.IsAlive && i < 10; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
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
        /// 编译并加载 3 个插件 → dispatch → 把独立插件升级到 v2 并重载、把共享组重载，返回两个旧 ALC 的弱引用。
        /// NoInlining：让本帧产生的所有指向旧 ALC 的临时引用随返回而释放（测 collectible ALC 卸载的标准手法）。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<(WeakReference Standalone, WeakReference Group)> LoadDispatchThenReloadAsync()
        {
            var pluginsDir = Path.Combine(_tempDir, "Plugins");
            // 独立插件
            PluginCompiler.Compile(PluginSource("Standalone", "Standalone-v1"), "Standalone", Path.Combine(pluginsDir, "Standalone.dll"));
            // 共享上下文组：Member 用 [SharedContextWith("Anchor")] 与 Anchor 同进一个 collectible ALC
            PluginCompiler.Compile(PluginSource("Anchor", "Anchor"), "Anchor", Path.Combine(pluginsDir, "Anchor.dll"));
            PluginCompiler.Compile(PluginSource("Member", "Member", sharedWith: "Anchor"), "Member", Path.Combine(pluginsDir, "Member.dll"));

            PluginManager.Init();
            Assert.False(Server.IsRunning, "前置条件:热重载测试中 HTTP server 未启动");
            PluginManager.InitializeLoadedPlugins();

            // 都加载了
            foreach (var name in (string[])["Standalone", "Anchor", "Member"])
                Assert.Contains(PluginManager.LoadedPlugins, p => p.Name == name);
            // Anchor 与 Member 在【同一个】context（共享 ALC），独立插件自成一组
            Assert.True(PluginManager.Contexts.Keys.Any(k => k.Contains("Anchor") && k.Contains("Member")),
                "Anchor 与 Member 应在同一个共享 collectible ALC");
            Assert.True(PluginManager.Contexts.ContainsKey("Standalone"));

            // analyzer 分发，三个 analyzer 都应被调用
            Dispatch();
            var first = File.ReadAllText(_logPath);
            Assert.Contains("Standalone-v1:", first);
            Assert.Contains("Anchor:", first);
            Assert.Contains("Member:", first);

            // 捕获两个旧 ALC 的弱引用（表达式临时，不落进局部强引用）
            var weakStandalone = new WeakReference(PluginManager.Contexts["Standalone"]);
            var weakGroup = new WeakReference(
                PluginManager.Contexts.First(kv => kv.Key.Contains("Anchor") && kv.Key.Contains("Member")).Value);

            // 独立插件升级到 v2 并重载
            PluginCompiler.Compile(PluginSource("Standalone", "Standalone-v2"), "Standalone", Path.Combine(pluginsDir, "Standalone.dll"));
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("Standalone"), "Standalone", PluginManager.PluginLifecycleOutcome.Succeeded);

            // 重载共享组（重载任一成员都会整组卸载+重载）
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync("Anchor"), "Anchor", PluginManager.PluginLifecycleOutcome.Succeeded);

            return (weakStandalone, weakGroup);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        async Task<WeakReference> ExerciseTransientShortcutReloadAndUnloadAsync()
        {
            const string pluginName = "TransientShortcutPlugin";
            var pluginPath = Path.Combine(_tempDir, "Plugins", $"{pluginName}.dll");
            var shortcutLog = Path.Combine(_tempDir, "shortcut-log.txt");
            HotkeyManager.OverlaySink = runtime.Host;
            var persistentInvocations = 0;
            HotkeyManager.Register(ConsoleKey.F8, "host persistent", () =>
            {
                persistentInvocations++;
                return Task.CompletedTask;
            });

            PluginCompiler.Compile(TransientShortcutPluginSource(pluginName, "v1", shortcutLog), pluginName, pluginPath);
            PluginManager.Init();
            Assert.False(Server.IsRunning, "前置条件:快捷键热重载发生在 HTTP server 启动前");
            PluginManager.InitializeLoadedPlugins();
            var oldContext = new WeakReference(PluginManager.Contexts[pluginName]);
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            HotkeyManager.HandleKeyAsync(Key.F7).GetAwaiter().GetResult();
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            Assert.Equal(["v1-notification", "v1-popup"], File.ReadAllLines(shortcutLog));
            Assert.Equal(0, persistentInvocations);

            PluginCompiler.Compile(TransientShortcutPluginSource(pluginName, "v2", shortcutLog), pluginName, pluginPath);
            AssertLifecycleOutcome(await PluginManager.ReloadPluginsAsync(pluginName), pluginName, PluginManager.PluginLifecycleOutcome.Succeeded);

            await runtime.Host.FlushAsync();
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
            HotkeyManager.HandleKeyAsync(Key.F8).GetAwaiter().GetResult();
            Assert.Equal(1, persistentInvocations);
            return oldContext;
        }

        /// <summary>经真实 typed/raw analyzer 分发路径调用已注册插件。</summary>
        static void Dispatch()
        {
            Server.DispatchResponse("/umamusume/account/index", [0xC0]).GetAwaiter().GetResult();
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

        /// <summary>生成一个最小 IPlugin 插件源码；analyzer 把 <paramref name="marker"/> 写进日志文件；可选 SharedContextWith。</summary>
        string PluginSource(
            string pluginName,
            string marker,
            string? sharedWith = null,
            string? displayName = null,
            bool loadInHost = false)
        {
            var attributes = new List<string>();
            if (sharedWith is not null)
                attributes.Add($"[assembly: SharedContextWith(\"{sharedWith}\")]");
            if (loadInHost)
                attributes.Add("[assembly: LoadInHostContext]");
            var assemblyAttributes = string.Join("\n", attributes);
            var pluginDisplayName = displayName ?? pluginName;
            return $$"""
                using System.IO;
                using System.Threading.Tasks;
                using Gallop.Endpoints;
                using UmamusumeResponseAnalyzer.Plugin;

                {{assemblyAttributes}}
                namespace {{pluginName}}Ns
                {
                    public class {{pluginName}}Plugin : IPlugin
                    {
                        public string Name => "{{pluginDisplayName}}";
                        public string Author => "test";
                        public string[] Targets => System.Array.Empty<string>();
                        public void Initialize(IPluginContext context) { }

                        [ResponseAnalyzer<GameApi.Account.Index>]
                        public ValueTask Analyze(byte[] payload)
                        {
                            File.AppendAllText(@"{{_logPath}}", "{{marker}}:" + payload.Length + "\n");
                            return ValueTask.CompletedTask;
                        }
                    }
                }
                """;
        }

        static string SharedInitializationPluginSource(
            string pluginName,
            string lifecycleLog,
            string? sharedWith,
            bool fail)
        {
            var attribute = sharedWith is null
                ? string.Empty
                : $"[assembly: SharedContextWith(\"{sharedWith}\")]";
            var failure = fail
                ? "throw new InvalidOperationException(\"initialize failed\");"
                : string.Empty;
            return $$"""
                using System;
                using System.IO;
                using UmamusumeResponseAnalyzer.Plugin;

                {{attribute}}
                namespace {{pluginName}}Ns;

                public sealed class Plugin : IPlugin
                {
                    public string Name => "{{pluginName}}";
                    public string Author => "test";
                    public string[] Targets => Array.Empty<string>();

                    public void Initialize(IPluginContext context)
                    {
                        File.AppendAllText(@"{{lifecycleLog}}", "{{pluginName}}:initialize" + Environment.NewLine);
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
                        public string Name => "{{pluginName}}";
                        public string Author => "test";
                        public string[] Targets => Array.Empty<string>();
                        public void Initialize(IPluginContext context)
                        {
                            var workspace = Workspace.Create("Transient shortcut");
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
}
