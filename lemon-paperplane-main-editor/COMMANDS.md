# 编辑器命令表

快捷键方案（插件 `keymaps` 贡献的 JSON）只做「键 → 命令」映射：`{"bindings": [{"key": "Cmd-d", "command": "deleteLine"}]}`。

- 键名用 CodeMirror 写法：修饰键 `Cmd`、`Ctrl`、`Alt`、`Shift`（不用 `Mod`，按平台写明），键名如 `a`、`/`、`ArrowUp`、`Enter`、`Backspace`、`Delete`、`PageDown`、`F3`；多键序列用空格分隔（`Cmd-k Cmd-0`）。
- 同一个键可以绑多个命令，按顺序执行到第一个成功的为止。
- 「界面」列为是的命令在焦点不在编辑区（文件树、快速打开、查找框）时也响应。
- 方向键、Home/End、PageUp/PageDown、回车、删除、Tab/Shift-Tab、Esc、括号自动闭合的退格、补全列表里的上下/回车，由编辑器内置的基础键位处理，方案里同键的绑定优先。
- module 方案（Vim/Emacs）：`export default function (cm, commands) { return Extension }`，`commands.run(id, view)` 或 `commands[id](view)` 执行下表命令；`base` 缺省时叠加在平台默认方案（`eclipse-macos` / `eclipse-windows`）之上。
- 新增命令时同步改本表（`npm test` 会检查本表与源码、各快捷键方案一致）。

## 文件与标签页

| 命令 | 说明 | 界面 |
|---|---|---|
| `save` | 保存当前文件（与磁盘冲突时询问覆盖 / 重新载入 / 对比） | 是 |
| `saveAll` | 保存全部修改过的文件 | 是 |
| `closeTab` | 关闭当前标签（未保存时询问） | 是 |
| `closeOtherTabs` | 关闭其他标签 | 是 |
| `closeTabsToRight` | 关闭右侧标签 | 是 |
| `closeAllTabs` | 关闭全部标签 | 是 |
| `nextTab` | 下一个标签 | 是 |
| `previousTab` | 上一个标签 | 是 |
| `reopenClosedTab` | 重新打开刚关闭的标签 | 是 |
| `quickOpen` | 快速打开文件（按文件名模糊匹配，空查询列出最近文件） | 是 |
| `quickSwitchTab` | 在已打开的文件之间切换 | 是 |
| `newFile` | 在文件树选中的目录下新建文件 | 是 |
| `reloadFromDisk` | 从磁盘重新载入（有未保存修改时先问） | 是 |
| `compareWithDisk` | 与磁盘上的版本对比 | 是 |
| `revealInTree` | 在文件树中定位当前文件 | 是 |
| `revealInFinder` | 在访达 / 资源管理器中显示当前文件 | 是 |
| `toggleSidebar` | 显示 / 隐藏文件树 | 是 |
| `focusFileTree` | 焦点移到文件树 | 是 |
| `focusEditor` | 焦点回到编辑区 | 是 |
| `openSettings` | 编辑器设置（快捷键方案、配色、字号、缩进、自动保存…） | 是 |

## 查找与导航

| 命令 | 说明 | 界面 |
|---|---|---|
| `find` | 查找（选中文本作为查找内容） | 是 |
| `replace` | 查找替换 | 是 |
| `findNext` | 查找下一个 | |
| `findPrevious` | 查找上一个 | |
| `findNextSelection` | 用选中文本查找下一个（Eclipse ⌘K） | |
| `findPreviousSelection` | 用选中文本查找上一个 | |
| `selectAllMatches` | 选中全部查找结果（多光标） | |
| `replaceNext` | 替换当前并跳到下一个 | |
| `replaceAll` | 全部替换 | |
| `closeFind` | 关闭查找面板 | |
| `findInFiles` | 在文件中查找（工程内全文，结果可点击跳转） | 是 |
| `goToLine` | 跳转到行（`行` 或 `行:列`） | 是 |
| `navigateBack` | 后退到上一个位置 | 是 |
| `navigateForward` | 前进 | 是 |
| `nextChange` | 下一处 Git 改动 | |
| `previousChange` | 上一处 Git 改动 | |
| `cursorMatchingBracket` | 跳到匹配的括号 | |
| `selectMatchingBracket` | 选到匹配的括号 | |

## 编辑

| 命令 | 说明 | 界面 |
|---|---|---|
| `undo` | 撤销 | |
| `redo` | 重做 | |
| `undoSelection` | 撤销光标 / 选区变化 | |
| `redoSelection` | 重做光标 / 选区变化 | |
| `deleteLine` | 删除行 | |
| `duplicateLine` | 复制当前行到下方（同 `copyLineDown`） | |
| `copyLineDown` | 向下复制行 | |
| `copyLineUp` | 向上复制行 | |
| `duplicateLineOrSelection` | 有选区复制选区，否则复制行（IntelliJ ⌘D） | |
| `moveLineUp` | 上移行 | |
| `moveLineDown` | 下移行 | |
| `insertLineAfter` | 在下方插入新行 | |
| `insertLineBefore` | 在上方插入新行 | |
| `joinLines` | 合并行 | |
| `splitLine` | 在光标处断行，光标不动 | |
| `deleteWordBackward` | 删除前一个词 | |
| `deleteWordForward` | 删除后一个词 | |
| `deleteToLineEnd` | 删除到行尾 | |
| `deleteToLineStart` | 删除到行首 | |
| `transposeChars` | 交换光标两侧的字符 | |
| `toUpperCase` | 转大写（无选区时作用于光标处的词） | |
| `toLowerCase` | 转小写 | |
| `toggleCase` | 大小写切换 | |
| `trimTrailingWhitespace` | 去掉行尾空白 | |
| `toggleLineComment` | 切换行注释 | |
| `toggleBlockComment` | 切换块注释 | |
| `toggleComment` | 切换注释（有行注释用行注释，否则块注释） | |
| `addBlockComment` | 加块注释 | |
| `removeBlockComment` | 去掉块注释 | |
| `indentMore` | 增加缩进 | |
| `indentLess` | 减少缩进 | |
| `indentSelection` | 按语言重新缩进选中的行 | |
| `insertTab` | 插入缩进（有选区时整体缩进） | |
| `startCompletion` | 显示补全（按单词补全，语言模块可能提供更多） | |

## 选择与多光标

| 命令 | 说明 | 界面 |
|---|---|---|
| `selectAll` | 全选 | |
| `selectLine` | 选中当前行 | |
| `expandSelection` | 按语法扩大选区 | |
| `shrinkSelection` | 缩小选区（回到扩大前） | |
| `selectNextOccurrence` | 把下一个相同文本加入选区（多光标） | |
| `selectAllOccurrences` | 选中所有相同文本 | |
| `addCursorAbove` | 在上一行加光标 | |
| `addCursorBelow` | 在下一行加光标 | |
| `simplifySelection` | 只保留主光标 | |
| `escape` | 关闭补全 / 查找面板，否则只保留主光标 | |

## 光标移动

| 命令 | 说明 | 界面 |
|---|---|---|
| `cursorDocStart` | 到文件开头 | |
| `cursorDocEnd` | 到文件末尾 | |
| `selectDocStart` | 选到文件开头 | |
| `selectDocEnd` | 选到文件末尾 | |
| `cursorLineStart` | 到行首（先到缩进后） | |
| `cursorLineEnd` | 到行尾 | |
| `selectLineStart` | 选到行首 | |
| `selectLineEnd` | 选到行尾 | |
| `cursorWordLeft` | 左移一个词 | |
| `cursorWordRight` | 右移一个词 | |
| `selectWordLeft` | 向左选一个词 | |
| `selectWordRight` | 向右选一个词 | |
| `cursorSubwordLeft` | 左移一个子词（驼峰） | |
| `cursorSubwordRight` | 右移一个子词 | |
| `cursorPageUp` | 上翻一页 | |
| `cursorPageDown` | 下翻一页 | |

## 折叠与视图

| 命令 | 说明 | 界面 |
|---|---|---|
| `fold` | 折叠当前块 | |
| `unfold` | 展开当前块 | |
| `toggleFold` | 折叠 / 展开当前块 | |
| `foldAll` | 全部折叠 | |
| `unfoldAll` | 全部展开 | |
| `toggleWordWrap` | 自动换行开关（写回设置） | 是 |
| `toggleWhitespace` | 显示空白字符开关 | 是 |
| `zoomIn` | 放大字号（本窗口） | 是 |
| `zoomOut` | 缩小字号 | 是 |
| `resetZoom` | 恢复字号 | 是 |
