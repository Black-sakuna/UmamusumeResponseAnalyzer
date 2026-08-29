# URA plugin build contract

Plugin repositories pin this Host repository as `deps/UmamusumeResponseAnalyzer` and import `URA.Plugin.Build.props` and `URA.Plugin.Build.targets` from that submodule. The targets reference the pinned Host source project, generate `manifest.json`, build the plugin ZIP, select managed NuGet runtime assets, and optionally deploy the ZIP locally.

Clone plugin repositories with `--recurse-submodules`. A missing Host submodule is a build error; there is no package or sibling-directory fallback.
