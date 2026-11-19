# 内核 API（HTTP / MCP / CLI）

数据结构的唯一真源：`lemon-paperplane-main-core/internal/model/model.go` 与插件 SDK（`sdk.*` 类型）。本文列出全部操作。

## 1. 连接

- daemon 启动后写 `$DATA/runtime.json`（权限 0600）：

```json
{"port": 47213, "token": "<随机 32 字节 hex>", "pid": 12345, "version": "0.1.0", "executable": "/Applications/Lemon Paperplane.app/Contents/MacOS/paperplane"}
```

- 只监听 `127.0.0.1`。所有请求带 `Authorization: Bearer <token>`；Host 头必须是 `127.0.0.1:<port>` 或 `localhost:<port>`；带 `Origin` 头的请求一律拒绝（浏览器跨站防护），插件 webview 例外（见第 6 节）。
- 界面启动流程：读 runtime.json → `POST /api/v1/call/system.info` → 失败则执行 `paperplane daemon --detach` 后重试（最多 5 秒）。macOS 包内路径 `Contents/MacOS/paperplane`，内置插件在 `Contents/Resources/plugins/`。

## 2. 调用

```
POST /api/v1/call/<op>
Content-Type: application/json
{ ...输入... }

200 {"result": <输出>}
4xx/5xx {"error": {"code": "<错误码>", "message": "<已本地化的说明>"}}
```

错误码：`bad_request`、`not_found`、`conflict`、`unauthorized`、`plugin_error`、`not_supported`、`cancelled`、`user_error`（message 可直接展示）、`internal`。

`GET /api/v1/ops` 返回全部操作：`[{name, title, description, readOnly, destructive, inputSchema}]`（JSON Schema），包括插件贡献的工具。

## 3. 事件流（SSE）

- `GET /api/v1/events`：`text/event-stream`，每条 `data: {"type": "...", "data": ...}`。类型见 `model.go` 的 `Ev*` 常量。连上后先收到一条 `hello`（data 为 SystemInfo）。每 15 秒一条 `: ping` 注释。
- `GET /api/v1/terminals/<terminalId>/stream?since=<字节偏移>`：交互终端的输出流。先发 `event: state`（Terminal），再补发缓冲里偏移 `since` 之后还留着的输出（每个终端保留最后 1 MB），之后实时推送。每条 `data: {"o": 起始偏移, "d": base64 原始字节}`（字节流，UTF-8 可能跨条切开，按字节写进终端模拟器）；终端退出或重新启动时发 `event: state`；终端关闭时发 `event: closed`。连接断开（跟不上会被断开）后带上已收到的末尾偏移重连即可续上。
- `GET /api/v1/consoles/<consoleId>/stream?since=<seq>`：某个运行（consoleId = runId）或任务（taskId）的日志流。先补发 `seq > since` 的缓冲行，再实时推送。每条 `data: {"lines": [LogLine...]}`（批量，最多 100ms 合并一次）。控制台结束后发 `event: end`。

## 4. 图标引用

界面上的 `icon` 字段是一个引用：

- `builtin:<name>`：`shared/icons/<name>.svg`（界面内置）。
- `plugin:<pluginId>/<相对路径>`：通过 `GET /api/v1/plugins/<pluginId>/files/<相对路径>` 取 SVG（需 token），界面按 pluginId+版本缓存。

## 5. 操作清单

输入里 `projectId` 等字段为必填，除非标注「可选」。输出类型见 model.go / SDK。

### 系统与设置

| 操作 | 输入 | 输出 |
|---|---|---|
| `system.info` | – | `SystemInfo` |
| `system.shutdown` | `{stopRuns: bool}` | – |
| `settings.get` | – | `Settings` |
| `settings.update` | `Settings` 的任意子集（JSON merge） | `Settings` |
| `mirrors.status` | – | `{configured, effective, probes: [{name, url, ms, ok}]}` |
| `mirrors.probe` | – | 同上（重新测速） |
| `ui.contributions` | – | `Contributions`（全部插件的界面贡献，已本地化；插件变化后重新取） |
| `ui.respond` | `{promptId, cancelled, values}` | – |
| `ui.prompts` | – | `[Prompt]`（尚未回答的对话框，界面重连时补弹） |
| `cli.install` | `{dir 可选}` | `{path}`（把 `paperplane` 链接到 PATH） |
| `mcp.config` | – | `{url, token, stdioCommand: [..], claudeCode: "<命令>", claudeDesktop: {...}}` |

### 工作区

| 操作 | 输入 | 输出 |
|---|---|---|
| `workspace.list` | – | `[Workspace]` |
| `workspace.create` | `{name}` | `Workspace` |
| `workspace.rename` | `{workspaceId, name}` | `Workspace` |
| `workspace.delete` | `{workspaceId, moveTo 可选}` | –（不删磁盘文件；不 moveTo 则移除其工程） |
| `workspace.reorder` | `{workspaceIds}` | – |

### 工程

| 操作 | 输入 | 输出 |
|---|---|---|
| `project.list` | `{workspaceId 可选}` | `[ProjectSummary]`（按工作区内顺序） |
| `project.get` | `{projectId}` | `Project` |
| `project.scan` | `{path, maxDepth 可选=2}` | `[{path, name, facets: [FacetBadge], imported: bool, projectId}]`（批量导入前预览） |
| `project.import` | `{path, workspaceId, name 可选}` | `Project`（识别+分析完成后返回；需要同步的会自动开同步任务） |
| `project.importMany` | `{paths, workspaceId}` | `[Project]` |
| `project.remove` | `{projectId}` | –（不删磁盘文件） |
| `project.rename` | `{projectId, name}` | `ProjectSummary` |
| `project.move` | `{projectId, workspaceId, index 可选}` | – |
| `project.reorder` | `{workspaceId, projectIds}` | – |
| `project.reimport` | `{projectId}` | `Task` |
| `project.sync` | `{projectId, moduleId 可选}` | `Task` |
| `project.settings` | `{projectId, env 可选, toolchains 可选: {kind: ToolchainOverride 或 null}}` | `Project` |
| `project.open` | `{projectId, moduleId 可选, with: finder\|terminal\|idea\|vscode\|default}` | – |
| `project.problems` | `{projectId}` | `[Diagnosis]`（静态问题 + 缺工具链 + 依赖未装） |
| `project.action` | `{projectId, moduleId, plugin, action, args 可选, runId 可选}` | `Task`（run.toolbar 动作传 runId，插件会拿到这次运行的快照） |
| `deps.install` | `{projectId, moduleId 可选, clean 可选}` | `Task` |

### 运行配置

| 操作 | 输入 | 输出 |
|---|---|---|
| `runconfig.types` | `{projectId, moduleId}` | `[{type: RunConfigTypeInfo, optionValues}]`（可新建的类型，表单选项已解析） |
| `runconfig.create` | `{projectId, moduleId, type, name, options, env}` | `RunConfig` |
| `runconfig.update` | `{projectId, configId, name 可选, options 可选, env 可选}` | `RunConfig`（标记 edited） |
| `runconfig.delete` | `{projectId, configId}` | – |
| `runconfig.reset` | `{projectId, configId}` | `RunConfig`（恢复插件生成的版本） |
| `runconfig.setDefault` | `{projectId, configId}` | – |

### 运行

| 操作 | 输入 | 输出 |
|---|---|---|
| `run.start` | `{projectId, configId 可选（默认配置）}` | `Run`（同一配置已在运行则返回它） |
| `run.stop` | `{runId}` | `Run` |
| `run.restart` | `{runId}` 或 `{projectId, configId}` | `Run`（新的） |
| `run.stopAll` | `{workspaceId 可选, projectId 可选}` | `{stopped: n}` |
| `run.list` | `{projectId 可选, active 可选}` | `[Run]`（含最近结束的） |
| `run.get` | `{runId}` | `Run` |
| `run.logs` | `{consoleId, tail 可选=200, since 可选, grep 可选, stream 可选}` | `{lines: [LogLine], lastSeq, total, truncated}`（AI 读日志用） |
| `run.input` | `{runId, text}` | –（写 stdin，自动补换行） |
| `run.clear` | `{runId}` | – |
| `fix.apply` | `{runId + diagnosisId, 或 taskId + diagnosisId（安装/同步任务的诊断）, 或 projectId + diagnosisId; fixId}` | `{message, openUrl, rerun: Run, task: Task}`（custom 修复在任务里执行，task 为该任务） |
| `port.kill` | `{port}` | `{pids}`（结束占用端口的进程） |

### 启动面板

工作区「启动」页：用户从各工程挑出要一起跑的运行配置，按工程分组，可一键全部运行/停止。启动项存在工程上（`Project.launch`，按加入顺序），工程换工作区时跟着走；运行配置被删除时自动移出。`ProjectSummary.launch` 只含仍存在的配置。

| 操作 | 输入 | 输出 |
|---|---|---|
| `launch.list` | `{workspaceId 可选, projectId 可选}` | `[{projectId, projectName, path, items: [{configId, name, type, plugin, moduleId, moduleName, run: RunBrief}]}]`（run 为最近一次，进行中优先） |
| `launch.add` | `{projectId, configIds}` | `{launch: [configId]}`（已在里面的忽略；配置不存在报 not_found） |
| `launch.remove` | `{projectId, configIds}` | `{launch: [configId]}` |
| `launch.addFile` | `{projectId, path}`（相对工程根） | `{configId, moduleId, name, created, launch}`：为脚本/可执行文件建「脚本 / 可执行文件」运行配置（同一文件已有就复用）并加入启动；文件不在任何模块里时放进工程根目录的普通模块 |
| `launch.addCommand` | `{projectId, name 可选, command, cwd 可选, shell 可选}` | 同上：建「自定义命令」运行配置（可多行，用默认 shell 执行）并加入启动；cwd 工程内存相对路径，工程外存绝对路径 |
| `runconfig.options` | `{projectId, moduleId, keys}` | `{key: [Option]}`：实时取 live 下拉选项（如 ios.devices / android.devices），界面打开运行配置期间每 4 秒刷新 |
| `launch.run` | `{workspaceId 可选, projectId 可选, configIds 可选}` | `{started: [Run], skipped, errors: [{projectId, configId, error}]}`（已在运行的跳过） |
| `launch.stop` | `{workspaceId 可选, projectId 可选, configIds 可选}` | `{stopped, errors}` |

`launch.setAlias` `{projectId, configId, alias}`：给启动项起别名（只在「启动」里显示，运行配置本身的名字不变；空字符串清除，最长 80 字）。别名在 `ProjectSummary.launchAlias`（configId → 别名）；移出启动时一并删除。

### 终端

工作区「终端」页：像 VS Code 一样开若干个交互终端。每个终端是伪终端里的登录 shell（`$SHELL -l`，读用户的 .zprofile/.zshrc），起始目录是所选工程的根目录，带上工程环境变量与镜像变量，`TERM=xterm-256color`。终端只在 daemon 内存里：界面关掉再开还在，daemon 退出时全部挂断。shell 以退出码 0 结束（输入 exit）时终端自动关闭；非 0 时保留并标记已退出，可以重新启动。变化时推 `terminals.changed`（data 为全部终端）。暂不支持 Windows。

| 操作 | 输入 | 输出 |
|---|---|---|
| `terminal.list` | `{workspaceId 可选}` | `[Terminal]`：`{id, workspaceId, projectId, name, cwd, shell, pid, cols, rows, startedAt, exited, exitCode, output}`（output 为累计输出字节数） |
| `terminal.create` | `{projectId 或 workspaceId+cwd, cwd 可选（相对工程根）, name 可选, cols, rows}` | `Terminal`（名字默认工程名，同工作区重名加序号） |
| `terminal.input` | `{id, data}` | –（原样写入，回车用 `\r`，Ctrl-C 用 `\u0003`） |
| `terminal.read` | `{id, maxBytes 可选=16384}` | `{text}`：最近输出的纯文本（去掉颜色、光标控制，按回车还原行），给 AI / 命令行看 |
| `terminal.resize` | `{id, cols, rows}` | – |
| `terminal.rename` | `{id, name}` | – |
| `terminal.restart` | `{id}` | `Terminal`（已退出的终端在原目录重新启动，输出接在后面） |
| `terminal.busy` | `{id}` | `{busy, process}`：是否有命令在前台运行（不是 shell 在等输入）及其名字，关闭前确认用 |
| `terminal.close` | `{id}` | –（给 shell 所在进程组发 SIGHUP，2 秒后还在就强制结束） |

### 任务

| 操作 | 输入 | 输出 |
|---|---|---|
| `task.list` | `{active 可选}` | `[Task]` |
| `task.get` | `{taskId}` | `Task` |
| `task.cancel` | `{taskId}` | – |

### 工具链

| 操作 | 输入 | 输出 |
|---|---|---|
| `toolchain.kinds` | – | `[ToolchainKind]` |
| `toolchain.list` | `{kind 可选}` | `[Toolchain]` |
| `toolchain.available` | `{kind}` | `[AvailableVersion]` |
| `toolchain.install` | `{kind, version}` | `Task` |
| `toolchain.add` | `{kind, path}` | `Toolchain`（手动登记） |
| `toolchain.remove` | `{toolchainId}` | –（Paperplane 下载的会删除文件；其他只是不再显示） |
| `toolchain.refresh` | – | – |

### 插件

| 操作 | 输入 | 输出 |
|---|---|---|
| `plugin.list` | – | `[PluginInfo]` |
| `plugin.enable` / `plugin.disable` | `{pluginId}` | – |
| `plugin.reload` | – | –（重新扫描插件目录与 manifest，开发时用） |
| `plugin.install` | `{path}`（zip 或目录） | `PluginInfo` |
| `plugin.uninstall` | `{pluginId}` | –（内置插件只能禁用；用户版卸载后回退到内置版） |
| `plugin.settings` | `{pluginId, values}` | – |
| `store.list` | `{refresh 可选}` | `[StoreEntry]` |
| `store.install` | `{pluginId}` | `Task` |

商店索引：设置里的 `storeIndexUrl`（在线商店填 `https://<商店服务>/api/public/v1/plugin/index.json`，服务端在 lemon-paperplane-store），没填时用随 App 附带的离线索引（Resources/store/index.json）。读在线索引时自动带上 `?locale=<界面语言>`；索引条目带 `versions`（新到旧）时，内核挑第一个和本机内核版本（engines.paperplane）、平台都兼容的版本作为 `version/platforms`，所以新版本要求更高的客户端时，旧客户端仍能装到它能用的版本。
| `view.render` | `{plugin, view, projectId 可选, moduleId 可选, runId 可选, state 可选}` | `sdk.ViewRenderResult` |
| `view.event` | `{plugin, view, event, values, projectId 可选, moduleId 可选, runId 可选, state 可选}` | `sdk.ViewEventResult` |
| `<pluginId>.<tool>` | 插件声明的 inputSchema（scope=project/module 时加 `projectId`/`moduleId`） | 插件返回 |

### 版本控制

全部操作的输入都是 `{projectId, root 可选, ...参数}`：`root` 为仓库绝对路径（工程内有多个仓库时必填，默认工程主仓库），其余字段即 SDK `vcs.go` 中对应 `VCS*Args` 的字段（平铺）。输出为对应的 SDK 类型。

| 操作 | 参数 | 输出 |
|---|---|---|
| `vcs.roots` | – | `[VCSRoot]` |
| `vcs.overview` | `{workspaceId 可选}`（不需要 projectId） | `[{projectId, projectName, root, relPath, name, vcs, status: VCSStatus, error}]`：工作区内全部工程的全部仓库（去重）及状态 |
| `vcs.status` | – | `VCSStatus` |
| `vcs.diff` | `VCSDiffArgs` | `VCSDiff` |
| `vcs.commit` | `VCSCommitArgs` | `VCSCommitResult` |
| `vcs.push` / `vcs.pull` / `vcs.fetch` | 对应 Args | `VCSOpResult`（远程操作同时生成 Task，输出在任务控制台） |
| `vcs.log` / `vcs.fileHistory` | 对应 Args | `VCSLog` |
| `vcs.show` | `{hash}` | `VCSCommitDetail` |
| `vcs.branches` | – | `VCSBranches` |
| 其余 | 见 `vcs.go` 的 `VCSOp*` 常量，操作名为 `vcs.<op>` | |

远程操作需要凭据时，内核发 `prompt` 事件（askpass），界面弹框后 `ui.respond`。

## 6. 插件静态文件与 webview

- `GET /api/v1/plugins/<pluginId>/files/<path>`：插件目录下的 `icons/`、`web/`、`resources/` 内文件。
- webview 类型的视图：`view.render` 不调插件，直接返回 `{"webviewUrl": "http://127.0.0.1:<port>/plugins/<pluginId>/<entry>?token=…&projectId=…&moduleId=…&runId=…"}`，界面用内嵌浏览器加载；页面可用同源 `fetch('/api/v1/call/<op>', {headers: {Authorization: 'Bearer ' + token}})` 调 API（token 从地址参数取）。

## 7. MCP

- Streamable HTTP：`POST http://127.0.0.1:<port>/mcp`（同样需要 Bearer token）。
- stdio：`paperplane mcp`（自动读 runtime.json，必要时拉起 daemon）。Claude Code 里：`claude mcp add paperplane -- "<paperplane 路径>" mcp`。
- 工具 = 全部操作（不含 `ui.*`、`system.shutdown`）：名字把 `.` 和 `-` 换成 `_`，如 `project_list`、`run_start`、`vcs_commit`、`spring_boot_apps`。只读操作带 `readOnlyHint`，破坏性操作带 `destructiveHint`。

## 8. CLI

```
paperplane daemon [--detach]          启动内核
paperplane mcp                         MCP stdio 桥
paperplane ls [工作区]                  列出工程与状态
paperplane import <路径> [-w 工作区]
paperplane run <工程> [配置]            启动并跟随日志（Ctrl-C 只退出跟随，不停止）
paperplane stop <工程|runId>
paperplane logs <工程|runId> [-f] [-n 200]
paperplane call <op> ['<json>']        调任意操作
paperplane ops                         列出全部操作
paperplane plugin pack <插件目录>       打商店包
```
