// 语言 / 快捷键模块都能被加载：用与编辑器相同形状的 window.cm 导入插件 editor/*.js，调用 default(cm)，
// 用返回的扩展建一个 EditorState 并解析示例文本。
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { pluginDirs, manifest, PLUGINS_ROOT } from "./helpers.mjs";
import { MODULES } from "../scripts/lang-modules.mjs";

import * as state from "@codemirror/state";
import * as view from "@codemirror/view";
import * as language from "@codemirror/language";
import * as commands from "@codemirror/commands";
import * as search from "@codemirror/search";
import * as autocomplete from "@codemirror/autocomplete";
import * as common from "@lezer/common";
import * as lr from "@lezer/lr";
import * as highlight from "@lezer/highlight";

const cm = {
  state, view, language, commands, search, autocomplete,
  lezer: { common, lr, highlight },
  legacy: { StreamLanguage: language.StreamLanguage, StringStream: language.StringStream },
  paperplane: { codeLanguages: [], version: "1" },
};
globalThis.cm = cm;

const fakeCommands = new Proxy({ run: () => true }, { get: (t, k) => (k in t ? t[k] : () => true) });

const SAMPLE = `// sample 1
function main() {
  let x = "text"; /* block */
  # hash comment
  if (x == 1) { return [1, 2.5, true, null]; }
}
<tag attr="v">body</tag>
key = value
`;

const langs = [];
for (const dir of pluginDirs()) {
  const m = manifest(dir);
  for (const l of m.contributes?.languages ?? []) langs.push({ dir, plugin: m.id, l });
}

test("各插件声明的语言模块都在 lang-modules.mjs 里（可以重新构建）", () => {
  const listed = new Set(MODULES.map((x) => `lemon-paperplane-plugin-${x.plugin}/${x.file}`));
  for (const { dir, l } of langs) {
    const rel = path.relative(PLUGINS_ROOT, path.join(dir, l.module));
    assert.ok(listed.has(rel), `${rel} 不在 scripts/lang-modules.mjs`);
  }
  assert.ok(langs.length >= 70, "语言数量异常：" + langs.length);
});

test("语言 id 不重复", () => {
  const ids = langs.map((x) => x.l.id);
  assert.deepEqual(ids.filter((x, i) => ids.indexOf(x) !== i), []);
});

const modules = new Map();
for (const { dir, l } of langs) modules.set(path.join(dir, l.module), l.name);

for (const [file, name] of modules) {
  test(`语言模块 ${path.relative(PLUGINS_ROOT, file)}`, async () => {
    assert.ok(fs.existsSync(file), "文件不存在");
    const src = fs.readFileSync(file, "utf8");
    assert.ok(!/from\s*["'](?:@codemirror|@lezer)\//.test(src), "模块里还有对 @codemirror/@lezer 的 import");
    const mod = await import(pathToFileURL(file).href);
    assert.equal(typeof mod.default, "function", "default 导出不是函数");
    const ext = await mod.default(cm);
    assert.ok(ext, "没有返回扩展");
    assert.ok(ext instanceof language.LanguageSupport || ext instanceof language.Language, `${name}: 应返回 LanguageSupport`);
    const st = state.EditorState.create({ doc: SAMPLE, extensions: [ext] });
    const tree = language.ensureSyntaxTree(st, st.doc.length, 5000);
    assert.ok(tree, `${name}: 解析超时`);
    assert.ok(st.facet(language.language), `${name}: 没有设置 language`);
  });
}

for (const dir of pluginDirs()) {
  for (const k of manifest(dir).contributes?.keymaps ?? []) {
    if (!k.module) continue;
    test(`快捷键模块 ${k.id}`, async () => {
      const mod = await import(pathToFileURL(path.join(dir, k.module)).href);
      const ext = await mod.default(cm, fakeCommands);
      assert.ok(ext, "没有返回扩展");
      state.EditorState.create({ doc: "abc", extensions: [ext] });
    });
  }
}
