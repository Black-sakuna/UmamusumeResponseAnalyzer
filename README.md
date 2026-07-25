# UmamusumeResponseAnalyzer

UmamusumeResponseAnalyzer 是基于 Terminal.Gui 的本地 TUI 宿主。它接收游戏请求/响应的 MessagePack payload，按 Gallop endpoint catalog 分发给已安装插件，并把插件输出、日志和通知显示在同一个 workspace 界面中。

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
* 进入 `插件仓库`，安装需要的功能插件。没有插件时宿主仍会启动，但只提供日志、通知和基础分发能力。
* 选择 `启动！` 后程序会启动内置 HTTP server，并进入 Terminal.Gui 界面；启动 workspace 显示配置摘要、数据加载、插件扫描/初始化和监听状态。

# 运行与文件位置

* 默认工作目录为 `%LocalAppData%\UmamusumeResponseAnalyzer`。启动时如果当前目录存在 `.portable` 文件夹，工作目录会改为 `./.portable`。
* `config.yaml`、数据文件、`Plugins/` 和 debug `packets/` 都写在工作目录下。
* 默认监听 `http://127.0.0.1:4693`。`/notify/ping` 返回 `pong`，可作为 smoke test。
* 启动菜单使用 Terminal.Gui 原生 `PopoverMenu`、纵向 `Menu` 和级联 `MenuItem.SubMenu`。`Up` / `Down` 移动当前菜单项，鼠标 hover 会移动 focus 并展开对应 SubMenu，`Right` 进入 SubMenu，`Left` 返回父层；click 或 `Enter` 直接激活 leaf。设置中的 bool 直接使用菜单内的原生 `CheckBox`，文本与多选设置分别打开 `TextField` 和 `MultiSelect` dialog。启动后按 `/` 或 `Enter` 打开命令输入栏；内置命令需以 `/` 开头。命令输入栏支持 `Tab` 补全，`Up` / `Down` 浏览当前进程内提交过的命令。`/workspace` 或 `/workspace switch` 打开 workspace 选择器，`/workspace switch <title>` 直接切换；标题也可用双引号包围，其中 `\"` 和 `\\` 分别表示双引号和反斜杠。`/workspace list` 列出 workspace。workspace 内容超出终端时默认显示底部；`Up` / `Down` 按行滚动，`PageUp` / `PageDown` 按页滚动，`Home` / `End` 跳到顶部或底部，`Left` / `Right` 浏览整体快照历史并逐步回到 live。Terminal.Gui 主界面中，滚轮产生与无 modifier 的 `Up` / `Down` 相同的 workspace viewport 滚动效果；滚轮不触发快捷键，popup 或命令输入栏存活时会被忽略。Host dialog 的 `TextField` 与 `ListView` 使用 Terminal.Gui 原生 whole-view hover；focus 视觉优先于 hover，hover 不改变 focus、selection 或 marked 状态。`Button`、菜单及插件提供的 `View` 使用各控件自身的 Terminal.Gui 默认行为。workspace/plugin popup 与 Command Mode overlay 保持 mouse click-through。popup 或命令输入栏存活时由其优先处理按键；workspace 已到边界时按键继续交给已注册 hotkey。`/plugin` 或 `/plugin list` 列出插件运行期状态，`/plugin load|unload|reload <InternalName>` 只改变当前进程内加载状态，不安装、不删除插件文件、不写禁用配置。插件更新 workspace panel 时默认会切到该 workspace；浏览历史期间，live 更新不会改变当前 workspace 或历史位置。按 `Ctrl+B` 查看启动信息，按 `P` 查看已加载插件列表，按 `Ctrl+C` 退出程序。
* 程序启动后会检查已加载插件是否有新版本；发现更新时只通知，不自动安装。更新插件需要进入 `插件仓库` 手动选择。
* 更新数据文件时会先写入临时文件，下载成功后替换目标文件；失败时清理临时文件并保留已有文件。
* 开启 debug packet 保存后，请求写为 `Q`、响应写为 `R` 的 `.msgpack` 文件，文件名包含 API endpoint path（`/` 写为 `-`）；`DEBUG` 构建额外写 `.json`，文件名只包含时间戳和 `Q`/`R`，完整 canonical URL 写在 JSON 内容中。`packets/` 中超过一天的旧文件会在下次保存时清理，单个旧文件清理失败不会中断当前请求/响应分析。

# 检查安装 Checking

* 浏览器或命令行访问 `http://127.0.0.1:4693/notify/ping`，返回 `pong` 说明宿主 HTTP server 已启动。
* 启动游戏后，前往殿堂马列表、竞技场选择对手或查看好友信息。若 workspace 中出现插件输出或相关日志，说明 sender、header 和插件分发配置正确。

# 插件仓库与 URACloud

* `插件仓库` 从 `https://ura.shuise.net/api/Plugins` 拉取插件目录，并按配置中的服务器目标过滤插件。插件自身未声明 `Targets` 时视为所有目标可用。
* 仓库安装会下载 ZIP 到 `Plugins/<InternalName>.zip`，随后尝试热重载。标记为 `[LoadInHostContext]` 或进入宿主上下文的插件需要重启才能生效。
* 同一 `InternalName` 的不同作者 fork 会在仓库列表中同时保留；本地同一时间只能安装其中一个来源。
* URACloud 网页集成挂在本地 `/uracloud/*`。`/uracloud/status` 返回当前 URA 版本和已加载插件；`/uracloud/install` 只接受 `{author, internalName, version}`，下载源固定为 URACloud 插件仓库，并且安装前必须在本机控制台确认。

# 插件开发 Plugin Development

* 插件直接引用宿主程序集与 Terminal.Gui。宿主公开 `IPlugin`、`AnalyzerAttribute`、workspace UI contract 和 Gallop DTO/endpoint catalog；插件源码使用 `UmamusumeResponseAnalyzer.Plugin`、`UmamusumeResponseAnalyzer.LiveDisplay`、`Terminal.Gui.ViewBase`、`Terminal.Gui.Views`、`Gallop`、`Gallop.Endpoints` 命名空间。
* 插件可以放在 `Plugins/` 下：DLL 会递归扫描，ZIP 只扫描 `Plugins/` 顶层。ZIP 主插件 DLL 放在压缩包根目录，程序集名等于 ZIP 文件名；同级其它 DLL 作为依赖加载；卫星资源 DLL 可放在对应 culture 子目录。
* 宿主加载插件时按程序集名作为 internal name。插件 `Targets` 为空、命中配置的 repository targets，或配置未设置 targets 时，才会注册 analyzer 和路由。
* 宿主提供基础 `TurnInfo` / `CommandInfo` 领域视图。UAF、L'Arc、Cook、Mecha、Legend、Pioneer、Onsen、Breeders 等场景专用聚合模型由场景插件基于 Gallop DTO 派生。
* 请求/响应 analyzer 使用 endpoint attribute，例如 `[ResponseAnalyzer<GameApi.Account.Index>] ValueTask Analyze(DataLinkIndexResponse response)`；唯一 payload 参数为 `byte[]` 时收到原始 MessagePack payload，其他 payload 参数类型必须精确匹配 Gallop descriptor 的 request/response DTO。analyzer 可声明第二参数 `GameHttpHeaders headers` 接收 sender 转发的游戏 HTTP header；缺失 header 为 `null`。同一个方法可以挂多个 analyzer attribute，但这些 attribute 必须要求同一个 payload 参数类型。
* 插件也可以在 `Initialize(IPluginContext context)` 中通过 `context.Analyzers.RegisterRequest(...)` / `context.Analyzers.RegisterResponse(...)` 程序化注册 analyzer；raw handler 使用单泛型 overload，DTO handler 使用双泛型 overload，二参 handler 的第二参数为 `GameHttpHeaders`。
* analyzer handler 返回 `ValueTask`。动态注册返回的 `IDisposable` 可注销对应 analyzer；注销只影响后续分发。
* 宿主按 `X-Hachimi-Game-Url` header 中的 canonical game URL 解析 path，并要求 path 命中 `GameEndpointCatalog.ByPath`；未命中时会在带/不带 `/umamusume` 前缀两种形式之间切换后再查 catalog。sender 可附带 `X-Hachimi-sid`、`X-Hachimi-app-ver`、`X-Hachimi-res-ver`、`X-Hachimi-viewerid`、`X-Hachimi-device`、`X-Hachimi-device-subtype`；raw analyzer 收到的是原始 MessagePack payload bytes。
* 分发以 raw payload 为基础；DTO analyzer 在执行点按 Gallop descriptor 反序列化，同一分发中的 DTO analyzer 共享反序列化结果。raw analyzer 和 DTO analyzer 都按 priority 顺序执行。
* 插件 HTTP route 使用 `[Route]` attribute。方法签名必须是 `Task Handler(HttpContextBase ctx)`，最终路径为 `/<PluginName>/<RoutePath>`。
* 插件必须实现 `Initialize(IPluginContext context)`；`IPlugin` 没有 updater/progress 入口。`context.Application` 是宿主进程唯一的 `IApplication`，插件不得自行创建或释放 Terminal.Gui application。`context.LiveDisplay` 提供 workspace 输出。`SetPanel(workspace, key, title, content, fullBleed, switchToWorkspace)` 接受 `LiveDisplayContent`，默认在 panel 更新时切到目标 workspace；静默刷新传 `switchToWorkspace: false`。`LiveDisplayContent` 的 factory 每次返回一个未挂载的新 `Terminal.Gui.ViewBase.View`，View 的挂载和释放由宿主管理；纯文本可用 `LiveDisplayContent.Text(...)`。`Log`、`Notify` 的无 workspace overload 在调用时使用 `CurrentWorkspace`，没有当前 workspace 时抛出异常；Host 的 public `LiveDisplayConsole.Log` / `Notify` 始终写入 global scope，界面只组合 global 与 active workspace 的日志和通知，每个日志 scope 保留最新 300 条。`context.Events.OnStarted(...)` 用于订阅宿主启动事件。`Initialize` 抛异常时，宿主记录插件失败并清理该插件的 analyzer、route、事件订阅和快捷键归属；热重载或卸载插件时也会做同样清理。
* `Notify(..., shortcuts: LiveDisplayShortcut[])` 注册 notification TTL 内的临时快捷键；`KeyboardHandlerContext.BindShortcut(...)` 注册 popup 存活期间的临时快捷键。临时 handler 可重复触发，不关闭 popup、notification，也不延长 TTL；按键优先级为 Command Mode、popup shortcut、最新 TTL-live notification shortcut、popup built-in、workspace navigation、持久 hotkey。notification 未显示在 active workspace、窄屏或 overflow 中时，TTL 内的快捷键仍然有效。临时快捷键不写入持久 hotkey dictionary，也不自动渲染按键提示；workspace 删除、宿主停止和插件卸载会清理对应注册。
* `CreateWorkspace(title, historyCapacity)` 原样保留 title（包括边界空白），以 `OrdinalIgnoreCase` 判定 workspace 身份；纯空白 title 非法。`historyCapacity` 只计算 FIFO 已提交快照，live state 不占槽位。`CaptureWorkspaceSnapshot(...)` 显式保存当时该 workspace 的全部 panels，并浅持有各 panel 的 `LiveDisplayContent` factory；日志和 notification 不进入快照。`RemoveWorkspace(...)` 同步清除该 workspace 的 panel、历史/滚动位置、日志、notification/临时快捷键和 workspace hotkey；删除 active workspace 时回退到首个剩余 workspace，没有剩余项时为 `null`。后续写入已删除 workspace 的内容会被忽略。
* 热重载按 internal name 应用；共享上下文插件会按上下文组一起重载。进入宿主上下文的插件不能卸载，只能通过重启应用新版本。
* 插件配置由插件自行维护。宿主不预创建插件数据目录，不自动读写配置文件，也不提供通用属性编辑器；菜单入口调用 `ConfigPromptAsync(IApplication application, CancellationToken cancellationToken = default)`，其中 `application` 与 `context.Application` 为同一个宿主实例。插件需要配置或数据目录时，应在插件代码中创建目录、读取/校验自己的文件，用户取消时不得写入 draft，错误直接抛出明确异常。
