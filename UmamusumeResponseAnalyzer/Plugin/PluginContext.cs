using Terminal.Gui.App;
using UmamusumeResponseAnalyzer.TerminalGui;

namespace UmamusumeResponseAnalyzer.Plugin
{
    internal sealed class PluginContext(
        UiHost host,
        IPlugin plugin,
        PluginHostEvents events) : IPluginContext, IPluginHostEvents
    {
        public IApplication Application { get; } = host.Application;
        public IPluginHostEvents Events => this;
        public IPluginAnalyzerRegistry Analyzers { get; } = PluginManager.AnalyzersFor(plugin);

        public IDisposable OnStarted(Func<CancellationToken, ValueTask> handler)
        {
            using var registration = PluginManager.EnterPluginRegistration(plugin);
            return events.SubscribeStarted(plugin, handler);
        }
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
            cancellationToken.ThrowIfCancellationRequested();
            using var subscriptions = Snapshot(plugins, cancellationToken);
            for (var i = 0; i < subscriptions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await subscriptions[i].InvokeAsync(cancellationToken);
            }
        }

        internal void DisposeFor(IPlugin plugin)
        {
            List<StartedSubscription> subscriptions = [];
            lock (gate)
            {
                if (subscriptionsByPlugin.Remove(plugin, out var pluginSubscriptions))
                    subscriptions = [.. pluginSubscriptions];
            }

            foreach (var subscription in subscriptions)
                subscription.MarkDisposed();
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
        }

        PluginCallbackSnapshot<StartedSubscription> Snapshot(
            IEnumerable<IPlugin>? plugins,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                var candidates = plugins is null
                    ? subscriptionsByPlugin.Values.SelectMany(x => x)
                    : plugins.ToHashSet<IPlugin>(ReferenceEqualityComparer.Instance)
                        .SelectMany(plugin => subscriptionsByPlugin.TryGetValue(plugin, out var subscriptions) ? subscriptions : []);
                return PluginCallbackSnapshot<StartedSubscription>.Create(
                    candidates.Where(x => !x.IsDisposed),
                    static subscription => subscription.Plugin,
                    cancellationToken);
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

            public async ValueTask InvokeAsync(CancellationToken cancellationToken)
            {
                if (IsDisposed)
                    return;

                using var callback = PluginManager.EnterPluginCallbackScope();
                using var ownerScope = HotkeyManager.RegisterScope(Plugin);
                try
                {
                    await handler(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
                    string message;
                    try { message = ex.Message; }
                    catch (Exception messageError)
                    {
                        var messageErrorType = messageError.GetType().FullName ?? messageError.GetType().Name;
                        message = $"<读取 Message 失败: {messageErrorType}>";
                    }

                    var failure = new InvalidOperationException(
                        $"插件事件处理错误: plugin={PluginManager.InternalName(Plugin)}, " +
                        $"exception={exceptionType}, message={message}");
                    _ = PluginManager.ReportPluginFailure("Plugin", failure);
                }
            }
        }
    }

}
