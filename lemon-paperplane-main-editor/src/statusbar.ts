// 状态栏：行:列、选择数、缩进、编码、换行符、语言、只读；可点击切换。
import { app, on, stateOf, setTabIndent, setTabLineEnding, setTabEncoding, setTabLanguage, reloadTab, statusFlash, detectIndent } from "./app";
import { h } from "./util";
import { icon, showMenu, type MenuItem } from "./ui";
import { t } from "./i18n";
import { allLanguages } from "./languages";
import { runCommand } from "./commands";

const ENCODINGS: [string, string][] = [
  ["utf-8", "UTF-8"], ["utf-16le", "UTF-16 LE"], ["utf-16be", "UTF-16 BE"], ["gb18030", "GB18030"], ["gbk", "GBK"], ["big5", "Big5"], ["latin1", "ISO-8859-1"],
];

let bar: HTMLElement;
let msg: HTMLElement, pos: HTMLElement, indentEl: HTMLElement, encEl: HTMLElement, leEl: HTMLElement, langEl: HTMLElement, roEl: HTMLElement;
let timer: ReturnType<typeof setTimeout> | undefined;
let listening = false;

const item = (cls: string, tip: string, onclick?: (e: MouseEvent) => void) =>
  h("button.pp-status-item." + cls, { type: "button", "data-tip": tip, onclick, disabled: !onclick });

export function createStatusBar(extra: HTMLElement[]): HTMLElement {
  msg = h("span.pp-status-msg");
  roEl = h("span.pp-status-item.ro", { "data-tip": t("editor.status.readOnly") }, icon("lock", 12), h("span", null, t("editor.status.readOnly")));
  pos = item("pos", t("editor.status.goToLine"), () => runCommand("goToLine", app.view));
  indentEl = item("indent", t("editor.status.indent"), indentMenu);
  encEl = item("enc", t("editor.status.encoding"), encodingMenu);
  leEl = item("le", t("editor.status.lineEnding"), lineEndingMenu);
  langEl = item("lang", t("editor.status.language"), languageMenu);
  bar = h("div.pp-statusbar", null, msg, h("span.pp-flex"), roEl, pos, indentEl, encEl, leEl, langEl, ...extra);
  // 合并同一轮的多次更新（不用 requestAnimationFrame：窗口在后台时它不跑，状态会过时）
  const schedule = () => { if (!timer) timer = setTimeout(() => { timer = undefined; render(); }, 16); };
  if (!listening) {
    listening = true;
    on("status", schedule);
    on("active", schedule);
  }
  schedule();
  return bar;
}

function render() {
  const tab = app.active;
  msg.textContent = statusFlash();
  const text = tab?.kind === "text";
  for (const el of [pos, indentEl, encEl, leEl, langEl]) el.hidden = !text;
  roEl.hidden = !(text && tab!.readOnly);
  if (!text || !tab) return;
  const st = stateOf(tab)!;
  const sel = st.selection;
  const head = sel.main.head;
  const line = st.doc.lineAt(head);
  let col = 1;
  for (let i = line.from; i < head; i++) col = st.sliceDoc(i, i + 1) === "\t" ? col + st.tabSize - ((col - 1) % st.tabSize) : col + 1;
  let chars = 0, lines = 0;
  for (const r of sel.ranges) if (!r.empty) { chars += r.to - r.from; lines += st.doc.lineAt(r.to).number - st.doc.lineAt(r.from).number + 1; }
  let p = `${line.number}:${col}`;
  if (sel.ranges.length > 1) p += "  " + t("editor.status.cursors", sel.ranges.length);
  if (chars) p += "  " + (lines > 1 ? t("editor.status.selectionLines", chars, lines) : t("editor.status.selection", chars));
  pos.textContent = p;
  indentEl.textContent = tab.indent.spaces ? t("editor.status.spaces", tab.indent.size) : t("editor.status.tabs", tab.indent.size);
  encEl.textContent = (ENCODINGS.find(([k]) => k === tab.encoding)?.[1] ?? tab.encoding.toUpperCase()) + (tab.bom ? " BOM" : "");
  leEl.textContent = tab.lineEnding === "crlf" ? "CRLF" : tab.lineEnding === "mixed" ? t("editor.status.mixed") : "LF";
  langEl.textContent = tab.lang?.name ?? t("editor.status.plainText");
}

function indentMenu(e: MouseEvent) {
  const tab = app.active;
  if (!tab) return;
  const items: MenuItem[] = [
    { label: t("editor.indent.useSpaces"), checked: tab.indent.spaces, run: () => setTabIndent(tab, { ...tab.indent, spaces: true }) },
    { label: t("editor.indent.useTabs"), checked: !tab.indent.spaces, run: () => setTabIndent(tab, { ...tab.indent, spaces: false }) },
    { divider: true },
    ...[2, 3, 4, 8].map((n) => ({ label: t("editor.indent.width", n), checked: tab.indent.size === n, run: () => setTabIndent(tab, { ...tab.indent, size: n }) })),
    { divider: true },
    { label: t("editor.indent.detect"), run: () => { const d = detectIndent(stateOf(tab)!.doc.toString()); if (d) setTabIndent(tab, d); } },
  ];
  showMenu(items, e.currentTarget as HTMLElement, { above: true, align: "end" });
}

function encodingMenu(e: MouseEvent) {
  const tab = app.active;
  if (!tab) return;
  const items: MenuItem[] = [
    { header: t("editor.encoding.saveWith") },
    ...ENCODINGS.map(([k, label]) => ({ label, checked: tab.encoding === k && !tab.bom, run: () => setTabEncoding(tab, k, false) })),
    { label: "UTF-8 BOM", checked: tab.encoding === "utf-8" && tab.bom, run: () => setTabEncoding(tab, "utf-8", true) },
    { divider: true },
    { header: t("editor.encoding.reopenWith") },
    ...ENCODINGS.map(([k, label]) => ({ label, disabled: tab.dirty, run: () => void reloadTab(tab, k) })),
    { divider: true },
    { label: t("editor.encoding.reload"), icon: "refresh-cw", disabled: tab.dirty, run: () => void reloadTab(tab) },
  ];
  showMenu(items, e.currentTarget as HTMLElement, { above: true, align: "end" });
}

function lineEndingMenu(e: MouseEvent) {
  const tab = app.active;
  if (!tab) return;
  showMenu([
    { label: "LF", checked: tab.lineEnding === "lf", run: () => setTabLineEnding(tab, "lf") },
    { label: "CRLF", checked: tab.lineEnding === "crlf", run: () => setTabLineEnding(tab, "crlf") },
  ], e.currentTarget as HTMLElement, { above: true, align: "end" });
}

function languageMenu(e: MouseEvent) {
  const tab = app.active;
  if (!tab) return;
  const langs = [...allLanguages()].sort((a, b) => a.name.localeCompare(b.name));
  const items: MenuItem[] = [
    { label: t("editor.status.plainText"), checked: !tab.lang, run: () => setTabLanguage(tab, "") },
    { divider: true },
    ...langs.map((l) => ({ label: l.name, checked: tab.lang?.id === l.id, run: () => setTabLanguage(tab, l.id) })),
  ];
  showMenu(items, e.currentTarget as HTMLElement, { above: true, align: "end" });
}
