# 设计规范（三端共用）

Lemon Paperplane 的 Mac（SwiftUI/AppKit）、Windows（WPF）、Linux（GTK）界面**长得一样**：同一套颜色、字号、间距、圆角、控件样式、图标和文案。风格参考 IntelliJ 新 UI（Islands）：紧凑、中性灰、蓝色强调色、16px 线性图标。

- 设计变量：`shared/design-tokens.json`（亮 / 暗两套颜色 + 字号、间距、圆角、尺寸、图标线宽、阴影、动效）。**三端运行时读取这份文件**，不要在代码里写死颜色。
- 图标：`shared/icons/*.svg`（Lucide，ISC 许可）；插件图标经 API 取。
- 文案：`shared/i18n/zh-Hans.json`、`shared/i18n/en.json`（扁平 key → 文本，`{0}` 占位）。
- 品牌：`shared/branding/app-icon.svg`（App 图标源）、`shared/branding/menubar.svg`（菜单栏 / 托盘单色模板图标）。

参考实现：`lemon-paperplane-main-macos/LemonPaperplane/`（`Design/`、`Components/`、`Renderers/`）。

---

## 1. 原则

1. **自绘控件，不用系统默认外观**。按钮、输入框、下拉、开关、复选框、分段控件、标签栏、菜单、弹出框、对话框、提示都按本规范自绘；只有文本输入光标、滚动条、文件选择框用系统的。
2. **不用 emoji，不用平台图标库**（SF Symbols、Segoe Fluent Icons、Adwaita 图标都不用）。所有图标都是 SVG。
3. **没有「AI 味」**：界面上不铺解释功能的小字。标题 + 控件为主；确实要说明的，放一个 `info` 图标，点开弹出框显示文字。空状态只有**一个图标 + 一句话 + 一个按钮**。错误直接给结论和修复按钮。
4. **轻量**：控制台只在视图里保留最近若干行（`size.consoleMaxLines`，完整日志在磁盘）；列表懒加载；不内嵌浏览器内核，只有插件声明 `kind: webview` 且用户点开时才创建 WebView；日志流只给可见的控制台订阅；插件界面的定时刷新在不可见时暂停。
5. **全部文案走 key**，界面上不出现硬编码中文或英文。

---

## 2. 颜色

全部颜色在 `design-tokens.json` 的 `color.light` / `color.dark`，键名三端一致。下面是用途（括号里是亮 / 暗取值，以 JSON 为准）。

| 键 | 用途 |
|---|---|
| `bgChrome` | 窗口底色：顶部工具栏、左侧栏（#F2F3F5 / #1E1F22） |
| `bgContent` | 内容区「岛」底色，圆角 `radius.lg`，1px `border` 描边（#FFFFFF / #26282B） |
| `bgSurface` | 卡片、表格、模块块 |
| `bgSubtle` | 次级区域：表头、搜索条、统计块 |
| `bgElevated` | 弹出框、菜单、对话框、提示 |
| `bgInput` / `bgInputDisabled` | 输入框 |
| `bgHover` / `bgPressed` | 悬停 / 按下 |
| `bgSelected` / `bgSelectedInactive` | 列表选中（窗口激活 / 未激活） |
| `bgCode` | 代码、详情块 |
| `border` / `borderSubtle` / `borderStrong` | 描边：卡片 / 分隔线 / 输入框、次按钮 |
| `borderFocus` / `focusRing` | 聚焦描边与外圈光晕（2px） |
| `textPrimary` / `textSecondary` / `textTertiary` / `textDisabled` | 文本四级 |
| `textOnAccent` / `textLink` | 强调底上的文字 / 链接 |
| `iconPrimary` / `iconSecondary` | 图标默认色 |
| `accent` / `accentHover` / `accentPressed` / `accentSubtle` / `accentSubtleBorder` | 强调色（#3574F0，主按钮、选中、开关） |
| `success*` / `warning*` / `danger*` / `info*` / `neutral*` | 语气色：前景 / `*Subtle` 浅底 / `*Border` 描边 |
| `statusIdle` / `statusStarting` / `statusRunning` / `statusFailed` / `statusStopping` | 运行状态点 |
| `buttonSecondaryBg` / `buttonSecondaryHover` / `buttonSecondaryBorder` | 次按钮 |
| `switchOff` | 开关关闭态轨道 |
| `scrim` | 对话框遮罩 |
| `consoleBg` / `consoleText` / `consoleSystem` / `consoleStderr` / `consoleSelection` | 控制台 |
| `searchMatch` / `searchMatchCurrent` | 搜索高亮 / 当前项 |
| `graphLane0`…`graphLane7` | 提交图车道颜色（`VCSGraphRow.color % 8`） |
| `diffAddedBg/Text`、`diffRemovedBg/Text`、`diffHunkBg` | 差异视图 |
| `brandLemon` | 品牌柠檬黄（只用于品牌元素） |

`ansi.light` / `ansi.dark`：16 个 ANSI 基础色（0–7 普通，8–15 亮色）。16–255 按 xterm 256 色规则计算（6×6×6 立方 + 24 级灰），真彩直接用。

**语气（tone）映射**：`neutral` → neutral*；`info` → info*；`success` → success*；`warning` → warning*；`danger`（或 `error`）→ danger*。诊断严重度：error→danger、warning→warning、info→info。

**主题**：设置里 `theme = system | light | dark`，`system` 跟随系统。切换立即生效（Mac：`NSApp.appearance`；Windows：切换资源字典；Linux：切换 CSS）。

---

## 3. 字体

| 层级（`typography`） | 字号 / 字重 / 行高 | 用途 |
|---|---|---|
| `caption` | 11 / 400 / 14 | 徽标、分组小标题、次要计数 |
| `small` | 12 / 400 / 16 | 次要信息、路径、说明 |
| `body` | 13 / 400 / 18 | 正文、列表、输入框（默认） |
| `bodyStrong` | 13 / 600 / 18 | 列表主标题、选中标签 |
| `label` | 12 / 500 / 16 | 按钮文字、表头 |
| `title` | 14 / 600 / 20 | 区块标题、卡片名称、对话框标题 |
| `heading` | 16 / 600 / 22 | 页面标题 |
| `display` | 20 / 600 / 26 | 工程详情页名称 |
| `stat` | 22 / 600 / 28 | 统计数字 |
| `mono` / `monoSmall` | 12 / 11 | 控制台、路径、版本号、代码 |

字体族：界面字体用各平台系统字体（`font.ui`），等宽字体优先 JetBrains Mono，否则平台等宽字体（`font.mono`）。字号三端相同。

---

## 4. 间距、圆角、尺寸

- 间距 `spacing`：`xxs 2`、`xs 4`、`sm 6`、`md 8`、`lg 12`、`xl 16`、`xxl 20`、`xxxl 24`、`huge 32`。页面内边距 `xl`，卡片内边距 `lg`，区块间距 `xxl`，控件间距 `md`。
- 圆角 `radius`：`xs 3`（复选框）、`sm 4`（徽标、菜单项）、`md 6`（按钮、输入框、下拉）、`lg 8`（卡片、弹出框、内容岛）、`xl 12`（对话框）。
- 尺寸 `size`：

| 键 | 值 | 说明 |
|---|---|---|
| `toolbarHeight` | 40 | 主工具栏、控制台工具栏 |
| `tabBarHeight` | 36 | 标签栏 |
| `rowHeight` / `rowHeightCompact` | 28 / 24 | 列表行、菜单项 |
| `controlHeight` / `controlHeightSmall` | 28 / 24 | 按钮、输入框、下拉 |
| `iconButton` / `iconButtonSmall` | 28 / 22 | 图标按钮 |
| `sidebarWidth`（min/max） | 232（180–360） | 左侧栏，可拖动 |
| `cardMinWidth` / `cardMaxWidth` | 300 / 440 | 工程卡片网格自适应列宽 |
| `formLabelWidth` | 160 | 表单标签列 |
| `dialogWidth` / `dialogWidthLarge` | 480 / 640 | 对话框 |
| `popoverMaxWidth` | 420 | 弹出框 |
| `statusDot` | 8 | 状态圆点 |
| `consoleMaxLines` | 5000 | 控制台视图里保留的行数 |

- 阴影 `shadow`：`popup`（弹出框、菜单）、`dialog`、`card`（悬停时）、`toast`。暗色主题阴影不透明度可加大。
- 动效 `motion`：`fast 100`、`normal 160`、`slow 240` 毫秒，一律 ease-out，不用弹簧；提示 `toastSeconds 4`、错误/警告 `toastErrorSeconds 8`。

---

## 5. 图标

- 来源：`shared/icons/<name>.svg`，Lucide 风格（24×24 viewBox、`stroke="currentColor"`、`stroke-width="2"`、圆角端点）。缺的从 lucide-static 补进来：去掉 license 注释和 `class` 属性。
- 引用：界面数据里的 `icon` 字段是 `builtin:<name>` 或 `plugin:<pluginId>/<相对路径>`（插件图标经 `GET /api/v1/plugins/<id>/files/<path>` 取，按插件 id + 版本缓存）。插件界面描述里没有前缀的相对路径属于该插件。
- 尺寸：默认 16，小 14（徽标、小按钮），大 20（列表大图标），空状态 32。
- **线宽按显示尺寸调细**（`icon.strokeScale`）：≤16px 时线宽 ×0.75（Lucide 的 2 → 1.5，16px 下约 1px，不发粗），≤20px ×0.875，更大 ×1。
- 着色：只用 `currentColor`（或无色）的图标按模板图渲染、随前景色着色；有固定颜色的（品牌图标）保留原色。
- **SVG 子集渲染器**（三端各一个，只支持这个子集）：元素 `svg g path circle ellipse rect line polyline polygon`；属性 `viewBox fill stroke stroke-width stroke-linecap stroke-linejoin fill-rule opacity transform`（另容忍 `fill-opacity`、`stroke-opacity`、`style` 里的同名声明）；path 全部命令（含弧线 A，转三次贝塞尔）；transform `translate scale rotate matrix`（含 skewX/skewY）；颜色 `#rgb #rrggbb #rrggbbaa rgb() 常见颜色名 none currentColor`。`defs` 等未知容器整体跳过。Mac 实现见 `PaperplaneKit/Sources/PaperplaneKit/SVG/`，有单元测试覆盖 shared/icons 全部图标与各插件图标。

---

## 6. 布局

### 主窗口

```
┌──────────────────────────────────────────────────────────────────────┐
│ ●●●  [侧栏]  位置标题        ……拖动区……   [搜索]  [+ 导入 ▾] [运行 n] [任务] │ 40px，bgChrome
├──────────────┬───────────────────────────────────────────────────────┤
│ 工作区     +  │ ╭───────────────────────────────────────────────────╮ │
│  默认      3  │ │                                                   │ │
│  客户 B  ● 5  │ │   内容岛：bgContent，圆角 8，1px border，            │ │
│ 运行中     2  │ │   右、下各留 6px 露出 bgChrome                      │ │
│ ─────────    │ │                                                   │ │
│ 工具链        │ │                                                   │ │
│ 插件          │ ╰───────────────────────────────────────────────────╯ │
│ 设置          │                                                       │
└──────────────┴───────────────────────────────────────────────────────┘
```

- 顶部工具栏横跨整个窗口，高 40；Mac 的红绿灯放在工具栏垂直中线上，左侧让出约 84px。Windows/Linux 的窗口按钮在右侧，同样放在 40px 栏里（自绘标题栏）。
- 左侧栏：工作区（名称、工程数、有运行中工程时绿点；可拖动排序；右键重命名/全部停止/删除）、「运行中」聚合、分隔线、工具链、插件、设置、插件贡献的 `sidebar` 视图。选中项 `bgSelected` + 图标强调色 + 粗体。
- 关闭主窗口只隐藏界面（Mac 变成仅菜单栏、Dock 图标消失；Windows/Linux 收到托盘），服务继续在 daemon 里跑。菜单「退出并停止全部服务」才调 `system.shutdown{stopRuns:true}`。

### 工程卡片

```
╭─────────────────────────────────────────╮
│ ● vrtalk-api                   [▲2] [⋯] │  状态点 + 名称（title）；问题数；更多菜单
│   ~/Code/vrtalk/vrtalk-api              │  路径（small，tertiary，~ 缩写，中间截断）
│ [▣ Gradle] [▣ Spring Boot] [▣ Java]     │  facet 徽标（图标 12 + caption）
│ Gradle 8.12   Java 17                   │  1–3 条 info（label tertiary，value secondary）
│ ─────────────────────────────────────── │
│ [Application      ⇅]          [↻] [■]   │  运行配置下拉 + 运行/停止/重启（22px 图标按钮）
│ ↗ localhost:8080          ▢ 3.5%  ▢ 512 MB │  运行中：可点击地址、CPU、内存（monoSmall）
│ ⎇ main  ↑1  3 处改动                     │  Git：分支、领先/落后、改动、冲突徽标
│ ◌ 正在同步…                              │  忙碌：转圈 + 任务名
╰─────────────────────────────────────────╯
```

点卡片进入工程详情；右键与「⋯」同一个菜单：打开、在访达中显示、在终端打开、用 IDEA/VS Code 打开、停止全部运行、重新导入、插件 `card.menu` 动作、移到工作区（子菜单）、重命名、移除。卡片可拖动排序（`project.reorder`）。悬停：描边变 `borderStrong` + `card` 阴影。

### 工程详情

头部：返回、状态点、名称（display）+ facet 徽标、路径链接（点了在文件管理器里显示）；右侧：运行地址、配置下拉、运行/停止/重启、插件 `project.toolbar` 动作、「⋯」（重新导入、同步、打开方式、停止、插件 `project.menu` 动作、重命名、移除）。

标签：概览、运行、文件、版本控制、配置 + 插件 `project.tab`。下划线标签栏，选中项粗体 + 2px 强调色下划线，可带数量徽标。

- **概览**：问题（诊断行 + 修复按钮）、模块卡（facet、info、各工具链当前版本与「自动/已安装/约束」选择、依赖状态 + 安装）、插件 `project.section`。
- **运行**：左侧运行配置列表（状态点、默认标记、运行/停止；右键编辑/复制/设为默认/恢复/删除；+ 新建），右侧控制台：诊断条 → 控制台工具栏（运行控件、状态、地址、CPU/内存、插件 `run.toolbar` 动作、侧栏开关 | 搜索、自动换行、跟随滚动、复制、清空、打开日志文件）→ ANSI 日志 → 输入行（运行中可写 stdin）；可切换的右侧插件 `run.panel`（按 `runState` 条件显示）。
- **配置**：环境变量键值表、工具链覆盖、运行配置列表（点开编辑）、相关插件设置。
- **文件**：见第 14 节。

---

## 7. 组件清单

Mac 实现都在 `LemonPaperplane/Components/`、`Design/`、`Renderers/`；Windows/Linux 照同名组件实现。

| 组件 | 规格 | Mac 用法 |
|---|---|---|
| **主题** | 见第 2 节 | `@Environment(\.theme) var theme`；`theme.textPrimary`、`theme.tone("warning").fg`；顶层视图包 `Themed { … }` |
| **尺寸 / 字体** | 第 3、4 节 | `M.rowHeight`、`M.md`、`M.radiusLG`；`.font(.pp(.title))` |
| **图标** `PPIcon` | 第 5 节 | `PPIcon("play")`、`PPIcon(ref: .plugin(id:path:), size: 20)`、`PPIcon(raw: "plugin:x/y.svg", fallback: "box")` |
| **按钮** `PPButton` | 高 28（small 24），水平内边距 12（small 8），圆角 6；`primary` 强调色实心、`secondary` 白底 1px 描边、`ghost` 无底悬停出底、`danger` 红色实心、`link` 文字链接；禁用 50% 不透明；可 `busy`（转圈） | `PPButton("运行", icon: "play", kind: .primary, size: .small) { … }` |
| **图标按钮** `PPIconButton` | 28 或 22 见方，圆角 6，悬停 `bgHover`，`active` 时 `accentSubtle` 底 + 强调色图标；必须有 tooltip | `PPIconButton("square", help: tr("action.stop"), tint: theme.danger) { … }` |
| **链接** `PPLink` | `textLink`，悬停下划线，可带图标，长文本中间截断 | `PPLink(title: "localhost:8080", mono: true) { … }` |
| **输入框** `PPTextField` | 高 28，圆角 6，`bgInput` + 1px `borderStrong`；聚焦 `borderFocus` + 2px `focusRing`；`invalid` 时红色描边；可带前置图标、清除按钮、等宽、密码 | `PPTextField(tr("common.filter"), text: $q, icon: "search", clearable: true)` |
| **多行文本** `PPTextArea` | 同输入框，最小高 72 | `PPTextArea(text: $t)` |
| **数字** `PPNumberField` | 等宽，非数字时红框 | `PPNumberField(placeholder: "", value: $n)` |
| **下拉** `PPDropdown` | 外观同输入框，右侧 `chevrons-up-down`；弹出自绘列表（勾选当前项、悬停强调色底），超过 12 项带筛选框 | `PPDropdown(options: [.init(value: "a", title: "A")], selection: $v)` |
| **菜单** `PPMenuButton` / `.ppContextMenu` | 自绘：项高 28、圆角 4、悬停强调色底白字、左侧 16px 图标位、右侧快捷键、子菜单向右弹出、分隔线、分组标题、危险项红字；键盘上下/回车/左右/Esc | `PPMenuButton(entries: { [.item("重命名", icon: "pencil") { … }, .divider, .submenu("移到", [...])] }) { label }`；`.ppContextMenu { entries }` |
| **弹出框** `.ppPopover` | 自绘无边框面板：`bgElevated`、1px `border`、圆角 8、`popup` 阴影，贴锚点下方（空间不够翻到上方），点外面 / Esc 关闭 | `.ppPopover(isPresented: $open, alignment: .trailing) { … }` |
| **说明** `PPInfoButton` | 14px `info` 图标，点开 280 宽弹出框显示说明 | `PPInfoButton(text: …)` |
| **分段控件** `PPSegmented` | `neutralSubtle` 底，选中段 `bgSurface` 底 + 轻阴影 | `PPSegmented(options: [("a","A"),("b","B")], selection: $v)` |
| **标签栏** `PPTabBar` | 高 36，选中粗体 + 2px 强调色下划线，可带图标和数量 | `PPTabBar(tabs: [...], selection: $tab)` |
| **开关** `PPSwitch` | 30×18 胶囊，开 `accent`，关 `switchOff`，白色圆钮 | `PPSwitch(isOn: $on)` |
| **复选框** `PPCheckbox` / 单选 `PPRadio` | 16×16，圆角 3，选中强调色 + 白勾 | `PPCheckbox(isOn: $on, label: "LTS")` |
| **徽标** `PPBadge` / `PPCountBadge` / `FacetBadgeView` | 高 18–20，圆角 4，语气色浅底 + 前景；数量为胶囊 | `PPBadge(text: "LTS", tone: "success")` |
| **状态点** `StatusDot` | 8px 圆；启动中/准备中/停止中呼吸闪烁；tooltip 为状态名 | `StatusDot(state: run.state)` |
| **转圈 / 进度** `PPSpinner` / `PPProgressBar` | `loader-circle` 旋转；4px 胶囊进度，`value < 0` 为不确定（来回移动） | `PPProgressBar(value: task.percent)` |
| **列表行** `PPRow` | 高 28，圆角 6，悬停 `bgHover`，选中 `bgSelected` | `PPRow(selected: s, action: { … }) { … }` |
| **区块** `PPSection` | 标题（title）+ 可选 i + 右侧附件，可折叠 | `PPSection("模块", accessory: { … }) { … }` |
| **卡片** `PPCard` | `bgSurface`、1px `border`、圆角 8、内边距 12 | `PPCard { … }` |
| **键值列表** `PPKeyValueList` | 键列 secondary 固定宽，值可等宽/语气色/链接 | `PPKeyValueList(rows: [.init(key: "端口", value: "8080", mono: true)])` |
| **提示条** `PPAlert` | 语气浅底 + 描边，图标 + 粗体标题 + 说明 + 右侧按钮 | `PPAlert(tone: "danger", title: …, message: …) { buttons }` |
| **诊断行** `DiagnosisRow` | 严重度图标与底色、标题、说明、可展开详情（等宽块）、修复按钮（推荐项为主按钮）；带 `confirm` 的修复先确认 | `DiagnosisRow(diagnosis: d, runId: r.id, projectId: p.id)` |
| **空状态** `PPEmptyState` | 32px 图标 + 一句话 + 一个主按钮，居中 | `PPEmptyState(icon: "folder-open", title: …, actionTitle: …) { … }` |
| **分栏** `PPSplitView` | 1px 分隔线，7px 拖动热区 | `PPSplitView(leftWidth: $w) { … } right: { … }` |
| **页面标题** `PPPageHeader` | 高 52，heading 字号 + 右侧操作 | `PPPageHeader(title: …) { … }` |
| **对话框** `PPDialog` + `Dialogs.*` | 主窗口内模态：`scrim` 遮罩 + 居中卡片（`bgElevated`、圆角 12、`dialog` 阴影），标题栏 44 + 关闭；内容可滚动（最高 560）；底部按钮右对齐（取消在左，主按钮在右）；Esc 取消、回车主按钮 | `Dialogs.confirm(title:message:ok:danger:) { … }`、`Dialogs.input(…)`、`Dialogs.form(title:fields:…) { values in … }` |
| **提示** `ToastStore` | 右下角，宽 340，`bgElevated`、语气图标，最多 4 条，自动消失 | `AppModel.shared.toasts.show("success", "已复制")` |
| **控制台** `ConsolePanel` | 见第 8 节 | `ConsolePanel(consoleId: run.id, runId: run.id, logFile: run.logFile) { 前置工具栏控件 }` |
| **表单** `FormView` | 见第 9 节 | `FormView(fields: f, values: $v, optionValues: o)` |
| **插件视图** `PluginViewHost` | 见第 10 节 | `PluginViewHost(view: v, context: ViewContext(...))` |
| **自动换行排列** `FlowRow` | 徽标、按钮组换行 | `FlowRow(spacing: 4) { … }` |

---

## 8. 控制台

- 等宽字体 `mono`，`consoleBg` 底，内边距 8×6。stdout 用 `consoleText`，stderr 用 `consoleStderr`，内核系统行（`s = system`）用 `consoleSystem`。
- ANSI：SGR 16/256/真彩前景与背景、粗体（粗体字形）、暗淡（60% 不透明）、斜体、下划线、反显、删除线；样式跨行保留；光标控制、清屏、OSC（含超链接）等全部丢弃；行内 `\r` 只保留最后一段（进度条）。解析器在 `PaperplaneKit/ANSI`，三端行为以它的单元测试为准。
- 日志里的 `http(s)://` 地址可点击。
- 视图里最多 `consoleMaxLines` 行，超出一次删掉多出的部分再多删 10%（减少删除次数）；批量追加（约 50ms 合并一次）。打开时先取最近 2000 行（`run.logs`），再从 `lastSeq` 起订阅 `consoles/<id>/stream`；断线自动续传。
- 工具栏：搜索（⌘F / Ctrl+F，高亮全部匹配、当前项更亮、上下跳转、显示 n/m）、自动换行、跟随滚动（用户往上滚自动关闭，滚到底自动打开）、复制（有选中复制选中，否则全部）、清空（运行会同时调 `run.clear`）、打开日志文件。

---

## 9. 表单（sdk.FormField）

| type | 控件 |
|---|---|
| `string` | 单行输入框 |
| `password`（及 `secret`） | 密码框（也会按 key 名识别 password/token/secret） |
| `text` | 多行文本 |
| `boolean` | 复选框（放在控件列，标签就是标题） |
| `number` | 数字输入（等宽） |
| `select` | 下拉（选项来自 `options` 与 `optionValues[optionsFrom]`；非必填时首项「无」） |
| `multiselect` | 复选框列表（超过 6 项滚动）；没有选项时退化为字符串列表 |
| `keyvalue` | 键值表（每行 KEY + value + 删除，底部「添加变量」） |
| `path` | 等宽输入框 + 「选择…」按钮（文件或目录） |
| `list` | 字符串列表（每行输入 + 删除，底部「添加」） |

- 布局：标签列宽 `formLabelWidth`（160），控件列自适应；多行类控件标签顶部对齐。
- `required`：标签后红色 `*`，提交时为空则标红；`info`：标签后 i 图标；`advanced`：收进「更多」折叠区；`default`：没有值时填入；`placeholder`：灰字占位。
- 提交前去掉 null，数字字段的字符串转数字。

---

## 10. 插件界面描述语言（sdk.ViewNode）

三端用同一套原生组件渲染，节点 → 组件映射：

| 节点 | 渲染 |
|---|---|
| `column` / `row` | 纵向（间距 8，左对齐）/ 横向（间距 8，居中对齐） |
| `section` | 可折叠区块（`collapsed` 初始折叠，`info` 为 i 图标） |
| `divider` / `spacer` | 分隔线 / 弹性空白 |
| `tabs` | 标签栏，子节点各为一页（`title`、`icon`） |
| `heading` / `text` | heading 字号 / 正文（`tone` 着色、`mono` 等宽、可选中） |
| `code` | 等宽只读块，`bgCode` 底，可横向滚动 |
| `markdown` | 只支持粗体、链接、`-`/`*` 列表、行内代码 |
| `kv` | 键值列表（`mono`、`href`、`tone`） |
| `badge` / `alert` / `stat` / `progress` / `empty` / `icon` | 对应徽标 / 提示条（children 为按钮）/ 统计块 / 进度条 / 小空状态 / 图标 |
| `table` | 表头 `bgSubtle` + 行；列 `width`（0 自适应）、`align`、`mono`；`selectable` 时首列复选框；点行发 `row.action`（values 为 `{id, …cells}`）；行尾 `actions` 按钮 |
| `list` | 行：图标、`cells.title` 标题、`cells.subtitle` 副标题、行尾按钮 |
| `tree` | 可展开的行（`expanded` 初始状态，缩进 16） |
| `button` | `style`: primary / secondary / danger / link；`confirm` 先确认；`disabled`；`data` 作为事件 values |
| `link` | `href` 直接打开，否则发 `action` |
| `form` | FormView + 提交按钮（`submitText`）；提交发 `action`，values 为表单值 |
| `toolbar` | 横排按钮 |
| `console` | 内嵌控制台（高 320），绑定 `consoles/<console>/stream` |

- 事件：`view.event{plugin, view, event: 节点 action, values, projectId, moduleId, runId, state}`。所有事件额外带 `selection: {表格 id: [行 id]}` 与 `selected: [行 id]`（有勾选时）。
- 返回：`refresh` → 用新 `state` 重新 `view.render`；`message` → 提示；`openUrl` → 打开；`reimport` → 重新导入工程。
- `refreshMs > 0` 定时重新渲染（最小 1000ms），视图不可见时暂停。
- `kind: webview`：先显示「打开」空状态，用户点了才调 `view.render` 取 `webviewUrl`（内核直接返回，带 token 与工程/模块/运行参数）并创建 WebView；页面可同源 `fetch('/api/v1/call/<op>')`，宿主另外注入 `window.paperplane.call(op, args)`（经原生 API 客户端转发）。

插件视图挂载位置：`project.tab`（详情标签页；多个模块符合时顶部可切换模块）、`project.section`（概览区块，每个符合的模块一块）、`sidebar`（左侧栏全局页）、`run.panel`（控制台右侧栏，按 `runState`）、`vcs.tab`、`settings`（设置页与插件详情）。动作挂载：`project.toolbar`（详情头部图标按钮）、`project.menu`（详情「⋯」）、`card.menu`（卡片菜单）、`run.toolbar`（控制台工具栏）。按 facet 过滤（动作没写 facets 时，本插件参与的工程都显示）。带 `form` 的先弹表单，带 `confirm` 的先确认；多个模块符合时先选模块。

---

## 11. 交互约定

- **对话框**：确认、输入、表单、导入预览、扫描勾选、修复确认、运行配置、动作表单、插件/凭据 prompt（daemon 的 `prompt` 事件；重连时 `ui.prompts` 补弹；回答 `ui.respond`；另一端回答后 `prompt.done` 关闭）。
- **修复**：`confirm` 非空或 `killPort` 时先确认；执行后显示返回的 `message`，`rerun` 出现在运行列表，`task` 进任务指示器，`openUrl` 打开。
- **任务**：工具栏右侧转圈 + 数量，弹出框列出进度、取消、查看日志（任务控制台）。本界面发起的任务结束时：成功有消息则提示，失败提示并可看日志。
- **运行中指示**：绿色活动图标 + 数量，弹出框列出全部运行（状态、地址、CPU/内存、看日志、停止、全部停止）。
- **菜单栏（Mac）/ 托盘（Windows、Linux）**：单色模板图标；窗口：按工程分组的运行（状态、地址、内存、停止，点行打开日志）、全部停止、打开主窗口、退出界面、退出并停止全部服务。
- **快捷键**（Windows/Linux 把 ⌘ 换成 Ctrl）：⌘R 运行当前工程（运行页选中的配置，否则默认配置）、⌘. 停止、⌘F 控制台搜索（没有可见控制台时聚焦工程搜索）、⌘, 设置、⌘N 导入文件夹、⇧⌘N 扫描文件夹、⌘Q 退出界面、⌥⌘Q 退出并停止全部服务。
- **拖动排序**：工作区、工程卡片；拖进目标即实时移动，放下提交。

---

## 12. 文案与多语言

- 全部界面文案在 `shared/i18n/<locale>.json`，key 按区域分组（`common.*`、`project.*`、`settings.*`…）。本期 `zh-Hans`、`en` 两份，**key 集合必须一致、占位符一致**（Mac 单元测试会检查）。
- 设置里 `locale = system | zh-Hans | en`，切换即时生效；`system` 时中文系统用 `zh-Hans`，其余用 `en`。daemon 按同一设置本地化插件文案与错误信息。
- 文案风格：动词开头的按钮（「运行」「安装依赖」）；标题用名词；不写「点击这里…」「该功能可以…」；错误说结果不说过程；中文用全角标点，英文按钮首字母大写（Title Case）。
- App 显示名：英文 Lemon Paperplane，中文 柠檬纸飞机（Mac 用 `InfoPlist.strings`）。

---

## 13. App 图标

`shared/branding/app-icon.svg`：1024 画布，824 圆角方块（圆角 186，macOS 图标网格），蓝色 #2E66E8 底；白色折纸飞机（两面明暗 + 下方折痕），左下柠檬黄（#FFD43B）柠檬点缀。只用 SVG 子集（纯色、无渐变），各平台用自己的工具从它生成位图：

```
cd lemon-paperplane-main-macos/PaperplaneKit
swift run pp-icongen ../../shared/branding/app-icon.svg ../LemonPaperplane/Resources/Assets.xcassets/AppIcon.appiconset
```

菜单栏 / 托盘图标 `shared/branding/menubar.svg`：18×18 单色纸飞机，按模板图渲染。

---

## 14. 文件树与编辑窗口

编辑是辅助功能（契约见 `docs/editor.md`）：平时零占用，打开编辑窗口时才创建 WebView，关闭后立即释放。

- **文件标签页**（原生）：顶部 40px 工具栏——搜索框（宽 ≤ 320，非空时改为整个工程的文件名模糊搜索 `fs.search`，结果显示文件名 + 所在目录）、新建文件、新建文件夹、全部折叠、刷新、「打开编辑窗口」。树按需逐级展开（`fs.list`，不预先遍历），行高 `rowHeightCompact`（24），每级缩进 16，展开箭头 14px，图标 16px。
  - 图标：目录 `folder` / `folder-open`；文件按扩展名取通用 SVG（`file-code`、`file-json`、`file-text`、`file-image`、`file-cog`、`file-terminal`、`file-archive`、`file-key` 等，规则在 `PaperplaneKit.FileIcon`）；有语言贡献且语言 id 对应某个技术徽标时用该徽标的图标。
  - 淡化：`ignored`（node_modules、build、.venv…）50% 不透明，`hidden` 75%。
  - Git 状态（取工程各仓库的 `vcs.status`）：修改/重命名 `info` 蓝、新增 `success` 绿、未跟踪 `warning` 橙、冲突 `danger` 红、删除 `textTertiary`；文件名着色 + 行尾等宽状态字母；含改动的目录名用 `info`。
  - 交互：单击选中；双击文件在编辑窗口打开、双击目录展开/收起；右键：编辑、新建文件/文件夹、重命名、删除（确认后移到废纸篓）、在访达中显示、在终端中打开、复制路径/相对路径、刷新（目录）。
- **编辑窗口**：每个工程一个（已打开则聚焦并 `paperplane.openFile(path, line)`），窗口内标签页与左侧文件树由编辑器网页绘制。标准标题栏（透明、底色 `bgContent`），标题由网页的 `title` 消息给出，未保存时关闭按钮显示圆点（`dirty`）。网页就绪前不显示 WebView（避免白屏）；加载失败或网页进程退出时显示空状态 + 「重新加载」。窗口尺寸与位置按工程记住。
  - 快捷键：编辑窗口在前时，原生菜单只保留退出、隐藏、最小化、全屏与剪切/复制/粘贴/全选/撤销/重做，其余快捷键（⌘S、⌘F、⌘W、⌘R…）全部交给网页，由快捷键方案插件决定（默认 Eclipse，Mac 用 macOS 版）；关闭窗口走红绿灯按钮，网页先处理未保存再发 `closeWindow`。
  - 退出 App 时有未保存的编辑窗口：不退出，让这些窗口各自提示。
- **设置 → 编辑器**：快捷键方案（`ui.contributions.keymaps`，显示名 + 平台，本平台默认的标「默认」；没有插件时仍显示默认方案名）、配色（跟随 App / 插件主题）、字号、字体、缩进宽度、空格/制表符、自动换行、自动保存、保存时去行尾空格、末尾补换行，写入 `settings.editor`。

---

## 15. 资源监控

数据：`stats` 事件（每 2 秒，运行中的 RunBrief：`cpu`、`rssMB`、`peakRSSMB`、`procs`、`t`）与 `run.stats`（最近 5 分钟、最多 150 点的趋势 + 进程明细）。内存是整个进程树的常驻内存之和，CPU 是各进程之和（多核可超过 100%），说明放在 i 图标里。

- **配色**：内存用 `accent`，CPU 用 `warning`；线宽 1.25（趋势线）/ 1.5（趋势图），下方面积填充同色 14%（暗色 22%）不透明度；数字用 `Format.memory` / `Format.cpu`、等宽字体。
- **趋势线**（Sparkline）：卡片资源区 64×14、运行中弹出框 48×14、悬浮详情 260 宽 × 28 高。纵轴下限内存 64 MB、CPU 10%，避免平稳数据被放大成锯齿。悬浮 350ms 后弹出详情：内存、CPU（各带趋势线）、峰值、进程数、运行时长。
- **运行页资源面板**（控制台上方，可收起，状态记住）：标题行（资源 + i + 运行结束后「已结束」徽标 + 当前内存/CPU，收起时多一条趋势线）；展开后左右两张趋势图（固定 5 分钟时间轴、纵轴取 1/2/2.5/5×10ⁿ 的整齐上限、上限与一半处虚线参考线；悬浮显示该时刻的时间与数值，两张图联动竖线）、事实条（峰值、进程数、运行时长、端口）、进程明细表（名称、PID、CPU、内存；列表按内存降序，树形按父子关系、同级按子树内存降序，可折叠）。面板展开且可见时每 2 秒取一次 `run.stats`，收起或离开即停止。
- **运行中页**：顶部汇总（运行中工程数、合计内存、合计 CPU，heading 字号、按指标配色）与排序（默认 / 内存；按内存时每 10 秒重排一次）。
- **性能**：每个运行一个可观察的资源对象，只有显示它的小视图随 stats 重绘；工程卡片网格、侧栏、运行列表不因 stats 重绘；历史只在视图首次出现时取一次。
