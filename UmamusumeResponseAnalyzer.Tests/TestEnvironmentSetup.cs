using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace UmamusumeResponseAnalyzer.Tests;

static class TestEnvironmentSetup
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries")]
    internal static void Initialize()
        => Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");
}
