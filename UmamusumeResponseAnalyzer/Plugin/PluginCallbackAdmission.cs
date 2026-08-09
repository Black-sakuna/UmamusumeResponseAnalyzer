namespace UmamusumeResponseAnalyzer.Plugin;

internal sealed class PluginCallbackSnapshot<T>(
    List<T> items,
    List<IDisposable> leases) : IDisposable
{
    List<T>? items = items;
    List<IDisposable>? leases = leases;

    internal int Count => items?.Count ?? 0;
    internal T this[int index] => items![index];

    internal static PluginCallbackSnapshot<T> Create(
        IEnumerable<T> candidates,
        Func<T, IPlugin> plugin,
        CancellationToken cancellationToken = default)
    {
        var items = new List<T>();
        var leases = new List<IDisposable>();
        try
        {
            foreach (var candidate in candidates)
            {
                var lease = PluginManager.TryEnterPluginCallback(plugin(candidate), cancellationToken);
                if (lease is null)
                    continue;
                items.Add(candidate);
                leases.Add(lease);
            }
            return new(items, leases);
        }
        catch
        {
            items.Clear();
            foreach (var lease in leases)
                lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var items = Interlocked.Exchange(ref this.items, null);
        var leases = Interlocked.Exchange(ref this.leases, null);
        items?.Clear();
        if (leases is null)
            return;

        foreach (var lease in leases)
            lease.Dispose();
        leases.Clear();
    }
}

internal sealed class PluginGeneration(IPlugin plugin)
{
    readonly object gate = new();
    TaskCompletionSource drained = CompletedSignal();
    int inFlight;
    bool initializing;
    bool accepting;
    bool closed;

    internal IPlugin Plugin { get; } = plugin;

    internal bool IsAccepting
    {
        get
        {
            lock (gate)
                return accepting;
        }
    }

    internal IDisposable EnterInitialization()
    {
        lock (gate)
        {
            if (closed)
                throw Closed();
            if (initializing || accepting)
                throw new InvalidOperationException($"插件 generation 已在初始化或运行: {PluginManager.InternalName(Plugin)}");

            initializing = true;
            return EnterLocked();
        }
    }

    internal void CompleteInitialization(bool activateCallbacks)
    {
        lock (gate)
        {
            if (!initializing)
                throw new InvalidOperationException($"插件 generation 未在初始化: {PluginManager.InternalName(Plugin)}");

            initializing = false;
            if (closed)
                throw Closed();
            accepting = activateCallbacks;
        }
    }

    internal void AbortInitialization()
    {
        lock (gate)
            initializing = false;
    }

    internal void Open()
    {
        lock (gate)
        {
            if (closed)
                throw Closed();
            if (initializing)
                throw new InvalidOperationException($"插件 generation 仍在初始化: {PluginManager.InternalName(Plugin)}");
            accepting = true;
        }
    }

    internal Task Close()
    {
        lock (gate)
        {
            accepting = false;
            closed = true;
            return drained.Task;
        }
    }

    internal bool TryEnterCallback(out IDisposable? lease)
    {
        lock (gate)
        {
            if (!accepting)
            {
                lease = null;
                return false;
            }

            lease = EnterLocked();
            return true;
        }
    }

    internal bool TryEnterInspection(out IDisposable? lease)
    {
        lock (gate)
        {
            if (closed)
            {
                lease = null;
                return false;
            }

            lease = EnterLocked();
            return true;
        }
    }

    internal bool TryEnterRegistration(out IDisposable? lease)
    {
        lock (gate)
        {
            if (closed || !initializing && !accepting)
            {
                lease = null;
                return false;
            }

            lease = EnterLocked();
            return true;
        }
    }

    IDisposable EnterLocked()
    {
        if (inFlight++ == 0)
            drained = NewSignal();
        return new Lease(Exit);
    }

    void Exit()
    {
        TaskCompletionSource? signal = null;
        lock (gate)
        {
            if (--inFlight == 0)
                signal = drained;
        }
        signal?.TrySetResult();
    }

    static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static TaskCompletionSource CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult();
        return signal;
    }

    InvalidOperationException Closed()
        => new($"插件 generation 已关闭: {PluginManager.InternalName(Plugin)}");

    sealed class Lease(Action release) : IDisposable
    {
        Action? release = release;

        public void Dispose()
            => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
