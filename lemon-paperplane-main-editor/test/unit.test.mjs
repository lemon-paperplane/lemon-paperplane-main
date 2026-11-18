// 纯函数单元测试：语言匹配、通配符、缩进推断、Git 改动块、主题 token、快捷键显示、编辑命令。
import { test, before } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import * as esbuild from "esbuild";
import { ROOT } from "./helpers.mjs";

let U;
before(async () => {
  // 生成的设计变量/图标在 src/gen，先确保存在
  if (!fs.existsSync(path.join(ROOT, "src/gen/icons.ts"))) throw new Error("先运行 npm run build 生成 src/gen");
  const out = path.join(ROOT, "node_modules/.cache/pp-editor-test/unit.mjs");
  await esbuild.build({ entryPoints: [path.join(ROOT, "test/unit-entry.ts")], bundle: true, format: "esm", platform: "node", outfile: out, logLevel: "silent", loader: { ".css": "empty" } });
  U = await import(pathToFileURL(out).href + "?t=" + Date.now());
});

test("globToRegExp", () => {
  assert.ok(U.globToRegExp("Dockerfile.*").test("Dockerfile.dev"));
  assert.ok(U.globToRegExp("*.gradle.kts").test("build.gradle.kts"));
  assert.ok(!U.globToRegExp("*.json").test("a/b.json"));
  assert.ok(U.globToRegExp("**/*.conf").test("a/b/c.conf"));
  assert.ok(U.globToRegExp("tsconfig.*.json").test("tsconfig.app.json"));
});

test("resolveLanguage：文件名 > 通配 > 扩展名（最长）> priority", () => {
  U.setLanguages([
    { id: "json", name: "JSON", extensions: [".json"], module: "m1", plugin: "nodejs" },
    { id: "jsonc", name: "JSONC", filenames: ["tsconfig.json"], filenamePatterns: ["tsconfig.*.json"], module: "m2", plugin: "nodejs" },
    { id: "uni", name: "uni", filenames: ["pages.json"], module: "m3", plugin: "uniapp", priority: 10 },
    { id: "kotlin", name: "Kotlin", extensions: [".kt", ".kts"], module: "m4", plugin: "kotlin" },
    { id: "groovy", name: "Groovy", extensions: [".gradle"], module: "m5", plugin: "gradle" },
    { id: "docker", name: "Dockerfile", filenames: ["Dockerfile"], filenamePatterns: ["Dockerfile.*"], module: "m6", plugin: "languages" },
    { id: "a", name: "A", extensions: [".x"], module: "m7", plugin: "p", priority: 1 },
    { id: "b", name: "B", extensions: [".x"], module: "m8", plugin: "q", priority: 5 },
  ]);
  const id = (p) => U.resolveLanguage(p)?.id ?? null;
  assert.equal(id("src/a.json"), "json");
  assert.equal(id("tsconfig.json"), "jsonc");
  assert.equal(id("web/tsconfig.app.json"), "jsonc");
  assert.equal(id("pages.json"), "uni");
  assert.equal(id("build.gradle.kts"), "kotlin");
  assert.equal(id("build.gradle"), "groovy");
  assert.equal(id("Dockerfile"), "docker");
  assert.equal(id("Dockerfile.prod"), "docker");
  assert.equal(id("A.JSON"), "json");
  assert.equal(id("z.x"), "b");
  assert.equal(id("README"), null);
  assert.equal(id(".json"), null);
});

test("guessIndent", () => {
  assert.deepEqual(U.guessIndent("a\n  b\n    c\n  d\n", 4), { spaces: true, size: 2 });
  assert.deepEqual(U.guessIndent("a\n\tb\n\t\tc\n", 4), { spaces: false, size: 4 });
  assert.deepEqual(U.guessIndent("class A {\n    void f() {\n        x();\n    }\n}\n", 2), { spaces: true, size: 4 });
  assert.equal(U.guessIndent("a\nb\n", 4), null);
});

test("blocksFromDiff：新增 / 修改 / 删除", () => {
  const d = { path: "a", hunks: [
    { oldStart: 2, oldLines: 0, newStart: 3, newLines: 2, lines: [{ kind: "+", new: 3, text: "x" }, { kind: "+", new: 4, text: "y" }] },
    { oldStart: 10, oldLines: 1, newStart: 12, newLines: 1, lines: [{ kind: "-", old: 10, text: "old" }, { kind: "+", new: 12, text: "new" }] },
    { oldStart: 20, oldLines: 2, newStart: 21, newLines: 0, lines: [{ kind: "-", old: 20, text: "d1" }, { kind: "-", old: 21, text: "d2" }] },
    { oldStart: 1, oldLines: 1, newStart: 0, newLines: 0, lines: [{ kind: "-", old: 1, text: "first" }] },
  ] };
  assert.deepEqual(U.blocksFromDiff(d), [
    { kind: "added", from: 3, to: 4, oldLines: [] },
    { kind: "modified", from: 12, to: 12, oldLines: ["old"] },
    { kind: "deleted", from: 21, to: 21, oldLines: ["d1", "d2"] },
    { kind: "deleted", from: 0, to: 0, oldLines: ["first"] },
  ]);
  assert.deepEqual(U.blocksFromDiff({ path: "b", binary: true, hunks: [] }), []);
  assert.deepEqual(U.blocksFromDiff(null), []);
});

test("parseTag", () => {
  assert.ok(U.parseTag("keyword"));
  assert.ok(U.parseTag("definition(variableName)"));
  assert.ok(U.parseTag("function(definition(variableName))"));
  assert.equal(U.parseTag("noSuchTag"), null);
});

test("formatKey 与 toKeyBindings", () => {
  const mac = /Mac/.test(navigator.platform);
  assert.equal(U.formatKey("Shift-Ctrl-s"), mac ? "⌃⇧S" : "Ctrl+Shift+S");
  assert.equal(U.formatKey("Shift-Cmd-s"), mac ? "⇧⌘S" : "Win+Shift+S");
  assert.equal(U.formatKey("Ctrl-k Ctrl-0"), mac ? "⌃K ⌃0" : "Ctrl+K, Ctrl+0");
  assert.equal(U.formatKey("Alt-ArrowUp"), mac ? "⌥↑" : "Alt+Up");
  assert.equal(U.formatKey("Ctrl--"), mac ? "⌃-" : "Ctrl+-");
  const kb = U.toKeyBindings([{ key: "Ctrl-s", command: "deleteLine" }, { key: "Ctrl-q", command: "noSuchCommand" }]);
  assert.equal(kb.length, 1);
  assert.equal(kb[0].scope, "editor");
  assert.equal(U.shortcutFor([{ key: "Ctrl-d", command: "deleteLine" }], "deleteLine"), mac ? "⌃D" : "Ctrl+D");
});

/** 在无 DOM 的「视图」上跑编辑命令：只需要 state 与 dispatch */
function run(cmd, doc, anchor, head = anchor) {
  let st = U.EditorState.create({ doc, selection: U.EditorSelection.single(anchor, head), extensions: [U.EditorState.allowMultipleSelections.of(true)] });
  const view = { get state() { return st; }, dispatch(tr) { st = (tr.state ? tr : st.update(tr)).state; } };
  const ok = U.commands[cmd].run(view);
  return { ok, doc: st.doc.toString(), sel: st.selection.main };
}

test("编辑命令：joinLines / insertLineBefore / toUpperCase / toggleCase / duplicateLineOrSelection", () => {
  assert.equal(run("joinLines", "a  \n    b\nc", 0).doc, "a b\nc");
  const r = run("insertLineBefore", "  x\n", 3);
  assert.equal(r.doc, "  \n  x\n");
  assert.equal(r.sel.head, 2);
  assert.equal(run("toUpperCase", "hello world", 2).doc, "HELLO world");
  assert.equal(run("toggleCase", "ABC def", 0, 3).doc, "abc def");
  assert.equal(run("duplicateLineOrSelection", "ab\n", 0, 2).doc, "abab\n");
  assert.equal(run("duplicateLineOrSelection", "ab\n", 1).doc, "ab\nab\n");
});

test("插件配色 JSON → 扩展", () => {
  const ext = U.themeFromJSON({ kind: "dark", colors: { background: "#2b2b2b", foreground: "#a9b7c6", selection: "#214283" }, tokens: { keyword: { color: "#cc7832", fontStyle: "bold" }, "definition(variableName)": { color: "#ffc66d" }, bogus: { color: "#f00" } } });
  const st = U.EditorState.create({ doc: "x", extensions: [ext] });
  assert.ok(st);
});
