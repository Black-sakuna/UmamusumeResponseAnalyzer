using Terminal.Gui.App;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal sealed class PluginContext(
        IApplication application,
        IPlugin plugin,
        IWorkspaceOutput workspaceOutput,
        PluginHostEvents events) : IPluginContext, IPluginHostEvents
    {
        public IApplication Application { get; } = application;
        public IWorkspaceOutput WorkspaceOutput { get; } = workspaceOutput;
        public IPluginHostEvents Events => this;
        public IPluginAnalyzerRegistry Analyzers { get; } = PluginManager.AnalyzersFor(plugin);

        public IDisposable OnStarted(Func<CancellationToken, ValueTask> handler)
            => events.SubscribeStarted(plugin, handler);
    }

    internal sealed class PluginHostEvents
    {
        readonly object gate = new();
        readonly Dictionary<IPlugin, List<StartedSubscription>> subscriptionsByPlugin = new(ReferenceEqualityComparer.Instance);

        internal IDisposable SubscribeStarted(IPlugin plugin, Func<CancellationToken, ValueTask> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            var subscription = new StartedSubscription(this, plugin, handler);
            lock (gate)
            {
                if (!subscriptionsByPlugin.TryGetValue(plugin, out var subscriptions))
                {
                    subscriptions = [];
                    subscriptionsByPlugin[plugin] = subscriptions;
                }
                subscriptions.Add(subscription);
            }
            return subscription;
        }

        internal async Task TriggerStartedAsync(IEnumerable<IPlugin>? plugins = null, CancellationToken cancellationToken = default)
        {
            using var callback = await PluginManager.EnterPluginCallbackAsync(cancellationToken);
            var subscriptions = Snapshot(plugins);
            foreach (var subscription in subscriptions)
                await subscription.InvokeAsync(cancellationToken);
        }

        internal void DisposeFor(IPlugin plugin)
            => DisposeForLater(plugin)();

        internal Action DisposeForLater(IPlugin plugin)
        {
            List<StartedSubscription> subscriptions = [];
            lock (gate)
            {
                if (subscriptionsByPlugin.Remove(plugin, out var pluginSubscriptions))
                    subscriptions = [.. pluginSubscriptions];
            }

            foreach (var subscription in subscriptions)
                subscription.MarkDisposed();

            return () =>
            {
                foreach (var subscription in subscriptions)
                    subscription.WaitForIdle();
            };
        }

        internal void Clear()
        {
            List<StartedSubscription> subscriptions;
            lock (gate)
            {
                subscriptions = subscriptionsByPlugin.Values.SelectMany(x => x).ToList();
                subscriptionsByPlugin.Clear();
            }

            foreach (var subscription in subscriptions)
                subscription.MarkDisposed();

            foreach (var subscription in subscriptions)
                subscription.WaitForIdle();
        }

        List<StartedSubscription> Snapshot(IEnumerable<IPlugin>? plugins)
        {
            lock (gate)
            {
                if (plugins is null)
                    return subscriptionsByPlugin.Values.SelectMany(x => x).Where(x => !x.IsDisposed).ToList();

                var set = plugins.ToHashSet<IPlugin>(ReferenceEqualityComparer.Instance);
                return set
                    .SelectMany(plugin => subscriptionsByPlugin.TryGetValue(plugin, out var subscriptions) ? subscriptions : [])
                    .Where(x => !x.IsDisposed)
                    .ToList();
            }
        }

        void Remove(StartedSubscription subscription)
        {
            lock (gate)
            {
                if (!subscriptionsByPlugin.TryGetValue(subscription.Plugin, out var subscriptions))
                    return;

                subscriptions.Remove(subscription);
                if (subscriptions.Count == 0)
                    subscriptionsByPlugin.Remove(subscription.Plugin);
            }
        }

        sealed class StartedSubscription(
            PluginHostEvents owner,
            IPlugin plugin,
            Func<CancellationToken, ValueTask> handler) : IDisposable
        {
            int disposed;
            int inFlight;
            readonly ManualResetEventSlim idle = new(initialState: true);

            public IPlugin Plugin { get; } = plugin;
            public bool IsDisposed => Volatile.Read(ref disposed) != 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                    return;

                owner.Remove(this);
            }

            public void MarkDisposed()
            {
                Interlocked.Exchange(ref disposed, 1);
            }

            public void WaitForIdle()
            {
                idle.Wait();
            }

            public async ValueTask InvokeAsync(CancellationToken cancellationToken)
            {
                if (!TryEnter())
                    return;

                try
                {
                    using var callback = PluginManager.EnterPluginCallbackScope();
                    using var scope = HotkeyManager.RegisterScope(Plugin);
                    await handler(cancellationToken);
                }
                catch (Exception ex)
                {
                    TerminalUi.Notify("Plugin", $"插件事件处理错误: {ex.Message}", UiSeverity.Error);
                    TerminalUi.LogException("Plugin", ex);
                }
                finally
                {
                    Exit();
                }
            }

            bool TryEnter()
            {
                if (IsDisposed)
                    return false;

                if (Interlocked.Increment(ref inFlight) == 1)
                    idle.Reset();

                if (!IsDisposed)
                    return true;

                Exit();
                return false;
            }

            void Exit()
            {
                if (Interlocked.Decrement(ref inFlight) == 0)
                    idle.Set();
            }
        }
    }

}
