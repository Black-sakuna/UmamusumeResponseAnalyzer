# URA plugin build contract

The `UmamusumeResponseAnalyzer` NuGet package contains the Host reference assembly and imports `URA.Plugin.Build.props` and `URA.Plugin.Build.targets` through `buildTransitive`. The targets generate `manifest.json`, build the plugin ZIP, select managed NuGet runtime assets, and optionally deploy the ZIP locally.

Plugin projects reference package version `2026.9.1`. The package is compile-time only: its reference assembly and dependency branch are excluded from plugin ZIP files. Direct plugin package references continue to contribute runtime assets.

Plugin-to-plugin source dependencies remain pinned submodules. A Host source checkout is not required to build an individual plugin.

Build the compile-time package without publishing it:

```powershell
dotnet pack ..\UmamusumeResponseAnalyzer\UmamusumeResponseAnalyzer.csproj -c Release -o ..\artifacts\nuget
```
