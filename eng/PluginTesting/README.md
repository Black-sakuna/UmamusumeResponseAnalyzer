# Plugin tests

Windows 仓根执行 `act workflow_dispatch`，通过 `.github/workflows/plugins.yml` 调用公开共用 workflow。环境要求和本地 workflow 映射见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/v1/README.md)。

`plugins.json` 固定 22 个公开插件仓的提交和项目路径。workflow 检出这些提交与递归源码依赖、检查发布边界、生成本轮 Release ZIP，再执行 `Tests/` 下 5 个跨插件 smoke 项目。`PluginSmokeTests` 从 `URA_PLUGIN_SMOKE_PACKAGE_ROOT` 指向的本轮产物目录读取 ZIP；`PluginReplaySmoke` 默认执行 parser self-test。

7 个插件专属 smoke 项目分别在 AIRedirector、LegendScenarioAnalyzer、RamenScenarioAnalyzer、SendGameStatusPlugin、SkillEffectPlugin、SkillTipsResponseAnalyzer、WinSaddleAnalyzer 的 `tests/` 下，由各插件 workflow 执行。插件测试通过 `UraTestHostRoot` 引用实际解析到的 NuGet 包对应的 Host 源码及 `SmokeHost.cs`；生产插件使用 `Version="*"` 引用最新稳定版 Host NuGet 包。

单独调试跨插件项目时，设置 `UraTestPluginSourcesRoot` 与 `URA_TEST_PLUGINS_ROOT` 为插件检出目录的绝对路径。构建传入 `GenerateUraPluginManifestOnBuild=false`、`PackageUraPluginOnBuild=false`、`DeployUraPluginToLocalAppDataOnBuild=false`；`PluginSmokeTests` 还需本轮 ZIP。对捕获语料运行 replay 时，将语料路径作为 `PluginReplaySmoke` 的命令行参数；不要提交捕获数据。
