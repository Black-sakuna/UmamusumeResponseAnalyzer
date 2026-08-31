# URA plugin build contract

Plugin repositories pin this Host repository as `deps/UmamusumeResponseAnalyzer` and import `URA.Plugin.Build.props` and `URA.Plugin.Build.targets` from that submodule. The targets reference the pinned Host source project, generate `manifest.json`, build the plugin ZIP, select managed NuGet runtime assets, and optionally deploy the ZIP locally.

Plugin project references use direct edges and inherit the parent configuration and platform. The Host path selector is excluded from the Host project's MSBuild identity, so one Host project instance is built per configuration even when plugins reference other plugins.

Clone plugin repositories with `--recurse-submodules`. A missing Host submodule is a build error; there is no package or sibling-directory fallback.
