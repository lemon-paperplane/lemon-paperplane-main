# lemon-paperplane-main-editor

Lemon Paperplane 的文件编辑器网页（CodeMirror 6 + 原生 DOM，TypeScript + esbuild）。契约见 `../docs/editor.md`。

```bash
npm ci                 # registry 见 .npmrc（npmmirror）
npm run build          # → dist/（index.html、assets/、i18n/），内核在 /editor/ 下提供
npm run watch          # 开发时增量构建
npm test               # 命令表/快捷键方案一致性、插件模块可加载、纯函数单元测试
npm run typecheck
```

## 结构

- `src/main.ts` 布局、界面级命令、宿主桥接（`window.paperplane.*`）、焦点与外部修改检测（每 3 秒 `fs.stat`，窗口不可见时暂停）。
- `src/app.ts` 标签页：每个标签一个 `EditorState`，共用一个 `EditorView`；打开/保存（`expectedMtime`，冲突时覆盖/重新载入/对比）/关闭（释放状态）、自动保存、Git 标记刷新、导航历史。
- `src/commands.ts` 编辑命令表（全部命令见 `COMMANDS.md`）；`src/keymaps.ts` 快捷键方案加载（JSON 键位表或 module）。
- `src/languages.ts` 按文件名匹配插件 `languages`，懒加载语言模块；`src/cm.ts` 共享命名空间 `window.cm`。
- `src/theme.ts` Paperplane Light/Dark（CSS 变量，取自 `shared/design-tokens.json`）与插件 `editorThemes` JSON。
- `src/compare.ts` 对比视图（懒加载 `@codemirror/merge`）。
- `scripts/build.mjs` 生成设计变量 CSS、只内联用到的 `shared/icons`、摘出 `editor.*` 文案、打包并报告首屏大小。

## 语言与快捷键模块

插件 `editor/*.js` 是单文件 ES module：`export default function (cm, commands) { return Extension }`。
`@codemirror/{state,view,language,commands,search,autocomplete}` 与 `@lezer/{common,lr,highlight}` 不打进模块，改写为读 `window.cm`（单实例）；语法包照常打进去。模块经带 token 的地址加载，所以不能有相对 import。语言模块应返回 `LanguageSupport`（Markdown 代码块等嵌套语言靠它）。

```bash
node scripts/build-lang.mjs                    # 按 scripts/lang-modules.mjs 重建全部内置模块
node scripts/build-lang.mjs languages nodejs   # 只重建这些插件
node scripts/build-lang.mjs --package @codemirror/lang-rust --export rust --out ../x/editor/lang-rust.js
node scripts/build-lang.mjs --legacy shell --out ../x/editor/lang-shell.js
node scripts/build-lang.mjs --entry my-mode.js --out ../x/editor/lang-my.js
```

## 配色 JSON（插件 editorThemes）

`{"kind": "dark", "colors": {...}, "tokens": {"keyword": {"color": "#cc7832", "fontStyle": "bold"}}}`。
`colors` 支持：background、foreground、cursor、selection、selectionInactive、lineHighlight、gutterBackground、gutterForeground、gutterActiveForeground、gutterBorder、matchingBracket、searchMatch、searchMatchCurrent、selectionMatch、whitespace、foldPlaceholder、panelBackground。
`tokens` 的键是 `@lezer/highlight` 的 tags 名，可带修饰：`definition(variableName)`、`function(propertyName)`；没给的 token 用内置配色。
