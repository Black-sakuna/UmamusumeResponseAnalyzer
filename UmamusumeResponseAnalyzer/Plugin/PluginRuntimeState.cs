using System.Reflection;
using System.Runtime.CompilerServices;

namespace UmamusumeResponseAnalyzer.Plugin;

internal sealed class PluginRuntimeState
{
    internal Dictionary<string, PluginManager.PluginMetadata> Metadatas { get; } = [];
    internal Dictionary<string, PluginManager.PluginMetadata> AssemblyMetadatas { get; } = [];
    internal List<string> FailedPlugins { get; } = [];
    internal List<IPlugin> LoadedPlugins { get; } = [];
    internal SortedDictionary<int, List<AnalyzerRegistration>> RequestAnalyzers { get; } = [];
    internal SortedDictionary<int, List<AnalyzerRegistration>> ResponseAnalyzers { get; } = [];
    internal List<HashSet<string>> ContextGroups { get; } = [];
    internal Dictionary<string, PluginManager.PluginLoadContext> Contexts { get; } = [];
    internal Dictionary<string, Assembly> AssemblyMap { get; } = [];
    internal List<Assembly> Assemblies { get; } = [];
    internal PluginHostEvents HostEvents { get; } = new();
    internal ConditionalWeakTable<IPlugin, PluginGeneration> Generations { get; } = new();
    internal AsyncLocal<int> CallbackDepth { get; } = new();
    internal Dictionary<IPlugin, List<RouteRegistration>> Routes { get; } = new(ReferenceEqualityComparer.Instance);

    internal object AnalyzerGate { get; } = new();
    internal ReaderWriterLockSlim StateLock { get; } = new(LockRecursionPolicy.SupportsRecursion);
    internal object LifecycleGate { get; } = new();

    internal bool ReloadTransactionActive { get; set; }
    internal bool InitializationComplete { get; set; }
    internal bool StartedComplete { get; set; }
    internal bool ShuttingDown { get; set; }
    internal TaskCompletionSource? ReloadCompleted { get; set; }
    internal TaskCompletionSource ShutdownCompleted { get; set; } = CompletedTaskSource();

    static TaskCompletionSource CompletedTaskSource()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
