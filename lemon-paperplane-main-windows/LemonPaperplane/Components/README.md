# 界面组件速查（Windows）

和 Mac 版 `LemonPaperplane/Components`、`Design` 一一对应。全部在 `LemonPaperplane` 命名空间；写界面的文件里加一行
`using static LemonPaperplane.UI;` 就能直接写 `HStack(…)`、`Text(…)`。

组件陈列页：`LemonPaperplane.exe --gallery`（或调试桥 `{"cmd":"gallery","tab":"controls|display|overlays|icons"}`），源码 `App/Gallery.cs` 就是每个组件的用法示例。

## 1. 写一个视图（PPView）

```csharp
// Mac: struct RunsBadge: View { @ObservedObject var runs: RunStore; var body: some View { … } }
public sealed class RunsBadge : PPView
{
    private readonly RunStore _runs;
    private readonly State<bool> _open;                      // @State var open = false

    public RunsBadge(RunStore runs)
    {
        _runs = runs;
        Observe(runs, nameof(RunStore.Active));              // 只订阅用到的属性（不给属性名 = 任何变化）
        _open = UseState(false);
    }

    protected override UIElement Build() =>
        HStack(M.Xs, new PPIcon("activity", color: ColorToken.StatusRunning), Text(_runs.Active.Count.ToString(), Typo.Label));
}
```

- `Observe(INotifyPropertyChanged, params string[] props)`、`Observe(INotifyCollectionChanged)`、`Observe(h => x.Changed += h, h => x.Changed -= h)`、`ObserveTheme()`（只有自绘读 `Theme.Color` 的视图需要）。
- 同一轮消息里的多次变化合并成一次重建；不可见（Unloaded）的视图不重建，重新显示时补一次。语言切换自动重建。
- `OnAppear()` / `OnDisappear()`：开始 / 停止轮询、订阅流。
- 重建会换掉整棵子树。框架会按 `.Key("…")` / `.Id("…")` 恢复焦点（文本框的光标与选区）和 `ScrollViewer` 的滚动位置（没有键时按出现顺序）。
- **含输入框的区域拆成独立视图**，不要订阅输入框自己写入的那个属性（搜索框在工具栏视图里，按搜索词过滤的列表在另一个视图里）。实在要在同一个视图里，用 `Keep("key", () => new PPTextField(…), tf => tf.Sync())` 跨重建复用同一个实例。
- 一次性的小视图：`new ViewOf(() => …, store1, store2)`。
- 跨重建保留的小状态（折叠、宽度）：`UIState.Get/Set/Bind("key", 默认值)`。
- store / 视图模型基类：`PPObservable`（`Set(ref _x, value)` 自动通知）。

## 2. 布局辅助（对应 SwiftUI 的写法）

| SwiftUI | C# |
|---|---|
| `HStack(spacing: 8) { a; b; Spacer() }` | `HStack(M.Md, a, b, Spacer())`（默认垂直居中） |
| `VStack(alignment: .leading, spacing: 4) { … }` | `VStack(M.Xs, …)`（默认左对齐；`.Align = CrossAlign.Stretch` 撑满） |
| `ZStack { … }` | `ZStack(…)` |
| `if cond { x }` / `ForEach(items) { … }` | 子元素写 `cond ? x : null`，或直接传 `items.Select(…)`（自动展开） |
| `Text("…").font(.pp(.title)).foregroundColor(theme.textSecondary)` | `Text("…", Typo.Title, ColorToken.TextSecondary)`（单行、末尾省略、空间不够先被压缩） |
| 多行文字 / 中间截断 / 可选中 | `Paragraph(…)`、`TextMiddle(…)`、`Selectable(…)` |
| `.padding(12)` | `.Pad(M.Lg)`（Border / Control / TextBlock / Stack；其它元素 `Box(el).Pad(…)`） |
| `.frame(width: 120, height: 28)` / `.frame(maxWidth: .infinity)` | `.W(120).H(28)` / `.Size(w, h)` / `.FillW()`；在 Stack 里占剩余空间 `.Flex()` |
| `.background(theme.bgSurface)` / `.foregroundColor(…)` | `.Bg(ColorToken.BgSurface)` / `.Fg(ColorToken.TextSecondary)`（资源引用，切换主题自动更新） |
| `.overlay(RoundedRectangle(…).strokeBorder(theme.border))` | `Box(x).Rounded(M.RadiusLG).Outline(ColorToken.Border)` |
| `.opacity(0.5)` / `.disabled(true)` / 显隐 | `.Alpha(0.5)` / `.Disabled()` / `.Visible(bool)`、`.Hidden()` |
| `.help("…")` | `.Tip("…")` |
| `.onTapGesture { }` / `.onHover { }` | `.OnClick(() => …)` / `.OnHover(inside => …)`（能用 PPButton / PPRow 时用它们） |
| 外边距 | `.Margins(…)`（方法名避开了元素已有属性：Margin → Margins、Opacity → Alpha、Width → W） |
| 网格 | `Grid("160,*", "Auto,Auto", a.At(0, 0), b.At(0, 1))` |
| `ScrollView { }` | `Scroll(content)`（叠在内容上的细滚动条，不占宽度） |
| 分隔线 / 固定空白 | `new PPDivider(vertical, subtle)` / `Gap(8)` |
| 自动化 ID（调试桥用） | `.Id("project.run")`、`.A11y("名字")` |
| 逃生口 | `.Do(el => …)` |

宽度按内容（`.fixedSize(horizontal: true)`）：`new FitWidth(child, min, max)`。自动换行排列：`Flow(spacing, …)`（= Mac 的 `FlowRow`）。

## 3. 主题、尺寸、字体、图标、文案

- 颜色键：`ColorToken.*`（键名同 `design-tokens.json`，也可以直接写字符串 `"bgContent"`；Debug 编译写错键名直接抛异常）。`ColorToken.Clear` 是透明但能接收鼠标。
- 主题：`Theme.Apply("system|light|dark")`（切换资源字典，不重建界面；system 跟随 Windows「应用模式」，系统设置变化时自动切换）、`Theme.IsDark`、`Theme.Changed`、`Theme.Brush(key)` / `Theme.Color(key)`（自绘用）、`Theme.Tone("warning")` → `.Fg/.Bg/.Border` 三个键、`Theme.Severity(…)`、`Theme.GraphLane(i)`、`Theme.Ansi(i)`。
- 阴影：`.Shadow(ShadowToken.Card)`（只加在背景形状上，别加在含文字的容器上，否则文字变糊）。
- 尺寸：`M.ToolbarHeight`、`M.RowHeight`、`M.Md`、`M.RadiusLG`、`M.Icon`… 与 Mac 的 `M` 同名（C# 大小写）。动效 `M.MotionFast/Normal/Slow`。
- 字体层级：`Typo.Caption / Small / Body / BodyStrong / Label / Title / Heading / Display / Stat / Mono / MonoSmall`；`el.Font(Typo.Title)`。字体族 `UIFonts.UI`、`UIFonts.Mono`（JetBrains Mono → Cascadia Mono → Consolas）。
- 图标：`new PPIcon("play")`、`new PPIcon("x", M.IconSmall, ColorToken.IconSecondary)`、`PPIcon.Raw(raw, plugin, fallback: "box", size)`（= Mac 的 `PPIcon(raw:plugin:fallback:)`）。不给颜色时跟随外层前景色。需要 `ImageSource` 时 `IconStore.Shared.Image(name, size, color)`。
  - 插件图标：`IconStore.Shared.PluginLoader = (id, path) => api.PluginFileAsync(id, path)`（返回 SVG 字节；接 API 的同事设置），`IconStore.Shared.PluginVersions[id] = version`，插件变化后 `ClearPluginIcons()`。
  - 托盘：`IconStore.Shared.TrayIcon(16 × DPI 缩放, IconStore.TrayColor)` 得到 `System.Drawing.Icon`；任务栏深浅变化订阅 `Theme.SystemThemeChanged`。
- 文案：`L.T("key")`、`L.T("key", arg0, arg1)`；`L10n.Shared.Apply("system|zh-Hans|en")` 切换（PPView 自动重建），`L10n.Shared.SystemLocale` 给拉起服务用。
- 界面小偏好（启动前就要用的值）：`UiPrefs.Get/Set`（注册表 `HKCU\Software\LemonPaperplane\UI`）。

## 4. 组件（Mac 写法 → C# 写法）

| 组件 | C# |
|---|---|
| `PPButton("运行", icon: "play", kind: .primary, size: .small) { … }` | `new PPButton(L.T("action.run"), () => …, icon: "play", kind: PPButtonKind.Primary, size: PPButtonSize.Small)`；`fullWidth`、`busy`（`btn.Busy = true`） |
| `PPIconButton("square", help: tr("action.stop"), tint: theme.danger) { … }` | `new PPIconButton("square", L.T("action.stop"), () => …, tint: ColorToken.Danger)`；`size: M.IconButtonSmall, iconSize: M.IconSmall`、`active`、`busy` |
| `PPLink(title: "localhost:8080", mono: true) { … }` | `new PPLink("localhost:8080", () => …, mono: true, icon: "external-link")` |
| `PPToolbarGroup { … }` | `PPToolbarGroup.Of(a, b, c)` |
| `PPTextField(tr("common.filter"), text: $q, icon: "search", clearable: true)` | `new PPTextField(L.T("common.filter"), q, icon: "search", clearable: true, onSubmit: …, onEscape: …)`；`mono`、`secure`、`invalid`、`height`；外部改值后 `tf.Sync()` |
| `PPTextArea(text: $t)` | `new PPTextArea(t, placeholder, mono: true)` |
| `PPNumberField(placeholder: "", value: $n)` | `PPNumberField.Make("", n)`（`Bind<double?>`） |
| `InputChrome(focused:) { … }` | `new InputChrome(content, height)`（工具栏搜索框之类自己拼的输入框外框） |
| `PPDropdown(options: [.init(value: "a", title: "A")], selection: $v)` | `new PPDropdown<string>(new[] { new DropdownOption<string>("a", "A", icon: "play", detail: "…") }, v, placeholder, size, minWidth, maxWidth, fullWidth, borderless)` |
| `PPMenuButton(entries: { […] }) { label }` | `new PPMenuButton(() => new[] { MenuEntry.Item(…), MenuEntry.Divider, MenuEntry.Submenu(…) }, label, PopupAlignment.Trailing, help)` |
| `PPButtonMenu(title:icon:kind:entries:)` | `new PPButtonMenu(title, () => entries, icon, kind, size, alignment)` |
| `.item("重命名", icon: "pencil") { … }` | `MenuEntry.Item(L.T("…"), () => …, icon: "pencil", shortcut: "Ctrl+R", destructive:, disabled:, @checked:)`；`MenuEntry.Header("…")`、`MenuEntry.Divider`、`MenuEntry.Submenu(title, children, icon)` |
| `.ppContextMenu { entries }` | `el.PPContextMenu(() => entries)`（右键 / 菜单键；嵌套时最内层的生效） |
| `.ppPopover(isPresented: $open) { … }` | `el.PPPopover(open, () => new PopoverContent(…, width), PopupAlignment.Leading)`；或命令式 `Popover.Show(anchor, content, alignment, onClose:)` 返回 `PPPopup`（`.Close()`） |
| 菜单弹出 | `MenuPresenter.Show(entries, anchor, alignment)`、`MenuPresenter.ShowAt(entries, element, point)` |
| `PPInfoButton(text: …)` | `new PPInfoButton(L.T("….info"))` |
| `PPSegmented(options: [("a","A")], selection: $v)` | `new PPSegmented<string>(new[] { ("a", "A"), ("b", "B") }, v, PPButtonSize.Small)` |
| `PPTabBar(tabs: […], selection: $tab)` | `new PPTabBar<string>(new[] { new PPTab<string>("run", L.T("…"), icon: "play", badge: 2) }, tab)` |
| `PPSwitch(isOn: $on)` | `new PPSwitch(on)` |
| `PPCheckbox(isOn: $on, label: "LTS")` / `PPRadio(selected:label:) { }` | `new PPCheckbox(on, "LTS")` / `new PPRadio(selected, label, () => …)` |
| `PPBadge(text: "LTS", tone: "success")` | `new PPBadge("LTS", tone: "success", icon: "badge-check", mono: false)` |
| `PPCountBadge(count: 3)` | `new PPCountBadge(3, tone: null)` |
| `FacetBadgeView(facet: f)` | `new FacetBadgeView(f.Name, f.Icon, f.Plugin)` |
| `StatusDot(state: run.state)` | `new StatusDot(run.State)`（`dot.State = …` 更新）；颜色 / 名称 `RunStatusStyle.Color/Title/IsBusy` |
| `PPSpinner(size:)` / `PPProgressBar(value:)` | `new PPSpinner(M.IconSmall, color)` / `new PPProgressBar(task.Percent, tone: null)`（`< 0` 不确定；`bar.Value = …`） |
| `PPDivider(vertical:subtle:)` | `new PPDivider(vertical: true, subtle: true)` |
| `PPSection("模块", accessory: { … }) { … }` | `new PPSection(L.T("…"), content, info: L.T("….info"), accessory: btn, collapsible: true, stateKey: "overview.modules")` |
| `PPCard { … }` | `new PPCard(content, hoverable: true, selected: false)` |
| `PPKeyValueList(rows: [.init(key: "端口", value: "8080", mono: true)])` | `new PPKeyValueList(new[] { new PPKeyValueRow("端口", "8080", mono: true, tone: null, href: null) }, keyWidth: 120)` |
| `PPAlert(tone: "danger", title:, message:) { buttons }` | `new PPAlert("danger", title, message, btn1, btn2)` |
| `PPEmptyState(icon:title:actionTitle:) { … }` | `new PPEmptyState("folder-open", L.T("…"), L.T("…"), () => …, actionIcon)` |
| `PPRow(selected: s, action: { … }) { … }` | `new PPRow(content, selected: s, action: () => …)` |
| `PPSplitView(leftWidth: $w) { … } right: { … }` | `new PPSplitView(left, right, w, minLeft, maxLeft)`（`Bind<double>`） |
| `PPPageHeader(title: …) { … }` | `new PPPageHeader(L.T("…"), subtitle, accessory…)` |
| `FlowRow(spacing: 4) { … }` | `Flow(M.Xs, …)` |
| `PopoverContent(width:) { … }` | `new PopoverContent(content, width)` |

`Bind<T>`（= SwiftUI 的 `Binding`）：传 `State<T>`，或 `Bind.Of(() => model.X, v => model.X = v)`、`Bind.Const(v)`。自绘控件按自身状态更新外观，外部改了绑定值后调 `Sync()`。陈列 / 截图时可以 `el.Force("hover|pressed|focus|open")` 强制显示某个状态。

## 5. 对话框与提示

```csharp
Dialogs.Confirm(L.T("project.remove"), message, async () => await api.ProjectRemoveAsync(id), ok: L.T("common.remove"), danger: true);
Dialogs.Input(L.T("workspace.new"), L.T("common.name"), async name => await api.WorkspaceCreateAsync(name), initial: "");
ToastStore.Shared.Show("success", L.T("common.copied"));
ToastStore.Shared.Show("error", L.T("action.run.failed"), e.Message, L.T("console.openLog"), () => …);
ToastStore.Shared.Error(e);                          // 取消不提示
```

- 回调抛异常时对话框不关，在框里显示错误（`e.Message`）；成功后自动关闭。回车 = 主按钮，Esc = 取消。
- 自定义对话框：继承 `AsyncDialog`（有 `Busy`、`Error`、`Run(async () => …)`），`Build()` 返回 `new PPDialog(title, content, new UIElement[] { cancel, ok }, width, onClose) { DefaultButton = ok, CancelButton = cancel }`，用 `Dialogs.Present(id, view, onCancel)` 显示、`Dialogs.Dismiss(id)` 关闭。
- **`Dialogs.Form` 由表单渲染器补**：`Dialogs` 是 `partial`，在 `Renderers/` 里另写一个 `public static partial class Dialogs { public static void Form(…) }`，里面用 `new FormDialogShell(id, title, message, () => new FormView(…), validate, submit, ok, danger, width, onCancel)`（表单只建一次，输入中不会被重建打断）。
- 对话框 / 提示的 store：`DialogStore.Shared`、`ToastStore.Shared`（AppModel 可以直接暴露这两个实例）。

## 6. 主窗口外壳

`MainWindow.Current`：

- 插槽：`ToolbarLeft`（侧栏开关、位置标题）、`ToolbarCenter`（一般空着，空白处可拖动 / 双击最大化）、`ToolbarRight`（搜索、导入、运行中、任务；窗口按钮在它右边）、`Sidebar`、`Page`（内容岛里的页面）。放进工具栏的控件自动可点（`WindowChrome.IsHitTestVisibleInChrome` 在插槽上设好了）。
- `SidebarVisible`、`SidebarWidth`（180–360，拖动条在侧栏右边，宽度与显隐记在 `UiPrefs`）。
- `ShowMainWindow()`（托盘、第二次启动时调用）、`HideOnClose = () => true`（托盘接上后：关闭只隐藏，服务继续运行）。
- 快捷键：`Shortcuts.Register("Ctrl+R", MainCommands.Run)`（返回菜单里显示的 "Ctrl+R"；弹出层或对话框打开时不触发）。

## 7. 调试桥

```powershell
$env:PAPERPLANE_DEBUG_BRIDGE = "ppd"; Start-Process LemonPaperplane.exe -ArgumentList --gallery
'{"cmd":"click","id":"gallery.menu"}','{"cmd":"snapshot","path":"D:/shots/menu.png","scale":1.5}' | & .\LemonPaperplane.exe --debug-client ppd -
```

命令见 `App/DebugBridge.cs` 顶部注释（snapshot / tree / click / focus / key / type / theme / locale / resize / state / gallery / force / hittest / scroll / popups / errors / quit）。组件都设了无障碍名字，按文字点击用 `{"cmd":"click","text":"运行"}`；功能页的关键控件请加 `.Id("…")`。

## 8. 已占用的类型名

同一个命名空间，新建类型前先避开：`M Typo UIFonts Theme ColorToken ShadowToken ToneKeys UI UIExt UIState Bind State PPObservable Stack FlowRow FitWidth SpacerElement MiddleTrimText KeyInput PPView ViewOf Templates IconKey CompiledIcon IconRenderer IconStore IcoWriter PPIcon L10n L UiPrefs UIHooks PPDebug IVisualStateful PPButtonBase PPButton PPIconButton PPLink PPToolbarGroup InputChrome PPTextField PPTextArea PPNumberField PPSwitch PPCheckbox PPRadio PPSegmented PPTab PPTabBar PPDropdown DropdownOption PPBadge PPCountBadge FacetBadgeView RunStatusStyle StatusDot PPSpinner PPProgressBar PPDivider PPSection PPCard PPEmptyState PPInfoButton PPKeyValueRow PPKeyValueList PPRow PPPageHeader PPAlert PPSplitView PopoverContent PopupAlignment PPPopup PopupController Popover PPMenuItem MenuEntry MenuEntryKind MenuPresenter PPMenuButton PPButtonMenu ContextMenus DialogStore PPDialog DialogHost AsyncDialog ConfirmDialog TextInputDialog FormDialogShell Dialogs ToastStore ToastHost ToastView MainWindow CaptionButton CaptionKind Shortcuts DebugBridge Gallery IconGen Native`。
