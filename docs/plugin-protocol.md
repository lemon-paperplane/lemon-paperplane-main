# 插件开发与协议

协议的唯一真源是 Go SDK：`lemon-paperplane-main-plugin-sdk/`（`protocol.go`、`manifest.go`、`vcs.go`、`view.go`）。本文解释怎么用。

## 1. 插件是什么

一个插件 = 一个文件夹（一个 git 仓库）：

```
lemon-paperplane-plugin-<id>/
├── plugin.json                    清单（manifest）
├── icons/*.svg                    图标（SVG 子集，见第 9 节）
├── l10n/en.json  l10n/zh-Hans.json 文案（扁平 key → 文本）
├── resources/                     运行期读取的资源（如 Gradle init script），可选
├── plugin/                        Go 包：实现逻辑，导出 func New() any
│   └── *.go *_test.go
├── cmd/paperplane-plugin-<id>/main.go   func main() { sdk.Serve(<pkg>.New()) }
├── build.sh                       go build -o bin/paperplane-plugin-<id> ./cmd/paperplane-plugin-<id>
├── go.mod                         module github.com/lemon-paperplane/lemon-paperplane-plugin-<id>
├── README.md
└── .gitignore                     bin/
```

**纯声明插件**：只贡献编辑器静态文件（`languages`、`keymaps`、`editorThemes`）的插件不写 `executable`，也没有 `plugin/`、`cmd/`、`go.mod`，目录里只有 `plugin.json`、`icons/`、`l10n/` 和 `editor/`、`keymaps/`、`themes/`。内核从不为它启动进程（对它的任何调用都按「不支持」处理）；商店里只发一个通用包，索引平台记为 `any`。

`go.mod` 在 SDK 发布 tag 之前用 replace 指向同级目录：

```
require github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk v0.0.0
replace github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk => ../lemon-paperplane-main/lemon-paperplane-main-plugin-sdk
```

插件逻辑放在 `plugin/` 包里而不是 `main` 包，是因为安装包里会把全部内置插件编进**同一个多入口二进制**（按 argv[0] 分发，省磁盘、共享内存页），每个插件仍然是独立进程。所以：

- `plugin/` 包不得有 `init()` 副作用、不得用全局可变状态（同一二进制里有别的插件代码，但进程是隔离的，所以全局状态其实安全；只是不要依赖 init 顺序）。
- 资源文件用 `//go:embed` 编进去，或运行时从 `InitializeParams.PluginDir` 读。

## 2. 生命周期

1. 内核扫描工程，发现目录里有匹配 `activation.files` 的文件，或已识别出 `activation.facets` 中的 facet，才会拉起插件。
2. 拉起后先发 `initialize`（带语言、镜像、目录、设置）。SDK 自动加载 `l10n/<locale>.json`，插件里用 `ctx.T(key, args...)` 取文案。
3. 之后按需发各种请求。插件空闲 60 秒后内核发 `shutdown`，插件进程退出；下次需要时再拉起。**不要在内存里缓存必须持久的状态**，需要持久化的写 `InitializeParams.DataDir`。
4. 请求可能被取消（`$/cancelRequest`），SDK 会取消 `ctx`；长操作要检查 `ctx.Done()`，通过 `host/exec` 启动的子进程会被内核自动终止。

## 3. 实现哪些接口

插件结构体实现下列接口中的若干个（见 `serve.go`），没实现的方法自动返回 `ErrNotSupported`：

| 接口 | 方法 | 何时调用 |
|---|---|---|
| `Detector` | `project/detect` | 扫描到激活文件的目录。只看文件，要快（< 50 ms），不要跑外部命令 |
| `Analyzer` | `module/analyze` | 导入、重新导入、文件变化后。静态解析，可读文件，尽量不跑外部命令 |
| `Syncer` | `module/sync` | 用户点「同步」或首次运行前 analyze 返回 `NeedsSync`。可以跑 Gradle/Maven，用 `TaskID` 作 Console |
| `DependencyInstaller` | `dependencies/install` | 安装依赖 |
| `RunPreparer` | `run/prepare` | 每次运行。前置步骤（编译、解析类路径）用 `host/exec` + `Console: RunID`，最后返回 `LaunchSpec` |
| `Diagnoser` | `run/diagnose` | 运行/安装/同步失败退出时，补充声明式规则做不到的诊断 |
| `FixApplier` | `fix/apply` | 用户点 `kind=custom` 的修复项 |
| `ActionInvoker` | `action/invoke` | 用户点插件声明的动作 |
| `ToolchainProvider` | `toolchain/discover|available|install` | 工具链管理 |
| `ServiceProvider` | `service/call` | 别的插件通过 `host/call` 调本插件服务 |
| `VCSProvider` | `vcs/call` | 版本控制操作（git 插件） |
| `ViewProvider` | `view/render`、`view/event` | 渲染插件界面、处理界面事件 |
| `ToolProvider` | `tool/call` | 插件声明的 API/MCP 工具被调用 |

调用顺序：detect 和 analyze 按插件依赖的拓扑序进行（java → gradle → kotlin → spring-boot），后面的插件在 `Module.Facets`、`Module.Models` 里能看到前面插件的结果。

## 4. 识别（detect）

- `DetectParams.Files` 是目录里的名字，子目录名以 `/` 结尾。
- 返回 `Facets`（`Data` 放识别时顺手得到的少量信息）、`Claims`（认领的子目录，被认领目录不再单独成模块，`"**"` 认领全部）、`Name`（模块名建议，如 package.json 的 name、Gradle 的 rootProject.name）。
- 同一目录可以被多个插件识别（Gradle + Java + Spring Boot）。
- 框架插件靠 `activation.facets` 激活（例如 vue 插件在 nodejs facet 出现时激活），读依赖声明判断。注意：带该 facet 的**每个**模块都会调用框架插件的 analyze / diagnose / 规则匹配，插件要自己判断这个模块是否真的用了本框架，没用就返回空结果。
- 框架插件影响 nodejs 生成的 npm 脚本运行配置：在自己的 facet `Data` 里给 `preferScripts`（默认脚本候选）、`scriptTitles`（显示名）、`hideScripts`（隐藏），常量见 SDK `FacetData*`。

## 5. 分析（analyze）与同步（sync）

`AnalyzeResult`：

- `Requirements`：工具链需求。`Constraint` 用 npm semver 范围语法（SDK 的 `MatchVersion` 实现了它，也兼容 PEP 440 的 `~=`、`==3.12.*`、逗号）。同一 kind 的多条需求取交集；`Purpose` 区分用途（如 gradle 的 `gradle-jvm`）。
- `RunConfigurations`：运行配置候选。`ID` 必须稳定（用 `sdk.StableID(...)`），重新导入时内核据此合并；用户改过的配置不会被覆盖。标一个 `Default`。
- `Dependencies`：依赖安装状态（`ok|missing|outdated|unknown|none`）。
- `Info`：卡片/概览上展示的关键信息，2–4 条，简短（如 `Gradle 8.12`、`Java 17`）。
- `Problems`：静态就能发现的问题（如 wrapper jar 缺失）。
- `OptionValues`：表单动态选项（如 Spring profiles）。
- `Model`：插件自己的模型（JSON），内核原样保存，下次请求时在 `Module.Models[插件id]` 里带回，依赖本插件的插件也能读到。
- `NeedsSync`：没有同步就无法运行时置 true。
- `AnalyzeParams.Env` 是工程级环境变量（用户或修复设置的）：静态问题若已被环境变量修复（如已设 `NODE_OPTIONS=--openssl-legacy-provider`），就不要再报。
- 多个插件都标了 `Default` 的运行配置时，内核优先 manifest `category` 为 `framework` 的插件的那一个。

## 6. 运行（run/prepare）

- `Toolchains` 是已为模块选定的工具链（按 kind）；`Env` 是镜像 + 工程 + 运行配置环境变量的增量。内核启动主进程时会自动把所有选定工具链的 `Env`/`PathPrepend` 和这些增量合并进去，插件只需在 `LaunchSpec.Env` 里放额外的变量。
- 前置步骤：`ctx.Host.Exec(ctx, &sdk.ExecParams{..., Console: p.RunID, Title: "编译"})`，输出实时进入这次运行的控制台；失败时返回带本地化信息的错误（`sdk.UserError`），内核会把错误显示在控制台并触发诊断。
- 不要在插件里启动长期运行的主进程，由内核托管。
- 常用环境：Python 设 `PYTHONUNBUFFERED=1`；Node 设 `FORCE_COLOR=1`；Spring Boot 加 `-Dspring.output.ansi.enabled=always`。
- `RunConfiguration.Env` 由内核合并进进程环境（也在 `PrepareRunParams.Env` 里），插件不必再放进 `LaunchSpec.Env`；运行配置选项里若另有 env 字段，由插件自己合并进 `LaunchSpec.Env`。
- 内核在进程开始监听端口时视为就绪；系统进程（如 macOS 隔空播放占用的 5000/7000）不会被当作端口冲突，也不会被结束。

## 7. 诊断规则（manifest 声明式）

```json
{
  "id": "node.openssl-legacy",
  "hint": "0308010C",
  "pattern": "error:0308010C:digital envelope routines::unsupported",
  "severity": "error",
  "title": "%diag.openssl.title%",
  "message": "%diag.openssl.message%",
  "stages": ["run"],
  "fixes": [
    {"id": "legacy", "title": "%fix.opensslLegacy%", "kind": "appendEnv", "params": {"key": "NODE_OPTIONS", "value": "--openssl-legacy-provider"}, "recommended": true},
    {"id": "node16", "title": "%fix.useNode16%", "kind": "setToolchain", "params": {"kind": "node", "constraint": "16"}}
  ]
}
```

- `hint` 是字面量预筛，**每条规则都应该给**，内核先 `strings.Contains` 再跑正则。
- 标题、说明、修复标题、确认文案、修复参数里都可用 `{1}`、`{2}` 引用捕获组。
- 同一端口的多条诊断（内核通用规则 + 插件规则）由内核合并为一条，插件的更具体会替换内核的。
- `custom` 修复在一个任务里执行：`ApplyFixParams.TaskID` 可作 `host/exec` 的 Console，长命令（装包、迁移）的输出实时可见；同时带 `Toolchains` 与 `Env`。
- 修复 kind：`setToolchain{kind,constraint}`、`setEnv{key,value}`、`appendEnv{key,value}`、`unsetEnv{key}`、`setRunOption{key,value}`、`installDependencies`、`killPort{port}`、`reimport`、`restart`、`openUrl{url}`、`custom`（交给插件 `fix/apply`）。
- 规则只对本插件参与的模块生效；`facets` 可进一步限定。

## 8. 工具链

- `toolchain/discover`：扫描已知安装位置，**只读**。`Paths` 非空时只检查给定路径（刚下载解压的目录）。返回的 `Toolchain` 必须填好 `Env`（如 `JAVA_HOME`）、`PathPrepend`（bin 目录）、`Executable`、`Source`。
- `toolchain/available`：可下载的版本列表（国内镜像优先，`InitializeParams.Mirrors`）。
- `toolchain/install`：返回 `InstallPlan`（URL 列表镜像在前、sha256、归档格式、去掉几层目录）由内核下载解压；或者插件自己装好（例如 python 调 uv）直接返回 `Toolchain`。安装目录用参数里的 `Dest`。

## 9. 图标

- 一律 SVG，禁止位图和 emoji。只允许这些元素：`svg g path circle ellipse rect line polyline polygon`；属性：`viewBox fill stroke stroke-width stroke-linecap stroke-linejoin fill-rule opacity transform`（transform 仅 `translate scale rotate matrix`）。三端各有一个小型渲染器只支持这个子集。
- `fill="currentColor"` / `stroke="currentColor"` 的图标会随主题着色；品牌图标保留品牌色。
- 内置界面图标见 `shared/icons/`（Lucide，ISC 许可），插件界面里可用 `builtin:<name>` 引用。技术品牌图标可取自 Simple Icons（CC0）。

## 10. 扩展界面（views）

manifest：

```json
"views": [
  {"id": "endpoints", "title": "%view.endpoints%", "icon": "icons/endpoints.svg", "location": "run.panel", "runState": "running"}
]
```

位置：`project.tab`（工程详情标签页）、`project.section`（工程概览区块）、`sidebar`（左侧栏全局页面）、`run.panel`（运行控制台侧栏）、`vcs.tab`（版本控制页标签）、`settings`（设置页）。

`view/render` 返回 `ViewNode` 树（类型见 `view.go`：column/row/section/tabs、heading/text/code、kv/badge/alert/stat/progress/empty、table/list/tree、button/link/form/toolbar、console）。三端用同一套原生组件渲染，外观一致。用户点按钮、提交表单、点表格行 → `view/event{event: 节点 Action, values}`；返回 `Refresh` 让界面重新 render，`State` 可在两次调用间保存视图状态。`RefreshMs` 让界面定时刷新（如端点列表）。

逃生舱：`"kind": "webview", "entry": "web/index.html"`，内核从 `/plugins/<id>/` 提供静态文件，页面里 `window.paperplane.call(op, args)` 调 API。只在声明式组件实在表达不了时用（会多占内存）。

动作返回 `Sync: true` 时内核在同一任务里执行 module/sync（如「同步 Gradle 工程」），`Reimport: true` 则重新识别分析。挂在 `run.toolbar` 的动作，`InvokeActionParams.Run` 带有触发时的运行快照（URL、端口、配置）；`run.panel` 视图的 `ViewRenderParams.Run` 同理。

动作（actions）可挂在：`project.toolbar`、`project.menu`、`card.menu`、`run.toolbar`、`vcs.toolbar`、`vcs.fileMenu`、`vcs.commitMenu`、`global.menu`。带 `form` 的动作先弹表单。

界面文案风格：标题和控件为主，不在界面上铺解释性小字；需要说明的放 `info`（显示为 i 图标，点开 popover）。

## 11. 扩展 API / MCP 工具（tools）

```json
"tools": [
  {
    "name": "actuator-health",
    "title": "%tool.health%",
    "description": "Query /actuator/health of a running Spring Boot app and return the JSON.",
    "scope": "module",
    "readOnly": true,
    "inputSchema": {"type": "object", "properties": {"runId": {"type": "string"}}}
  }
]
```

内核把它注册为操作 `spring-boot.actuator-health`，同时出现在 `POST /api/v1/call/spring-boot.actuator-health`、MCP 工具 `spring_boot_actuator_health`、`paperplane call spring-boot.actuator-health`。`scope=project|module` 时内核自动加 `projectId`/`moduleId` 参数并解析成 `ToolCallParams.Project/Module`。`description` 是写给 AI 看的，用英文写清楚用途、参数、返回。插件的 actions 也会自动通过通用操作 `project.action` 暴露给 AI。

## 12. 内核能力（host/*）

| 方法 | 说明 |
|---|---|
| `host/exec` | 执行命令。`Console` 填 runId/taskId 时输出实时进控制台；`Capture` 返回输出；取消会杀整个进程树 |
| `host/console` | 往控制台写一行系统信息（如「正在解析类路径…」） |
| `host/progress` | 任务进度 |
| `host/log` | 写 daemon 日志 |
| `host/call` | 调别的插件的服务（对方 manifest 要在 `services` 里声明） |
| `host/toolchains` | 列出内核已知的某类工具链 |
| `host/download` | 下载（可解压），带镜像回退、校验、进度 |
| `host/notify` | 弹通知 |
| `host/prompt` | 弹对话框要输入/确认（没有界面时返回 Cancelled） |

`InitializeParams.AskpassPath` 是内核提供的 askpass 程序，设给 `GIT_ASKPASS`/`SSH_ASKPASS` 后，git/ssh 需要密码时会在界面弹框。

## 13. 不改用户工程

- 内核对所有 `host/exec` 和运行进程统一注入 npm / yarn / pnpm / pip / uv / Go 的镜像环境变量（`npm_config_registry`、`YARN_REGISTRY`、`PIP_INDEX_URL`、`UV_DEFAULT_INDEX`、`UV_PYTHON_INSTALL_MIRROR`、`GOPROXY`、`GOSUMDB`、`COREPACK_NPM_REGISTRY`）。Maven/Gradle 仓库镜像和各类发行包下载镜像由插件根据 `InitializeParams.Mirrors` 自行处理。

- 只读用户文件。镜像、JDK、参数一律用环境变量、命令行参数、`--init-script`、临时 settings 文件注入，临时文件放 `CacheDir`。
- 依赖安装不改锁文件：有锁文件用 `npm ci` / `yarn install --frozen-lockfile` / `pnpm install --frozen-lockfile` / `uv sync --frozen`；没有锁文件时用不生成锁文件的方式（如 `npm install --no-package-lock`）。确实需要改用户文件的修复（如 `go mod tidy`、加 .gitignore）必须在 Fix/Action 上写 `confirm`。

## 14. 本地调试

不需要启动整个应用，用开发探针：

```bash
cd lemon-paperplane-main/lemon-paperplane-main-plugin-sdk
go build -o /tmp/pp-probe ./cmd/paperplane-plugin-probe
/tmp/pp-probe -plugins ../../lemon-paperplane-plugin-java,../../lemon-paperplane-plugin-gradle analyze <工程目录>
/tmp/pp-probe -plugins ... sync <工程目录>
/tmp/pp-probe -plugins ... run <工程目录> -exec
/tmp/pp-probe -plugins ... toolchains jdk
```

## 15. 商店包

`paperplane plugin pack <插件目录>` 生成 zip：`plugin.json`、`icons/`、`l10n/`、`resources/`、`web/`、`editor/`、`keymaps/`、`themes/`、`bin/<os>-<arch>/paperplane-plugin-<id>[.exe]`（纯声明插件没有 bin）。商店索引条目：`{id, version, engines, platforms: {"darwin-arm64": {url, sha256}, ...}}`；纯声明插件为 `platforms: {"any": {...}}`，本平台没有包时内核取 `any`。

## 补充：任何模块都能建的运行配置、代码字段、实时下拉选项（2026-11）

- `runConfigurationTypes[].anyModule: true`：此类配置在任何模块（包括没识别出技术的工程根目录）都能新建，插件不必参与识别分析，也不必声明 activation，平时不会被拉起（如内置的 `shell` 插件：「脚本 / 可执行文件」与「自定义命令」）。运行这类配置时，配置所属插件的声明式规则（diagnostics / urlPatterns / readyPatterns）照样生效。
- 表单字段 `type: "code"` + `language`（如 `shell`）：界面用 CodeMirror 输入框（编辑器工程的 `snippet.html`），带着色、行号、撤销，⌘↩ 提交。
- 表单字段 `live: true`（配合 `optionsFrom`）：选项随时会变（如已连接设备）。界面打开表单时及打开期间定期调用插件的 `options/list`（`OptionsParams{project, module, keys, env}` → `{values: {key: [Option]}}`），分析时的 `optionValues` 只作初始值。内核只调用声明了这些键的插件。
- 重新识别时，没被识别出来、但用户在里面建过运行配置（`source: "user"`）的目录会保留为普通模块，只保留用户配置。
