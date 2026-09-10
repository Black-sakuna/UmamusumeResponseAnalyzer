# Repository Guidelines

## Project Structure & Module Organization

`UmamusumeResponseAnalyzer.sln` contains the .NET 10 TUI host and its xUnit test project. Production code lives in `UmamusumeResponseAnalyzer/`: `TerminalGui/` owns host UI, workspaces, commands, and hotkeys; `Plugin/` contains plugin loading and repository behavior; `Game/` and `Entities/` hold domain views and models; `Localization/` contains `.resx` resources. `Gallop/` is protocol-compiler output—do not edit it by hand or re-enable the MessagePack source generator; the generated DTO wire contract depends on the project's custom formatters. Tests live in `UmamusumeResponseAnalyzer.Tests/`, generally mirroring a production feature in a `*Tests.cs` file.

## Build, Test, and Development Commands

Use the .NET 10 SDK from the repository root:

- `dotnet restore UmamusumeResponseAnalyzer.sln` — restore NuGet dependencies.
- `dotnet build UmamusumeResponseAnalyzer.sln -c Debug --no-restore` — compile the host and tests.
- `dotnet test UmamusumeResponseAnalyzer.Tests/UmamusumeResponseAnalyzer.Tests.csproj -c Debug --no-build` — run the full xUnit suite after building.
- `dotnet run --project UmamusumeResponseAnalyzer/UmamusumeResponseAnalyzer.csproj -- --version` — exercise a CLI-only smoke path. Do not launch the TUI from a redirected agent shell; validate TUI behavior with the existing test app and child-process helpers.

When building sibling `URA-Plugins` projects, pass `-p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false` unless packaging or deployment is explicitly under test; the default targets can overwrite installed plugin archives.

Cross-plugin smoke projects live in `eng/PluginTesting/Tests/`; `eng/PluginTesting/plugins.json` pins the public plugin commits. Run `act workflow_dispatch` on Windows to execute `.github/workflows/plugins.yml`. Plugin-owned tests live in the corresponding plugin repository. Details: [plugin test workflows](eng/PluginTesting/README.md).

## Coding Style & Naming Conventions

Production source follows `UmamusumeResponseAnalyzer/.editorconfig`; mirror it in tests: four-space indentation, CRLF line endings, Allman braces, `using` directives outside namespaces, and `var` for locals. Use PascalCase for types, methods, and properties; prefix interfaces with `I`; use camelCase for locals and parameters. Prefer direct, readable C# and existing modern language patterns over new wrappers or speculative abstractions. Update `.resx` sources rather than generated `*.Designer.cs` files.

## Testing Guidelines

Tests use xUnit v3 with `[Fact]` and `[Theory]`. Name test classes `FeatureTests` and methods as clear PascalCase behavior statements. Add tests for observable behavior and key invariants; no numeric coverage threshold is configured. For Terminal.Gui work, reuse the existing test app and child-process lifecycle helpers, and assert public state, real input, or framebuffer-visible output. Run a focused class with `dotnet test UmamusumeResponseAnalyzer.Tests/UmamusumeResponseAnalyzer.Tests.csproj --filter "FullyQualifiedName~WorkspaceLifecycleTests"` before the full suite.

## Commit & Pull Request Guidelines

Recent history favors `type(scope): summary`, for example `fix: ...`, `test(workspace): ...`, or `refactor(terminal-gui): ...`; keep each commit focused. PRs should explain the behavior change and rationale, link relevant issues, and list exact validation commands. Include screenshots or framebuffer evidence for visible TUI changes. Update `README.md` and localization resources when user-facing behavior or text changes.

## Runtime Data & Security

Runtime configuration, plugins, data files, and captured packets belong under `%LocalAppData%\UmamusumeResponseAnalyzer` or a disposable `.portable/` directory. Do not commit local configuration, plugin binaries, packet captures, credentials, or private endpoints.
