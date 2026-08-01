# UmamusumeResponseAnalyzer

UmamusumeResponseAnalyzer 是基于 Terminal.Gui 的本地 TUI 宿主。它接收游戏请求/响应的 MessagePack payload，按 Gallop endpoint catalog 分发给已安装插件；插件 live state 显示在 workspace，notification 由 Host overlay 呈现，interactive Host session 中捕获的异常集中记录到启动 workspace。

# 前置 Prerequisite

* 任意可以把游戏请求/响应 MessagePack payload 发送到宿主 `/notify/request` / `/notify/response` 的 sender。请求必须带 `X-Hachimi-Game-Url` header，值为游戏原始 canonical URL；该 URL 的 path 必须命中 Gallop endpoint catalog，或能在带/不带 `/umamusume` 前缀两种形式之间切换后命中 catalog。推荐 [ura-core](https://github.com/UmamusumeResponseAnalyzer/ura-core)。
* sender 的目标地址默认设置为 `http://127.0.0.1:4693`。如果游戏在手机或其他设备上运行，首次运行向导可把监听地址改为 `0.0.0.0`；启动时按控制台提示放行防火墙。
* Windows 版主菜单提供 `自动安装ura-core`。该入口会查找本机游戏目录，选择安装 Hachimi 或 umamusume-localify，并在启用 DLL redirection 时请求管理员权限。
* (可选，如果需要脱离 DMM 启动游戏) DMM Game Player β 及 HTTPS proxy，比如 [Fiddler](https://www.telerik.com/fiddler/fiddler-classic) 或 [mitmproxy](https://mitmproxy.org/)。

# 安装 Installation

* 在 [Release](https://github.com/EtherealAO/UmamusumeResponseAnalyzer/releases) 页面下载最新版本程序。
* 将程序放在任意位置，运行 `UmamusumeResponseAnalyzer.exe`。
* 普通启动要求 stdin 和 stdout 连接到 interactive terminal；redirected stdin/stdout 会以明确错误退出。`--version`、`--update`、`--update-data`、`--enable-dll-redirection` 等 CLI-only 路径不启动 TUI。
* 首次运行按向导选择运行设备、服务器目标(日服 Cygames / 繁中服 Komoe)、事件数据语言和训练员性别。
* 返回主菜单后先选择 `更新数据文件`。数据文件用于事件、技能、名称等本地解析；缺失或损坏时程序会保留空集合并提示更新。
* 进入 `插件仓库`，安装需要的功能插件。没有插件时宿主仍会启动，并提供启动信息、异常记录、通知和基础分发能力。
* 选择 `启动！` 后会立即进入 Terminal.Gui 的全屏启动 workspace；数据加载、插件初始化和 HTTP server 启动在后台推进。启动 workspace 在整个 Host session 内持续存在，显示运行环境、初始化结果、插件摘要和最近日志；Host 捕获的异常写入最近日志，但不会切换当前 workspace。异常行可右键打开 context menu，`复制完整 backtrace` 会将完整异常链和 stack trace 写入系统 clipboard；普通日志不提供该操作。四个区域的尺寸只随 viewport 变化；超出区域的表格与日志使用 Terminal.Gui 原生滚动条，内容增长不改变 panel 布局。启动状态以紧凑结果行实时更新，不显示额外 header 或 footer。

# 运行与文件位置

* 默认工作目录为 `%LocalAppData%\UmamusumeResponseAnalyzer`。启动时如果当前目录存在 `.portable` 文件夹，工作目录会改为 `./.portable`。
* `config.yaml`、数据文件、`Plugins/` 和 debug `packets/` 都写在工作目录下。
* 默认监听 `http://127.0.0.1:4693`。`/notify/ping` 返回 `pong`，可作为 smoke test。
* 启动、设置和插件设置使用占满 terminal 且无外边框的 Terminal.Gui 原生 `Menu` 页面；mouse hover 或 `Up` / `Down` 移动当前菜单项，click 或 `Enter` 直接激活。插件仓库、更新、安装 Mod 和字段编辑使用各自的 dialog，完成后返回对应的上级菜单。启动后按 `/`，或在 focused control 未使用 `Enter` 时，打开 Command Mode；内置命令需以 `/` 开头。Command Mode 使用 Terminal.Gui 原生 `TextField` 输入，支持 `Tab` 补全、`Up` / `Down` 浏览当前进程内提交过的命令、`Esc` 取消和 `Enter` 执行。底部全宽带框 overlay 显示 ` Command Mode ` 标题、补全候选及 `+N more`、`❯` 输入区和操作 footer；窄窗口或高度不足时使用紧凑布局。鼠标移到 terminal 最底部会显示覆盖在 workspace 上的居中 taskbar popup，不占用 workspace 布局空间；过长标题会随 terminal 宽度以 `…` 缩略，当前项和 hovered 项优先显示完整标题。click workspace title 直接切换，按住左键左右拖拽可调整并持久化 taskbar 顺序；该顺序只影响 taskbar。当前项和 hover 由背景属性区分。Command Mode 显示时 taskbar 被抑制，并拥有更高的图层优先级。`/workspace` 或 `/workspace switch` 打开 workspace 选择器，`/workspace switch <title>` 直接切换；标题也可用双引号包围，其中 `\"` 和 `\\` 分别表示双引号和反斜杠。`/workspace list` 列出 workspace。workspace 内容超出终端时默认显示底部；`Up` / `Down` 按行滚动，`PageUp` / `PageDown` 按页滚动，`Home` / `End` 跳到顶部或底部。`Left` / `Right` 不参与 workspace viewport 滚动；focused view 未处理时继续匹配已注册 hotkey。Terminal.Gui 主界面中，滚轮产生与无 modifier 的 `Up` / `Down` 相同的 workspace viewport 滚动效果；滚轮不触发快捷键，popup 或 Command Mode 存活时会被忽略。Host dialog 的 `TextField` 与 `ListView` 使用 Terminal.Gui 原生 whole-view hover；focus 视觉优先于 hover，hover 不改变 focus、selection 或 marked 状态。`Button`、菜单及插件提供的 `View` 使用各控件自身的 Terminal.Gui 默认行为。workspace/plugin popup 保持 mouse click-through。popup 或 Command Mode 存活时由其优先处理按键；workspace 已到边界时按键继续交给已注册 hotkey。`/plugin` 或 `/plugin list` 列出插件运行期状态，`/plugin load|unload|reload <InternalName>` 只改变当前进程内加载状态，不安装、不删除插件文件、不写禁用配置。插件更新 workspace panel 时默认会切到该 workspace；bootstrap 刷新和异常写入不会切换当前 workspace。按 `Ctrl+B` 返回启动 workspace，按 `P` 查看已加载插件列表，按 `Ctrl+C` 退出程序。
* 程序启动后会检查已加载插件是否有新版本；发现更新时只通知，不自动安装。更新插件需要进入 `插件仓库` 手动选择。
* 更新数据文件时会先写入临时文件，下载成功后替换目标文件；失败时清理临时文件并保留已有文件。
* 开启 debug packet 保存后，请求写为 `Q`、响应写为 `R` 的 `.msgpack` 文件，文件名包含 API endpoint path（`/` 写为 `-`）；`DEBUG` 构建额外写 `.json`，文件名只包含时间戳和 `Q`/`R`，完整 canonical URL 写在 JSON 内容中。`packets/` 中超过一天的旧文件会在下次保存时清理，单个旧文件清理失败不会中断当前请求/响应分析。

# 检查安装 Checking

* 浏览器或命令行访问 `http://127.0.0.1:4693/notify/ping`，返回 `pong` 说明宿主 HTTP server 已启动。
* 启动游戏后，前往殿堂马列表、竞技场选择对手或查看好友信息。若 workspace 中出现插件输出，说明 sender、header 和插件分发配置正确。

# 插件仓库与 URACloud

* `插件仓库` 从 `https://ura.shuise.net/api/Plugins` 拉取插件目录，并按配置中的服务器目标过滤插件。插件自身未声明 `Targets` 时视为所有目标可用。
* 仓库安装会下载 ZIP 到 `Plugins/<InternalName>.zip`，随后尝试热重载。标记为 `[LoadInHostContext]` 或进入宿主上下文的插件需要重启才能生效。
* 同一 `InternalName` 的不同作者 fork 会在仓库列表中同时保留；本地同一时间只能安装其中一个来源。
* URACloud 网页集成挂在本地 `/uracloud/*`。`/uracloud/status` 返回当前 URA 版本和已加载插件；`/uracloud/install` 只接受 `{author, internalName, version}`，下载源固定为 URACloud 插件仓库，并且安装前必须在本机控制台确认。

# 插件开发 Plugin Development

* 插件直接引用宿主程序集与 Terminal.Gui。宿主公开 `IPlugin`、`AnalyzerAttribute`、workspace UI contract 和 Gallop DTO/endpoint catalog；插件源码使用 `UmamusumeResponseAnalyzer.Plugin`、`UmamusumeResponseAnalyzer.TerminalGui`、`Terminal.Gui.ViewBase`、`Terminal.Gui.Views`、`Gallop`、`Gallop.Endpoints` 命名空间。
* 插件可以放在 `Plugins/` 下：DLL 会递归扫描，ZIP 只扫描 `Plugins/` 顶层。ZIP 主插件 DLL 放在压缩包根目录，程序集名等于 ZIP 文件名；同级其它 DLL 作为依赖加载；卫星资源 DLL 可放在对应 culture 子目录。
* 宿主加载插件时按程序集名作为 internal name。插件 `Targets` 为空、命中配置的 repository targets，或配置未设置 targets 时，才会注册 analyzer 和路由。
* 宿主提供基础 `TurnInfo` / `CommandInfo` 领域视图。UAF、L'Arc、Cook、Mecha、Legend、Pioneer、Onsen、Breeders 等场景专用聚合模型由场景插件基于 Gallop DTO 派生。
* 请求/响应 analyzer 使用 endpoint attribute，例如 `[ResponseAnalyzer<GameApi.Account.Index>] ValueTask Analyze(DataLinkIndexResponse response)`；唯一 payload 参数为 `byte[]` 时收到原始 MessagePack payload，其他 payload 参数类型必须精确匹配 Gallop descriptor 的 request/response DTO。analyzer 可声明第二参数 `GameHttpHeaders headers` 接收 sender 转发的游戏 HTTP header；缺失 header 为 `null`。同一个方法可以挂多个 analyzer attribute，但这些 attribute 必须要求同一个 payload 参数类型。
* 插件也可以在 `Initialize(IPluginContext context)` 中通过 `context.Analyzers.RegisterRequest(...)` / `context.Analyzers.RegisterResponse(...)` 程序化注册 analyzer；raw handler 使用单泛型 overload，DTO handler 使用双泛型 overload，二参 handler 的第二参数为 `GameHttpHeaders`。
* analyzer handler 返回 `ValueTask`。动态注册返回的 `IDisposable` 可注销对应 analyzer；注销只影响后续分发。
* 宿主按 `X-Hachimi-Game-Url` header 中的 canonical game URL 解析 path，并在带/不带 `/umamusume` 前缀两种形式之间查询 `GameEndpointCatalog.ByPath`；两种形式均未命中的数据会被静默丢弃。sender 可附带 `X-Hachimi-sid`、`X-Hachimi-app-ver`、`X-Hachimi-res-ver`、`X-Hachimi-viewerid`、`X-Hachimi-device`、`X-Hachimi-device-subtype`；raw analyzer 收到的是原始 MessagePack payload bytes。
* 分发以 raw payload 为基础；DTO analyzer 在执行点按 Gallop descriptor 反序列化，同一分发中的 DTO analyzer 共享反序列化结果。raw analyzer 和 DTO analyzer 都按 priority 顺序执行。
* 插件 HTTP route 使用 `[Route]` attribute。方法签名必须是 `Task Handler(HttpContextBase ctx)`，最终路径为 `/<PluginName>/<RoutePath>`。
* 插件必须实现 `Initialize(IPluginContext context)`；`IPlugin` 没有 updater/progress 入口。`context.Application` 是宿主进程唯一的 `IApplication`，插件不得自行创建或释放 Terminal.Gui application。插件通过 `Workspace.Create(title)` 获取 workspace，`Workspace.Current` 读取当前 workspace；`SetPanel`、`RemovePanel`、`Log`、`Notify`、`SwitchTo`、`BindHotkey` 和 `Remove` 均为 `Workspace` 实例方法。`SetPanel(key, title, content, fullBleed, switchToWorkspace)` 接受 `WorkspaceContent`，默认在 panel 更新时切到目标 workspace；静默刷新传 `switchToWorkspace: false`。`WorkspaceContent` 的 factory 每次返回一个未挂载的新 `Terminal.Gui.ViewBase.View`，View 的挂载和释放由宿主管理；纯文本可用 `WorkspaceContent.Text(...)`。`Workspace.Log` / `Notify` 写入对应 workspace scope；Host 的 public `TerminalUi.Log` / `Notify` 始终写入 global scope。日志按 global/workspace scope 存储，每个 scope 保留最新 300 条；普通 workspace 只显示 panel live state，不渲染日志。notification 按现有 scope 由 Host overlay 显示。interactive Host session 中，`TerminalUi.LogException` 捕获的异常写入启动 workspace 的“最近日志”区域，且不会切换 active workspace。插件 hotkey 通过 `HotkeyManager` 注册。`context.Events.OnStarted(...)` 用于订阅宿主启动事件。`Initialize` 抛异常时，宿主记录插件失败并清理该插件的 analyzer、route、事件订阅和快捷键归属；热重载或卸载插件时也会做同样清理。
* `Notify(..., shortcuts: UiShortcut[])` 注册 notification TTL 内的临时快捷键；`HotkeyContext.BindShortcut(...)` 注册 popup 存活期间的临时快捷键。临时 handler 可重复触发，不关闭 popup、notification，也不延长 TTL；Command Mode 激活后由 focused `TextField` 处理输入，其他按键依次交给 popup shortcut、popup built-in、最新 TTL-live notification shortcut、workspace navigation 和持久 hotkey，仍未处理的 `/` 或 `Enter` 再打开 Command Mode。notification 未显示在 active workspace、窄屏或 overflow 中时，TTL 内的快捷键仍然有效。临时快捷键不写入持久 hotkey dictionary，也不自动渲染按键提示；workspace 删除、宿主停止和插件卸载会清理对应注册。
* `Workspace.Create(title)` 原样保留 title（包括边界空白），纯空白 title 非法；进程内以 `OrdinalIgnoreCase` 将 title canonicalize 为全局共享的 `Workspace`，所有调用方取得同一实例。`Workspace.Current` 返回当前 workspace。普通 workspace 只显示各 panel 的当前 live state；同一 workspace 内的 panel key 以 `Ordinal` 区分并由所有调用方共享，`SetPanel(...)` 替换同 key panel，`RemovePanel(key)` 删除同 key panel并返回是否存在。`Workspace.Remove()` 清除该 workspace 的 panel、滚动位置、日志、notification/临时快捷键和 workspace hotkey；删除 active workspace 时回退到首个剩余 workspace，没有剩余项时 `Workspace.Current` 为 `null`。相同 title 此后再次 `Workspace.Create(...)` 会取得新的 generation；旧 handle 成为 tombstone，除重复 `Remove()` 外，`SetPanel`、`RemovePanel`、`Log`、`Notify`、`SwitchTo` 和 `BindHotkey` 均立即抛出 `InvalidOperationException`。
* 热重载按 internal name 应用；共享上下文插件会按上下文组一起重载。进入宿主上下文的插件不能卸载，只能通过重启应用新版本。
* 插件配置由插件自行维护。宿主不预创建插件数据目录，不自动读写配置文件，也不提供通用属性编辑器；菜单入口调用 `ConfigPromptAsync(IApplication application, CancellationToken cancellationToken = default)`，其中 `application` 与 `context.Application` 为同一个宿主实例。插件需要配置或数据目录时，应在插件代码中创建目录、读取/校验自己的文件，用户取消时不得写入 draft，错误直接抛出明确异常。
