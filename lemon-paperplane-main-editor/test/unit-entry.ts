// 单元测试入口：只导出不碰 DOM 的纯函数（由 test/unit.test.mjs 用 esbuild 打包后导入）。
export { globToRegExp, guessIndent, basename, dirname, extname } from "../src/util";
export { resolveLanguage, setLanguages } from "../src/languages";
export { blocksFromDiff } from "../src/gitgutter";
export { parseTag, themeFromJSON } from "../src/theme";
export { formatKey, toKeyBindings, shortcutFor } from "../src/keymaps";
export { commands, runCommand } from "../src/commands";
export { EditorState, EditorSelection } from "@codemirror/state";
