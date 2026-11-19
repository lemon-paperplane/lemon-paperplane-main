# Windows 版移植约定

目标：把 macOS 版（`../lemon-paperplane-main-macos/`）**一比一**搬到 Windows：同样的功能、布局、配色、图标、文案、交互。Mac 源码就是需求说明；`../docs/design-system.md` 是三端共用的设计规范；`../docs/api.md` 是内核接口。遇到 Mac 版和规范不一致的地方，以 Mac 版的实际行为为准。

## 1. 技术选型（已定，不要改）

- WPF + .NET Framework 4.8（`TargetFrameworkVersion v4.8`），C# 最新语法（`LangVersion latest`，可空引用开启）。新语法缺的类型在 `build/Polyfills.cs`（`init`、`required`、`^1`、`a..b` 等；**数组**切片 `arr[a..b]` 在 net48 上编不过，用 `Skip/Take` 或 `Array.Copy`）。
- **老式 csproj**（非 SDK 风格），编译用 Visual Studio Build Tools 的 MSBuild（开发机上是挂载的 EWDK）。`Compile Include="**\*.cs"`，新建文件不用改工程文件。
- **纯代码写界面，不用 XAML 编译**（没有 `.xaml` Page、没有 `App.xaml`）。原因：编译期检查、和 SwiftUI 的写法对得上、绑定写错不会静默失效。只有控件模板（TextBox、ScrollBar 这类必须用模板的）可以写成 `LemonPaperplane/Design/Templates/*.xaml`，作为嵌入资源在运行时 `XamlReader.Load`。
- 第三方库只有：`Newtonsoft.Json`（JSON）、`Microsoft.Web.WebView2`（只给编辑窗口、终端、插件 webview、命令输入框用），都放在 `Vendor/` 随仓库提交。托盘图标用框架自带的 `System.Windows.Forms.NotifyIcon`。不要再引入别的包（没有 MVVM 框架、没有 UI 库、没有测试框架）。
- 一个程序集一个命名空间：`PaperplaneKit`（不依赖 WPF 的纯逻辑，对应 Mac 的 PaperplaneKit）、`LemonPaperplane`（界面）。用 file-scoped namespace。

## 2. 目录

```
lemon-paperplane-main-windows/
├── build.ps1                    编译（-Release、-Test、-Filter）
├── build/Common.props           共用编译设置
├── build/Polyfills.cs           新语法需要的类型（每个程序集编一份）
├── Vendor/                      Newtonsoft.Json、WebView2
├── PaperplaneKit/               ↔ macos/PaperplaneKit/Sources/PaperplaneKit（同名子目录：Models API Daemon ANSI SVG Design I18n Util Graph）
├── PaperplaneKit.Tests/         ↔ macos/PaperplaneKit/Tests（自带极简运行器：[Test] 方法 + Assert；夹具直接引用 Mac 的 Fixtures）
└── LemonPaperplane/             ↔ macos/LemonPaperplane（同名子目录：App Core Design Components Renderers Features/<区域>）
```

**文件一一对应**：Mac 的 `Features/VCS/VCSLogView.swift` → `LemonPaperplane/Features/VCS/VCSLogView.cs`。类型名、方法名尽量和 Swift 一致（改成 C# 大小写习惯），方便对照。

## 3. 数据与接口（PaperplaneKit）

- JSON：Newtonsoft。全局设置在 `PaperplaneKit/Util/Json.cs`：属性按 camelCase、**字典键不改大小写**（环境变量、插件模型等）、序列化忽略 null、`DateParseHandling.None`（动态 JSON 里的日期保持字符串）。
- 模型：Swift 的 `struct` → C# `class`（属性 PascalCase，集合默认空、不为 null）；Swift 里用字符串原始值的枚举 → `string` + 常量类（内核以后可能加新值，不能因为不认识就解析失败）；`JSONValue` → `JToken`。
- `ApiClient.CallAsync<T>(op, args)` 调 `POST /api/v1/call/<op>`；Mac `Operations.swift` 里的每个包装方法都有同名 C# 方法（`api.Workspaces()` → `api.WorkspacesAsync()`）。错误抛 `ApiException(Code, Message)`。
- 事件流、控制台流、终端流（SSE）在 `PaperplaneKit/API/SSE.cs`、`Streams.cs`；回调在后台线程，界面层自己切回 UI 线程。
- `HttpClient` 对同一主机默认只有 2 个连接：启动时 `ServicePointManager.DefaultConnectionLimit = 64`。

## 4. 界面约定（LemonPaperplane）

- **颜色只用设计变量**：画刷是 `Application.Resources` 里的动态资源，键名 = `design-tokens.json` 的颜色键（如 `bgContent`、`textSecondary`）。代码里一律 `SetResourceReference`（封装好的扩展方法），不写死颜色；切换深浅色只换资源字典，界面不用重建。
- **字号、间距、圆角、尺寸**用 `M.*`（对应 Mac 的 `M`），字体层级用 `Typo.*`（对应 `.pp(.title)`）。
- **文案**一律 `L.T("key", args)`，key 在 `../shared/i18n/*.json`；界面上不出现硬编码中文或英文。切换语言时重建主窗口内容。Windows 上说法不同的（访达 → 资源管理器、废纸篓 → 回收站、⌘ → Ctrl…）放在 `../shared/i18n/windows/*.json`，加载时覆盖同名 key；需要新的 Windows 说法就往这里加（两种语言都加）。
- **图标**一律 `../shared/icons/*.svg`（经 SVG 子集渲染器），不用 Segoe 图标字体、不用 emoji。
- **快捷键**：Mac 的 ⌘ → Ctrl，⌥ → Alt。
- **视图写法**：一个 SwiftUI View → 一个 C# 类（通常继承 `PPView`，在 `Build()` 里返回元素树；订阅的状态变化时合并重建）。含输入框的区域要拆成独立视图，别让重建把正在输入的框换掉。长列表用虚拟化（`VirtualizingStackPanel`），控制台、差异、提交图这类大量文本用自绘。
- **线程**：只在 UI 线程改 store；`async` 调接口后回到 UI 线程（WPF 的 `await` 默认会回到 Dispatcher）。
- **弹出层**：菜单、弹出框、下拉用 WPF `Popup`（可以超出窗口）；对话框、提示是主窗口内的覆盖层（和 Mac 一样窗口内模态）。
- **窗口**：自绘标题栏（`WindowChrome`），40px 工具栏同时是标题栏，最小化/最大化/关闭在右侧（Windows 11 样式；最大化按钮悬停出贴靠布局）；关闭主窗口 = 收到托盘，服务继续运行。

具体组件 API 见 `LemonPaperplane/Components/` 与 `Design/`（第 7 节记录）。

## 5. 在 Windows 上编译、测试

日常在 Mac 上写代码，在一台 Windows 编译机上编译和测试（OpenSSH 服务 + 免密登录）：

- 编译：`build.ps1`（自己找 MSBuild 和 4.8 参考程序集）；`build.ps1 -Test` 跑 PaperplaneKit.Tests。编译机上可以是 Visual Studio / Build Tools，也可以是挂载的 EWDK（自带 MSBuild 18 和最新 Roslyn）。
- 内核：用 `../scripts/build-windows-runtime.sh <输出目录> amd64`（或在 Windows 上 `go -C ../scripts/ppbuild run . runtime …`）交叉编译 Windows 版内核 + 内置插件，拷到编译机；界面用环境变量 `PAPERPLANE_BIN` 指向其中的 `paperplane.exe`，`PAPERPLANE_DATA_DIR` 隔离数据目录（不要用默认数据目录测试）。
- **界面能在 ssh 会话里跑**（没有可见桌面，软件渲染）：窗口照常创建，`RenderTargetBitmap` 能把窗口画成 PNG，用调试桥（第 6 节）截图、导航、点击。
- **但 ssh 会话里起的进程继承了 sshd 的「重定向保护」**，打不开普通用户建的目录联接（例如 uv 的 `cpython-3.12-…`），和真实环境不一样：测内核、插件时用计划任务（`schtasks /IT` + `conhost --headless`）把内核起在用户的桌面会话里。WebView2（编辑窗口、终端）、真实鼠标和托盘也要在桌面会话里测。
- ssh 命令结束时 sshd 会结束它派生的进程：一次测试的启动、操作、截图放在同一条命令里。
- PowerShell 5.1 按系统代码页读没有 BOM 的 `.ps1`：仓库里的 `.ps1` 必须带 UTF-8 BOM；给原生程序传 JSON 参数要把双引号转义成 `\"`。
- 发布包：仓库根目录的 `build.cmd`（在 Windows 上）或 `../scripts/build-windows.sh`（在 Mac 上远程构建），见 README。

## 6. 调试桥（自动化测试用）

设了环境变量 `PAPERPLANE_DEBUG_BRIDGE=<管道名>` 启动时，界面开一个命名管道 `\\.\pipe\<管道名>`，每行一个 JSON 命令、回一行 JSON。客户端：`LemonPaperplane.exe --debug-client <管道名> '<json>'`。命令见 `LemonPaperplane/App/DebugBridge.cs`（截图 `snapshot`、元素树 `tree`、按自动化 ID 或文字点击 `click`、键盘 `key`、导航 `nav`、主题 `theme`、窗口大小 `resize`…）。截图会把打开着的 Popup 一起合成进去。

## 7. 组件 API 速查

见 `LemonPaperplane/Components/README.md`（每个组件 Mac 写法 → C# 写法、PPView、布局辅助、主题、图标、文案、对话框、提示、主窗口插槽、调试桥、已占用的类型名）。组件陈列页 `App/Gallery.cs` 就是用法示例。

## 7.1 界面骨架（已完成，功能页直接用）

- `Core/AppModel.cs`：`AppModel.Shared`——`Connection`、`SystemInfo`、`Api`/`Client()`、`Data`(DataStore)、`Runs`(RunStore)、`Terminals`、`Nav`(NavStore)、`Dialogs`、`Toasts`、`Bus`(EventBus：ProjectChanged / VcsChanged / ToolchainsChanged / PluginsChanged / Raw)、`Perform(title, async api => …)`（失败弹提示）、`Track(task)`（结束时提示）、`ReloadToolchains()`、`ReloadPlugins()`、`Quit(stopServices)`。
- `Core/Stores.cs`：store 都是 `PPObservable`，用 `Observe(store, nameof(DataStore.Projects))` 订阅。修改走方法（`Data.PutProject`、`Runs.PutRun`、`Runs.PutTask`、`Nav.OpenProject(id, ProjectTab.Run)`…），不要直接改字典。导航：`Nav.Page = NavPage.WorkspacePage(id)` / `NavPage.Running` / `NavPage.SettingsPage`…；工程详情标签 `ProjectTab.*`。
- `Core/Actions.cs`：和 Mac `Actions` 同名的静态方法（Run / Stop / Restart / Reimport / Sync / InstallDeps / Rename / Remove / Move / RevealInExplorer / OpenURL / Copy / OpenFile / CreateWorkspace / ShowTaskLog / ApplyFix / Invoke / PickModule / Applicable / MenuEntries / OpenWithEntries）。
- `Core/PromptPresenter.cs`、`Features/Main/*`（MainView、PageRouter、Sidebar、工具栏、运行中 / 任务弹出框）、`Features/MenuBar/Tray.cs`（托盘）、`App/AppCommands.cs`（快捷键）。
- 模型名 `Run` 和 WPF 的 `System.Windows.Documents.Run` 重名：界面文件里写 `using RunModel = PaperplaneKit.Run;`。

## 7.2 功能页分工与占位

多人并行时每人在自己的工作副本里改，只改下表里自己那一行的路径。别人的东西用占位类的公开签名（类名、构造参数、静态方法）调用，**不要改占位类的签名**；需要改共用文件（`Core/`、`Components/` 里别人的组件、`Design/`、`shared/i18n`、csproj）时在自己的副本里改到最少，并在汇报里逐条列出，由集成的人合并。

| 负责人（远端名） | 负责的路径（相对 LemonPaperplane/） | 对外的占位 / 入口（签名不要改） |
|---|---|---|
| 工作区与全局页（pp-ws） | `Features/Workspace/*`、`Features/Import/*`、`Features/Resources/*`、`Features/Toolchains/*`、`Features/Plugins/*`、`Features/Settings/*` | `WorkspacePage(string? workspaceId)`、`DiagnosisRow(diagnosis, runId, projectId, onFixed)`、`ResourceStore.Shared`（Apply / RunChanged / Prune）、`RunResourceText`、`RunResourceInline`、`RunResourcePanel(Run)`、`ImportFlow.ImportFolder/ScanFolder`、`ToolchainsPage`、`PluginsPage`、`SettingsPage` |
| 工程详情、运行、启动、表单与插件视图（pp-proj） | `Features/Project/*`、`Features/Launch/*`、`Components/Console.cs`、`Renderers/FormRenderer.cs`、`Renderers/ViewNodeRenderer.cs` | `ProjectDetailPage(string projectId)`、`LaunchBoardPage(workspaceId)`、`LaunchGroupData.ActiveCount(…)`、`ConsolePanel(consoleId, runId, logFile, compact, leading)`、`Dialogs.Form(…)`、`FormView`、`PluginViewHost(view, context)` |
| 版本控制（pp-vcs） | `Features/VCS/*` | `VCSWorkspaceSwitcher(workspaceId, Func<UIElement> projects)`、`VCSTabView(projectId)`、`VCSHooks.CloneAction` |
| 文件、编辑窗口、终端、WebView2（pp-files） | `Features/Files/*`、`Features/Editor/*`、`Features/Terminal/*`、`Components/CodeInput.cs`、`Renderers/WebviewHost.cs`；以及 `../../lemon-paperplane-main-editor/`（编辑器网页要同时支持 WebView2，Mac 不能坏） | `FilesTab(Project)`、`EditorWindows.Shared`（Open / PrepareForQuit / RequestClose / IsEmpty）、`TerminalPage(workspaceId)`、`PPCodeInput(…)`、`WebviewHost(Uri, ViewContext)` |

## 8. 进度

| 区域 | 状态 |
|---|---|
| 内核 Windows 部分（Job Object、Ctrl+C 停止、toolhelp32、端口表、ConPTY、系统语言、注册表环境、打开方式、回收站、命令行工具加 PATH） | 完成，Windows 上测试通过 |
| PaperplaneKit（数据层 + 设计变量 / SVG / 文案） | 完成，83 个测试通过 |
| 设计层、组件库、主窗口外壳、调试桥、App 图标 | 完成 |
| AppModel / Stores / Actions / 主窗口 / 托盘 | 完成 |
| 工作区、导入、资源监控、工具链、插件、设置 | 完成，已合并 |
| 工程详情、运行、控制台、启动、表单、插件视图 | 完成，已合并（控制台 20 万行压测界面线程最长 9ms） |
| 版本控制 | 完成，已合并（差异、日志图虚拟化自绘） |
| 文件、编辑窗口、终端、WebView2、代码输入框、插件网页视图 | 完成，已合并（编辑器网页同时支持 WKWebView 与 WebView2） |
| 远程服务器（docs/remote.md 第 5 节：左上角切换器、添加服务器、设置 → 服务器、选择服务器上的文件夹、运行地址转发 / 直连、远程时隐藏本机操作；`Core/Servers.cs`、`Features/Servers/*`） | 完成，Windows 上连本机另起的服务器内核测过（连接码 / 账号密码、导入运行、端口转发、吊销后 403、服务器断开与恢复、本机内核重启后重开网关） |
| 打包（安装包 + zip，x64 / ARM64） | 根目录 `build.cmd`，新电脑也能直接出包 |

已知还没在真实鼠标 / 桌面上验证的：卡片与工作区拖动排序、系统选择文件夹对话框弹出、贴靠布局弹出、托盘浮窗位置、多显示器 DPI。
