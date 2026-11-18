// 快捷键方案：插件 keymaps 贡献的 JSON 键位表（键 → 命令）或 module（Vim/Emacs 这类模式编辑）。
import { Prec, type Extension } from "@codemirror/state";
import { keymap, type KeyBinding } from "@codemirror/view";
import { standardKeymap, historyKeymap, indentLess } from "@codemirror/commands";
import { closeBracketsKeymap } from "@codemirror/autocomplete";
import type { KeymapInfo } from "./api";
import { fetchJSON } from "./api";
import { cm, ensureShared } from "./cm";
import { commands, runCommand, commandApi, insertIndent } from "./commands";
import { importModule } from "./languages";
import { isMac } from "./util";

export interface Binding { key: string; command: string }
export interface KeymapJSON { bindings: Binding[] }

/** 内置的基本键位：光标移动、回车、删除、括号与补全的特殊键；方案里的同键绑定优先 */
const baseKeymap: Extension = keymap.of([
  ...closeBracketsKeymap,
  ...standardKeymap,
  ...historyKeymap,
  { key: "Tab", run: insertIndent, shift: indentLess },
  { key: "Escape", run: (v) => runCommand("escape", v) },
]);

/** 键位缺失时的最小兜底（快捷键方案插件被禁用或加载失败时仍可保存、查找） */
const FALLBACK: Binding[] = [
  { key: "Mod-s", command: "save" }, { key: "Shift-Mod-s", command: "saveAll" }, { key: "Mod-f", command: "find" },
  { key: isMac ? "Alt-Mod-f" : "Mod-h", command: "replace" }, { key: "Mod-g", command: "findNext" }, { key: "Shift-Mod-g", command: "findPrevious" },
  { key: "Mod-/", command: "toggleLineComment" }, { key: "Mod-w", command: "closeTab" }, { key: "Mod-p", command: "quickOpen" },
  { key: "Shift-Mod-f", command: "findInFiles" }, { key: isMac ? "Mod-l" : "Ctrl-g", command: "goToLine" }, { key: "Mod-d", command: "selectNextOccurrence" },
  { key: "Ctrl-Space", command: "startCompletion" },
];

export interface LoadedKeymap {
  id: string;
  extension: Extension;
  /** 用于菜单显示快捷键：方案 + 基础方案的 JSON 键位 */
  bindings: Binding[];
  error?: string;
}

export async function loadKeymap(defs: KeymapInfo[], wanted: string, platformDefault: string): Promise<LoadedKeymap> {
  const def = defs.find((d) => d.id === wanted) ?? defs.find((d) => d.id === platformDefault) ?? null;
  if (!def) return { id: "", extension: [Prec.high(keymap.of(toKeyBindings(FALLBACK))), baseKeymap], bindings: FALLBACK };
  try {
    const parts: Extension[] = [];
    let bindings: Binding[] = [];
    if (def.module) {
      // 模式编辑方案叠加在 base（缺省为平台默认方案）之上
      await ensureShared();
      const mod = await importModule(def.module);
      const fn = mod.default ?? mod;
      parts.push(Prec.highest(await fn(cm, commandApi())));
      const base = defs.find((d) => d.id === (def.base || platformDefault) && d.file) ?? defs.find((d) => d.id === platformDefault && d.file);
      if (base) bindings = await loadBindings(base.file!);
    }
    if (def.file) bindings = bindings.concat(await loadBindings(def.file));
    if (!bindings.length) bindings = FALLBACK;
    parts.push(Prec.high(keymap.of(toKeyBindings(bindings))), baseKeymap);
    return { id: def.id, extension: parts, bindings };
  } catch (e: any) {
    console.error("[paperplane] keymap", def.id, e);
    return { id: def.id, extension: [Prec.high(keymap.of(toKeyBindings(FALLBACK))), baseKeymap], bindings: FALLBACK, error: e?.message || String(e) };
  }
}

async function loadBindings(ref: string): Promise<Binding[]> {
  const j = await fetchJSON<KeymapJSON>(ref);
  return (j.bindings ?? []).filter((b) => b && typeof b.key === "string" && typeof b.command === "string");
}

/** JSON 键位 → CodeMirror KeyBinding；界面级命令同时挂在 search-panel 与 global 作用域 */
export function toKeyBindings(list: Binding[]): KeyBinding[] {
  const out: KeyBinding[] = [];
  for (const b of list) {
    const c = commands[b.command];
    if (!c) { console.warn("[paperplane] keymap: unknown command", b.command); continue; }
    out.push({ key: b.key, run: (v) => runCommand(b.command, v), preventDefault: !!c.global, scope: c.global ? "editor search-panel global" : "editor" });
  }
  return out;
}

// ---------------------------------------------------------------------------
// 快捷键显示：Mac 用符号（⌃⌥⇧⌘），其他平台用 Ctrl+Shift+X

const KEY_NAMES: Record<string, [string, string]> = {
  ArrowUp: ["↑", "Up"], ArrowDown: ["↓", "Down"], ArrowLeft: ["←", "Left"], ArrowRight: ["→", "Right"],
  Enter: ["↩", "Enter"], Escape: ["⎋", "Esc"], Backspace: ["⌫", "Backspace"], Delete: ["⌦", "Delete"],
  Tab: ["⇥", "Tab"], Space: ["Space", "Space"], " ": ["Space", "Space"], PageUp: ["⇞", "PgUp"], PageDown: ["⇟", "PgDn"],
  Home: ["↖", "Home"], End: ["↘", "End"],
};

export function formatKey(key: string): string {
  return key.split(" ").map(formatStroke).join(isMac ? " " : ", ");
}

function formatStroke(stroke: string): string {
  const parts = stroke.split(/-(?!$)/);
  let name = parts.pop()!;
  let ctrl = false, alt = false, shift = false, meta = false;
  for (const p of parts) {
    const m = p.toLowerCase();
    if (m === "ctrl" || m === "c" || m === "control") ctrl = true;
    else if (m === "alt" || m === "a") alt = true;
    else if (m === "shift" || m === "s") shift = true;
    else if (m === "cmd" || m === "meta" || m === "m") meta = true;
    else if (m === "mod") { if (isMac) meta = true; else ctrl = true; }
  }
  const kn = KEY_NAMES[name];
  name = kn ? (isMac ? kn[0] : kn[1]) : name.length === 1 ? name.toUpperCase() : name;
  if (isMac) return (ctrl ? "⌃" : "") + (alt ? "⌥" : "") + (shift ? "⇧" : "") + (meta ? "⌘" : "") + name;
  return [ctrl && "Ctrl", meta && "Win", alt && "Alt", shift && "Shift", name].filter(Boolean).join("+");
}

/** 找某命令在当前方案里的第一个快捷键 */
export function shortcutFor(bindings: Binding[], command: string): string {
  const b = bindings.find((x) => x.command === command);
  return b ? formatKey(b.key) : "";
}
