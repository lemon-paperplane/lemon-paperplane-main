// 命令表与快捷键方案的一致性：COMMANDS.md == 源码命令；每个方案里的 command 都存在、键名合法、不重复。
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { documentedCommands, sourceCommands, pluginDirs, manifest } from "./helpers.mjs";

const MODS = new Set(["Cmd", "Ctrl", "Alt", "Shift"]);
const NAMED = new Set(["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Enter", "Escape", "Backspace", "Delete", "Tab", "Space",
  "Home", "End", "PageUp", "PageDown", "Insert", ...Array.from({ length: 12 }, (_, i) => "F" + (i + 1))]);

test("COMMANDS.md 与源码命令一致", () => {
  const doc = documentedCommands();
  const src = sourceCommands();
  assert.ok(src.size > 90, "源码命令数量异常：" + src.size);
  assert.deepEqual([...src].filter((c) => !doc.has(c)), [], "源码里有、COMMANDS.md 没写的命令");
  assert.deepEqual([...doc].filter((c) => !src.has(c)), [], "COMMANDS.md 写了、源码里没有的命令");
});

const keymapFiles = [];
for (const dir of pluginDirs()) {
  for (const k of manifest(dir).contributes?.keymaps ?? []) keymapFiles.push({ dir, k });
}

test("找到全部快捷键方案", () => {
  const ids = keymapFiles.map((x) => x.k.id).sort();
  for (const id of ["eclipse-macos", "eclipse-windows", "intellij-macos", "intellij-windows", "vscode-macos", "vscode-windows", "vim", "emacs"]) {
    assert.ok(ids.includes(id), "缺少方案 " + id);
  }
});

for (const { dir, k } of keymapFiles) {
  test(`快捷键方案 ${k.id}`, () => {
    assert.ok(k.file || k.module, "file 与 module 至少一个");
    if (k.module) assert.ok(fs.existsSync(path.join(dir, k.module)), "模块不存在 " + k.module);
    if (!k.file) return;
    const j = JSON.parse(fs.readFileSync(path.join(dir, k.file), "utf8"));
    const cmds = documentedCommands();
    const seen = new Set();
    const isMac = k.platform === "macos";
    for (const b of j.bindings) {
      assert.ok(cmds.has(b.command), `${k.id}: 未知命令 ${b.command}`);
      const sig = b.key + " → " + b.command;
      assert.ok(!seen.has(sig), `${k.id}: 重复绑定 ${sig}`);
      seen.add(sig);
      for (const stroke of b.key.split(" ")) {
        const parts = stroke.split(/-(?!$)/);
        const name = parts.pop();
        for (const m of parts) assert.ok(MODS.has(m), `${k.id}: 键 ${b.key} 的修饰键 ${m} 不合法（不要用 Mod）`);
        assert.ok(name.length === 1 || NAMED.has(name), `${k.id}: 键 ${b.key} 的键名 ${name} 不合法`);
        if (!isMac) assert.ok(!parts.includes("Cmd"), `${k.id}: Windows 方案不应使用 Cmd（${b.key}）`);
      }
    }
    // 常用命令都要有键
    for (const c of ["save", "saveAll", "find", "findInFiles", "quickOpen", "goToLine", "closeTab", "undo", "redo", "toggleLineComment", "deleteLine", "moveLineUp", "moveLineDown", "nextTab", "previousTab"]) {
      if (k.id.startsWith("intellij") && c === "save") continue; // IntelliJ 的 ⌘S 就是全部保存
      if (k.id.startsWith("vscode") && c === "deleteLine") continue;
      assert.ok(j.bindings.some((b) => b.command === c), `${k.id}: 常用命令 ${c} 没有快捷键`);
    }
  });
}

test("同一方案里一个键不会指向互相冲突的多个界面命令", () => {
  for (const { dir, k } of keymapFiles) {
    if (!k.file) continue;
    const j = JSON.parse(fs.readFileSync(path.join(dir, k.file), "utf8"));
    const byKey = new Map();
    for (const b of j.bindings) byKey.set(b.key, [...(byKey.get(b.key) ?? []), b.command]);
    for (const [key, list] of byKey) assert.ok(list.length === 1, `${k.id}: 键 ${key} 绑定了多个命令 ${list.join(", ")}`);
  }
});
