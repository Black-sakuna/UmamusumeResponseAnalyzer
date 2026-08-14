using UmamusumeResponseAnalyzer.Plugin;
using Xunit;

namespace UmamusumeResponseAnalyzer.Tests;

[Collection("PluginReload")]
public sealed class SharedContextTests : IDisposable
{
    readonly string originalDirectory = Directory.GetCurrentDirectory();
    readonly string testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ura-manifest-dependencies-{Guid.NewGuid():N}");
    readonly string pluginsDirectory;

    public SharedContextTests()
    {
        pluginsDirectory = Path.Combine(testDirectory, "Plugins");
        Directory.CreateDirectory(pluginsDirectory);
        Directory.SetCurrentDirectory(testDirectory);
    }

    public void Dispose()
    {
        try
        {
            PluginManager.ShutdownAsync().GetAwaiter().GetResult();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            try { Directory.Delete(testDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void MissingManifestDependencyLoadsConsumerAndReportsUnavailable()
    {
        var resultPath = Path.Combine(testDirectory, "availability-result.txt");
        CreatePackage(
            "Member",
            ["Missing"],
            $$"""
            using System.IO;
            using UmamusumeResponseAnalyzer.Plugin;

            public sealed class MemberPlugin : IPlugin
            {
                public void Initialize(IPluginContext context)
                    => File.WriteAllText(@"{{resultPath.Replace("\"", "\"\"")}}", context.IsPluginAvailable("Missing").ToString());
            }
            """);

        RestartPluginManager();
        PluginManager.InitializeLoadedPlugins();

        Assert.Empty(PluginManager.FailedPlugins);
        Assert.Contains(PluginManager.LoadedPlugins, plugin => PluginManager.InternalName(plugin) == "Member");
        Assert.Equal("False", File.ReadAllText(resultPath));
    }

    [Fact]
    public void ManifestDependencyCycleFailsBeforeCreatingLoadContext()
    {
        CreatePackage("CycleA", ["CycleB"]);
        CreatePackage("CycleB", ["CycleA"]);

        var error = Assert.Throws<InvalidDataException>(RestartPluginManager);

        Assert.Contains("CycleA", error.Message, StringComparison.Ordinal);
        Assert.Contains("CycleB", error.Message, StringComparison.Ordinal);
        Assert.Contains("循环", error.Message, StringComparison.Ordinal);
        Assert.Empty(PluginManager.Contexts);
    }

    [Fact]
    public void ManifestDependencyLoadsBothPackagesInOneAssemblyLoadContext()
    {
        var resultPath = Path.Combine(testDirectory, "context-result.txt");
        CreatePackage("Anchor");
        CreatePackage(
            "Member",
            ["Anchor"],
            $$"""
            using System.IO;
            using System.Reflection;
            using System.Runtime.Loader;
            using UmamusumeResponseAnalyzer.Plugin;

            public sealed class MemberPlugin : IPlugin
            {
                public void Initialize(IPluginContext context)
                {
                    var ownContext = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!;
                    var dependency = ownContext.LoadFromAssemblyName(new AssemblyName("Anchor"));
                    var dependencyContext = AssemblyLoadContext.GetLoadContext(dependency);
                    File.WriteAllText(
                        @"{{resultPath.Replace("\"", "\"\"")}}",
                        context.IsPluginAvailable("Anchor") && object.ReferenceEquals(ownContext, dependencyContext)
                            ? "available-same"
                            : "unavailable-or-different");
                }
            }
            """);

        RestartPluginManager();
        PluginManager.InitializeLoadedPlugins();

        Assert.Empty(PluginManager.FailedPlugins);
        Assert.Equal("available-same", File.ReadAllText(resultPath));
    }

    static void RestartPluginManager()
    {
        PluginManager.ShutdownAsync().GetAwaiter().GetResult();
        PluginManager.Init();
    }

    void CreatePackage(
        string internalName,
        IReadOnlyList<string>? dependencies = null,
        string? source = null)
        => PluginCompiler.CompilePackage(
            source ?? $$"""
            using UmamusumeResponseAnalyzer.Plugin;

            public sealed class {{internalName}}Plugin : IPlugin
            {
                public void Initialize(IPluginContext context) { }
            }
            """,
            internalName,
            Path.Combine(pluginsDirectory, $"{internalName}.zip"),
            dependencies);
}
