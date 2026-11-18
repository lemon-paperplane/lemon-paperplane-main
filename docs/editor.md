# 文件编辑（辅助功能）

定位：临时改几个文件，**文件为辅**。主界面仍是工程卡片；工程详情里有「文件」标签页（文件树），双击或菜单「编辑」打开该工程的编辑窗口。用户用得很少，所以：**平时零占用，打开时才创建，关闭后尽快把内存全部还给系统**。

## 1. 形态

- 每个工程一个编辑窗口，窗口内多标签页；左侧可收起的文件树。
- 编辑器内核：CodeMirror 6，跑在系统 WebView 里（Mac WKWebView、Windows WebView2、Linux WebKitGTK），三端同一份网页，外观与行为一致。
- 一个窗口一个 WebView（标签页共用）。窗口关闭时销毁 WebView、它独占的进程池与网页进程，不缓存、不预热。
- 不做代码智能（无语言服务）：着色、查找替换（含正则）、跳转行、多光标、折叠、自动缩进、括号匹配、按单词补全、Git 改动行标记、外部修改检测、保存/全部保存、快速打开文件、在文件中查找。
- 着色、快捷键方案、配色主题都由插件提供：语言插件带自己语言的着色（java 带 Java、nodejs 带 JS/TS/JSON…），`lemon-paperplane-plugin-languages` 提供通用格式；快捷键方案是独立插件，默认 Eclipse（Mac 用 macOS 版、其他平台用 Windows 版，两版互相都能装），IntelliJ / VS Code / Vim / Emacs 在商店里装。

## 2. 加载与内存预算

| 状态 | 额外内存 |
|---|---|
| 没有编辑窗口 | 0（不创建 WebView） |
| 打开一个编辑窗口 | 网页进程约 40–60 MB + 编辑器约 5–15 MB |
| 关闭全部编辑窗口 | 几秒内回到打开前 |

- 编辑器网页首屏只加载内核（实测压缩后约 530 KB、gzip 约 170 KB，其中 CodeMirror 共享命名空间约 410 KB，必须整包给插件模块用，无法再裁）；语言着色模块按打开的文件类型懒加载；快捷键方案只加载当前选中的一个。实测打开一个普通文件 JS 堆约 10 MB。
- 文件树按需逐级展开，不预先遍历工程。
- 大文件：> 5 MB 只读打开并提示；> 50 MB 不打开；二进制文件不打开；图片不超过 8 MB 时在标签页里预览，关闭标签即释放。

## 3. 内核 API（同时是 MCP 工具，AI 也能读写工程文件）

路径一律是相对工程根的 `/` 分隔路径，内核拒绝越出工程根（含符号链接解析后）。

| 操作 | 输入 | 输出 |
|---|---|---|
| `fs.list` | `{projectId, path: ""}` | `{entries: [{name, path, dir, size, mtime, symlink, hidden, ignored}], truncated}`；目录在前、按名称排序；`ignored` 为 node_modules/build/.venv 等（界面淡化显示，可展开） |
| `fs.read` | `{projectId, path, encoding 可选}` | `{content, encoding, bom, lineEnding: lf\|crlf\|mixed, preferredLineEnding, size, mtime, binary, tooLarge, readOnly, image, dataUrl}`；`encoding` 支持 utf-8、utf-16le/be、gb18030、gbk、big5、latin1，传入时按它解码（「用其他编码重新打开」）；`content` 换行统一为 `\n`，`mixed` 时 `preferredLineEnding` 是占多数的一种，保存时用它；图片（png/jpg/gif/webp/bmp/ico/avif）`image=true`，不超过 8 MB 时 `dataUrl` 带内容 |
| `fs.write` | `{projectId, path, content, encoding 可选, bom 可选, lineEnding 可选, expectedMtime 可选, create 可选}` | `{path, exists, mtime, size}`；`expectedMtime` 与磁盘不一致时返回 `user_error`，`data.reason = "modifiedExternally"`；原子写入并保留权限 |
| `fs.stat` | `{projectId, paths}` | `[{path, exists, dir, size, mtime}]`（编辑器检测外部修改用） |
| `fs.create` | `{projectId, path, dir}` | `{path}` |
| `fs.rename` | `{projectId, from, to}` | `{path}` |
| `fs.delete` | `{projectId, paths}` | `{trashed: [...]}`；移到废纸篓，不直接删除 |
| `fs.search` | `{projectId, query, limit 可选=50}` | `[{path, name, score}]`；按文件名模糊匹配（快速打开），score 是整数，只用于排序 |
| `fs.grep` | `{projectId, query, regex, caseSensitive, include 可选 glob, limit 可选=500}` | `{matches: [{path, line, column, text}], truncated}`；跳过二进制与 > 2 MB 文件、ignored 目录 |

`mtime` 是 RFC3339Nano 字符串，原样回传给 `expectedMtime`。

设置 `settings.editor`：`{keymap, theme, fontSize, fontFamily, tabSize, insertSpaces, wordWrap, autoSave: off|onFocusChange|afterDelay, trimTrailingWhitespace, insertFinalNewline, showWhitespace}`。`keymap` 为空时按平台取默认（`eclipse-macos` / `eclipse-windows`）。

`ui.contributions` 增加 `languages`、`keymaps`、`editorThemes`（见第 4 节）。

## 4. 插件贡献点（SDK `manifest.go`）

```json
"languages": [
  {"id": "java", "name": "Java", "extensions": [".java"], "filenames": [], "filenamePatterns": [],
   "module": "editor/lang-java.js", "lineComment": "//", "blockComment": ["/*", "*/"], "priority": 0}
],
"keymaps": [
  {"id": "eclipse-macos", "name": "%keymap.eclipseMac%", "platform": "macos", "file": "keymaps/eclipse-macos.json"},
  {"id": "vim", "name": "Vim", "platform": "any", "module": "editor/keymap-vim.js", "base": "eclipse-macos"}
],
"editorThemes": [
  {"id": "darcula", "name": "Darcula", "kind": "dark", "file": "themes/darcula.json"}
]
```

- 文件放在插件目录的 `editor/`、`keymaps/`、`themes/` 下，内核经 `GET /api/v1/plugins/<id>/files/<path>?token=…` 提供（`Cache-Control: no-cache`，插件升级后不会用到旧模块）。因为地址带 token，模块必须是单文件、不能有相对 import。
- **语言模块**：ES module，`export default function (cm) { return <CodeMirror Extension 或 LanguageSupport> }`。`cm` 是编辑器提供的共享命名空间（`cm.state`、`cm.view`、`cm.language`、`cm.lezer.lr`、`cm.lezer.highlight`、`cm.legacy`（StreamLanguage 与 legacy-modes 工具）等），模块**不得**自带一份 @codemirror/* 或 @lezer/*（CodeMirror 要求单实例）。编辑器工程提供构建脚本把 npm 上的语言包打成这种模块。
- **快捷键方案**：JSON `{"bindings": [{"key": "Cmd-d", "command": "deleteLine"}, …]}`，键名用 CodeMirror 写法但不用 `Mod`（平台明确写 `Cmd`/`Ctrl`）；`command` 取编辑器命令表（`lemon-paperplane-main-editor/COMMANDS.md`）。需要模式编辑的（Vim/Emacs）用 `module`：`export default function (cm, commands) { return Extension }`，`base` 指定在哪个方案之上叠加，不写时叠加在平台默认方案（`eclipse-macos` / `eclipse-windows`）上。
- **配色主题**：JSON `{"kind": "dark", "colors": {"background": "#…", …}, "tokens": {"keyword": {"color": "#…", "fontStyle": "bold"}, …}}`，token 名取 @lezer/highlight 的 tags 名。

同一扩展名被多个插件声明时取 `priority` 高的；都没有时按纯文本打开。

## 5. 编辑器网页与宿主窗口

- 编辑器网页由内核在 `GET /editor/`（静态文件，不需要 token）提供，与 API 同源，网页直接 `fetch('/api/v1/call/…')`（Authorization 用地址里的 token）。
- 宿主打开：`http://127.0.0.1:<port>/editor/index.html#token=<token>&projectId=<id>&file=<相对路径>&line=<行>&locale=zh-Hans&theme=dark`
- 宿主 → 网页（执行 JS）：`paperplane.openFile(path, line)`、`paperplane.setTheme("light"|"dark")`、`paperplane.setLocale(l)`、`paperplane.requestClose()`（网页自己处理未保存提示，确认后发 `closeWindow`）、`paperplane.saveAll()`。
- 网页 → 宿主（Mac `webkit.messageHandlers.paperplane.postMessage`，Windows `chrome.webview.postMessage`，Linux 同 WebKit）：
  - `{type: "ready"}`
  - `{type: "title", title}`（窗口标题：工程名 — 当前文件）
  - `{type: "dirty", dirty: bool}`（窗口关闭按钮/菜单栏显示未保存点）
  - `{type: "closeWindow"}`（用户确认后真正关闭）
  - `{type: "closeCancelled"}`（用户在未保存提示里点了取消，宿主恢复正常状态）
  - `{type: "reveal", path}`（在访达/资源管理器中显示）
  - `{type: "openExternal", url}`
- 编辑器网页源码：`lemon-paperplane-main/lemon-paperplane-main-editor/`（TypeScript + esbuild），构建产物 `dist/` 由打包脚本放进 App 资源（Mac：`Contents/Resources/editor`），内核从 `<可执行文件>/../Resources/editor` 或环境变量 `PAPERPLANE_EDITOR_DIR` 读取。

## 终端页

`/editor/terminal.html#token=…&theme=dark|light`：原生界面「终端」分段里的终端区域（xterm.js 6 + fit / web-links / unicode11 / webgl 插件）。一个网页装下一个工作区的全部终端，同一时间显示一个；离开分段时宿主释放网页，终端本身在内核里继续跑。

- 宿主 → 网页（`window.paperplaneTerm`）：`show(id)` 显示（必要时创建）终端并聚焦；`sync(ids)` 关掉不在列表里的；`setTheme("light"|"dark")`；`focus()`；`size()` 返回当前区域能放下的 `{cols, rows}`（新建终端时按它启动 shell）。
- 网页 → 宿主（`messageHandlers.paperplane`）：`{type:"ready"}`、`{type:"closed", id}`（终端已在内核关闭）、`{type:"title", id, title}`（OSC 标题）、`{type:"openExternal", url}`（⌘ 点击链接）、`{type:"focus", value}`。
- 输出：只给正在显示的终端连 `GET /api/v1/terminals/<id>/stream`（WebKit 对同一主机最多 6 个连接），按已写入的字节偏移续传；断开后先用 `terminal.list` 确认终端还在再重连。
- 输入：`terminal.input` 一个一个按顺序发，前一个没回来时后面的合并，保证按键顺序。尺寸变化 60ms 防抖后发 `terminal.resize`；字体没测好时算出的过小行列（少于 10 列或 3 行）不发，避免 shell 按两三列重画提示符。
- macOS 快捷键与系统终端一致：⌘K 清屏，⌘←/→ 行首行尾，⌥←/→ 按词移动，⌘⌫ 删到行首，⌥⌫ 删词。配色来自设计变量的 console 与 ansi 色板。

