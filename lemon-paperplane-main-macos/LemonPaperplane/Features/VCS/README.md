# 版本控制界面接入说明（Mac）

「版本控制」标签页的完整界面在本目录实现。目前只有占位 `VCSTabView.swift`（接收 `projectId`），工程详情页已经把它挂在「版本控制」标签下，替换它的 `body` 即可。

工程使用 Xcode 文件夹同步组：往 `LemonPaperplane/` 下任何子文件夹加 `.swift` 文件都会自动编进 App，不用改 `project.pbxproj`。本文件（`.md`）已在工程里排除，不会拷进包。

先读：`docs/design-system.md`（颜色、尺寸、组件清单）、`docs/api.md` 第 5 节「版本控制」、SDK `vcs.go`（数据结构）。

## 必须遵守

- 只用设计系统组件和 SVG 图标（`PPIcon("git-branch")`），不用系统默认控件外观、SF Symbols、emoji。
- 全部文案走 `tr("key")`，key 同时加进 `shared/i18n/zh-Hans.json` 和 `shared/i18n/en.json`（`swift test` 会检查两边 key 一致）。建议统一用 `vcs.*` 前缀。
- 界面上不铺解释性小字；需要说明的用 `PPInfoButton(text:)`。
- 轻量：日志图、文件列表用 `LazyVStack`；差异只渲染当前文件；不要 WebView。

## 数据与调用

API 客户端：`AppModel.shared.client()`（`throws`，未连接时抛出可展示的错误）或 `AppModel.shared.api`（可选）。`PaperplaneKit/API/Operations.swift` 里已有类型化封装：

```swift
let api = try AppModel.shared.client()
let roots  = try await api.vcsRoots(projectId: pid)                       // [VCSRoot]
let status = try await api.vcsStatus(projectId: pid, root: rootPath)      // VCSStatus
let diff   = try await api.vcsDiff(projectId: pid, VCSDiffArgs(path: "a.txt", context: 3))
let log    = try await api.vcsLog(projectId: pid, VCSLogArgs(ref: "--all", limit: 200))
let detail = try await api.vcsShow(projectId: pid, hash: h)               // VCSCommitDetail
let br     = try await api.vcsBranches(projectId: pid)
let r      = try await api.vcsCommit(projectId: pid, VCSCommitArgs(message: m, paths: files, push: false))
let p      = try await api.vcsPush(projectId: pid, VCSPushArgs(setUpstream: true))   // VCSOpResult
// 其余操作用通用方法：op 取 VCSOp.* 常量，args 是 vcs.go 对应的 *Args（全部已在 PaperplaneKit/Models/VCSModels.swift）
let res: VCSOpResult = try await api.vcs(VCSOp.stashSave, projectId: pid, root: rootPath, args: VCSStashSaveArgs(message: "wip"))
let stashes: VCSStashList = try await api.vcs(VCSOp.stashList, projectId: pid)
```

- 输入是 `{projectId, root 可选, ...Args 平铺}`，`vcs(...)` 已经帮你平铺。工程内有多个仓库时 `root` 必填（`vcs.roots` 或 `Project.vcsRoots`）。
- 远程操作（push / pull / fetch / clone / submoduleUpdate / mergeTool）的 `VCSOpResult.taskId` 是任务 id：
  - 进度会出现在工具栏的任务指示器里；你可以 `AppModel.shared.track(task)` 让它结束时自动提示（需要 `TaskInfo`：`try await api.task(taskId)`）。
  - 输出：`ConsolePanel(consoleId: taskId, runId: nil, logFile: nil, compact: true)`，或 `Actions.showTaskLog(taskId)` 弹出任务日志。
- 需要凭据时 daemon 发 `prompt` 事件，主界面已经统一弹框并 `ui.respond`（`type: "password"` 字段渲染为密码框），你不用处理。
- `VCSOpResult.conflicts` 非空、`needsAuth`、`rejected`（推送被拒，需要先拉取）请在界面上给出对应的下一步按钮。

## 状态与事件

- `AppModel.shared.data.projects[pid]?.vcs`：卡片用的 `VCSBrief`（分支、领先/落后、改动数、冲突、进行中的操作）。
- `AppModel.shared.bus.vcsChanged`（Combine `PassthroughSubject<VCSChangedEvent, Never>`）：仓库变化（`{projectId, root}`），收到后重新取 `vcs.status` 等：

```swift
.onReceive(AppModel.shared.bus.vcsChanged.filter { $0.projectId == projectId }) { _ in Task { await model.reload() } }
```

- `AppModel.shared.bus.projectChanged`：工程摘要变化（工程 id）。
- 当前工程详情：`AppModel.shared.nav.openProjectId`；切到版本控制标签：`AppModel.shared.nav.projectTab = .vcs`。
- 插件贡献的 `vcs.tab` 视图：`AppModel.shared.data.contributions.views(at: ViewLocation.vcsTab)`，用 `PluginViewHost(view:context:)` 渲染；动作 `actions(at: ActionLocation.vcsToolbar / .vcsFileMenu / .vcsCommitMenu)`，调 `Actions.invoke(action, projectId:moduleId:)`。

## 可用组件（详见 design-system.md 第 7 节）

| 需要 | 用 |
|---|---|
| 主题色、语气色、提交图车道色 | `@Environment(\.theme) var theme`；`theme.tone("danger")`；`theme.graphLane(i)`；差异色 `theme.diffAddedBg` 等 |
| 尺寸、字体 | `M.rowHeight`、`M.md`…；`.font(.pp(.mono))` |
| 按钮、图标按钮、链接 | `PPButton`、`PPIconButton`、`PPLink` |
| 输入、多行、下拉、复选框、开关、分段、标签栏 | `PPTextField`、`PPTextArea`、`PPDropdown`、`PPCheckbox`、`PPSwitch`、`PPSegmented`、`PPTabBar` |
| 列表行、分栏、区块、卡片、徽标、空状态、提示条 | `PPRow`、`PPSplitView`、`PPSection`、`PPCard`、`PPBadge`/`PPCountBadge`、`PPEmptyState`、`PPAlert` |
| 菜单、右键菜单、弹出框 | `PPMenuButton(entries:)`、`.ppContextMenu { [MenuEntry] }`（`.item` / `.submenu` / `.divider` / `.header`）、`.ppPopover(isPresented:)` |
| 对话框 | `Dialogs.confirm(...)`、`Dialogs.input(...)`、`Dialogs.form(title:fields:...)`；自定义：`AppModel.shared.dialogs.present(id:) { PPDialog(title:...) { 内容 } footer: { 按钮 } }`，关闭 `dialogs.dismiss(id)` |
| 提示 | `AppModel.shared.toasts.show("success", 标题, message:)`、`.error(error)` |
| 带错误提示的调用 | `AppModel.shared.perform(tr("…")) { api in … }` |
| 控制台 | `ConsolePanel(consoleId:runId:logFile:compact:)` |
| 转圈、进度 | `PPSpinner`、`PPProgressBar(value:)` |
| 横向换行排列（引用徽标等） | `FlowRow(spacing:)` |
| 复制、打开地址、在访达中显示 | `Actions.copy(_:)`、`Actions.openURL(_:)`、`Actions.revealInFinder(_:)` |

## 提交图

日志里每个 `VCSCommit.graph`（`VCSGraphRow`）已由内核算好车道与连线，界面只负责画。用 `PaperplaneKit.CommitGraphGeometry`：

```swift
let geo = CommitGraphGeometry(laneWidth: 14, rowHeight: M.rowHeight, leftPadding: 8)
let row = geo.row(commit.graph!, previous: previousCommit?.graph)
// row.node：节点中心；row.segments：上半段（来自上一行的 edges）+ 下半段（本行 edges）；row.width：本行图宽
// 颜色：theme.graphLane(segment.color)
```

用 `Canvas` 逐行画（线宽 1.5，节点半径 4，HEAD 节点空心），整列宽度取 `geo.width(for: rows)`。

## 主界面上的挂点

- `VCSHooks.cloneAction`：赋值后，主工具栏「导入」菜单会出现「从 Git 克隆…」。例如在你的模块里提供一个 `@MainActor` 初始化函数，并在 `AppModel.start()` 或 `VCSTabView` 首次出现前设置：

```swift
VCSHooks.cloneAction = { CloneDialog.present() }
```

- 工程卡片上的 Git 区、标签页上的改动数徽标已经用 `ProjectSummary.vcs` 显示，不需要改。

## 验证

- `swift test`（在 `PaperplaneKit/`）：模型解码、ANSI、SVG、提交图几何、文案 key 一致性。
- `xcodebuild -project LemonPaperplane.xcodeproj -scheme LemonPaperplane -configuration Debug -destination 'platform=macOS' CODE_SIGNING_ALLOWED=NO build`：零错误零警告。
- 对照真实 daemon：起一个独立实例（`PAPERPLANE_DATA_DIR=<临时目录>`，`settings.json` 里改 `apiPort`），用 `paperplane call vcs.status '{"projectId":"…"}'` 看真实返回，再把响应存进 `PaperplaneKit/Tests/PaperplaneKitTests/Fixtures/real/` 加解码测试。
