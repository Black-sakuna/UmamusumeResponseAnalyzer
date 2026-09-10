# URA plugin build contract

The `UmamusumeResponseAnalyzer` NuGet package contains the Host reference assembly and imports `URA.Plugin.Build.props` and `URA.Plugin.Build.targets` through `buildTransitive`. The targets generate `manifest.json`, build the plugin ZIP, select managed NuGet runtime assets, and optionally deploy the ZIP locally.

Plugin projects use `Version="*" PrivateAssets="all"` to reference the latest stable package without propagating the compile-time package into consuming test projects. The shared workflow refreshes dependency resolution before building and uses the resolved package's repository commit for the test Host. Compilation and test failures stop the workflow. The package is compile-time only: its reference assembly and dependency branch are excluded from plugin ZIP files. Direct plugin package references continue to contribute runtime assets.

Plugin-to-plugin source dependencies remain pinned submodules. A Host source checkout is not required to build an individual plugin.

Build the compile-time package without publishing it:

```powershell
dotnet pack ..\UmamusumeResponseAnalyzer\UmamusumeResponseAnalyzer.csproj -c Release -o ..\artifacts\nuget
```
