// 顶部弹出的输入面板：快速打开文件（fs.search）、切换已打开文件、跳转行。
import { app, openFile, stateOf, pushHistory, activate, focusEditor } from "./app";
import { call } from "./api";
import { h, basename, dirname, debounce } from "./util";
import { t } from "./i18n";
import { fileIcon } from "./fileicons";
import { recentFiles } from "./app";
import { EditorView } from "@codemirror/view";

interface Item { path: string; label?: string }

let open: { close: () => void } | null = null;

function frame(input: HTMLInputElement, list: HTMLElement | null, onClose: () => void): { box: HTMLElement; close: () => void } {
  open?.close();
  const prevFocus = document.activeElement as HTMLElement | null;
  const box = h("div.pp-palette", { role: "dialog" }, h("div.pp-palette-input", null, input), list);
  const host = document.getElementById("pp-island")!;
  host.appendChild(box);
  input.focus();
  const onDown = (e: MouseEvent) => { if (!box.contains(e.target as Node)) close(); };
  setTimeout(() => document.addEventListener("mousedown", onDown, true));
  let closed = false;
  function close() {
    if (closed) return;
    closed = true;
    box.remove();
    document.removeEventListener("mousedown", onDown, true);
    if (open?.close === close) open = null;
    onClose();
    if (document.activeElement === document.body || !document.activeElement) {
      if (app.active?.kind === "text") focusEditor(); else prevFocus?.focus();
    }
  }
  open = { close };
  return { box, close };
}

/** 模糊匹配高亮：按顺序找到的字符加粗 */
function highlight(name: string, q: string): (Node | string)[] {
  if (!q) return [name];
  const lower = name.toLowerCase();
  const ql = q.toLowerCase();
  const sub = lower.indexOf(ql);
  if (sub >= 0) return [name.slice(0, sub), h("b", null, name.slice(sub, sub + q.length)), name.slice(sub + q.length)];
  const out: (Node | string)[] = [];
  let j = 0, run = "";
  for (let i = 0; i < name.length; i++) {
    if (j < ql.length && lower[i] === ql[j]) {
      if (run) { out.push(run); run = ""; }
      out.push(h("b", null, name[i]));
      j++;
    } else run += name[i];
  }
  if (run) out.push(run);
  return out;
}

/** 快速打开：空查询时列出最近打开的文件；mode=tabs 只列已打开的标签 */
export function quickOpen(mode: "files" | "tabs" = "files") {
  const input = h("input.pp-palette-field", { type: "text", placeholder: t(mode === "tabs" ? "editor.quickOpen.tabsPlaceholder" : "editor.quickOpen.placeholder"), spellcheck: false, autocomplete: "off" });
  const list = h("div.pp-palette-list", { role: "listbox" });
  let items: Item[] = [];
  let sel = 0;
  let seq = 0;
  const { close } = frame(input, list, () => search.cancel());

  function render(q: string) {
    if (!items.length) {
      list.replaceChildren(h("div.pp-palette-empty", null, q ? t("editor.quickOpen.noResults") : t("editor.quickOpen.noRecent")));
      return;
    }
    sel = Math.min(sel, items.length - 1);
    list.replaceChildren(...items.map((it, i) => {
      const el = h("div.pp-palette-item" + (i === sel ? ".active" : ""), { role: "option" },
        fileIcon(it.path),
        h("span.pp-palette-name", null, ...highlight(basename(it.path), q)),
        h("span.pp-palette-dir", null, dirname(it.path)),
        app.tabs.some((x) => x.path === it.path && x.dirty) ? h("span.pp-palette-dot") : null);
      el.addEventListener("mousedown", (e) => e.preventDefault());
      el.addEventListener("mousemove", () => { if (sel !== i) { sel = i; mark(); } });
      el.addEventListener("click", () => choose(i));
      return el;
    }));
  }
  function mark() {
    list.querySelectorAll(".pp-palette-item").forEach((el, i) => el.classList.toggle("active", i === sel));
    list.children[sel]?.scrollIntoView({ block: "nearest" });
  }
  function choose(i: number) {
    const it = items[i];
    if (!it) return;
    close();
    void openFile(it.path);
  }
  function local(q: string): Item[] {
    const tabs = app.tabs.map((x) => x.path);
    const base = mode === "tabs" ? [...tabs].sort((a, b) => recentRank(a) - recentRank(b)) : [...new Set([...recentFiles(), ...tabs])];
    const ql = q.toLowerCase();
    return base.filter((p) => !ql || fuzzy(p.toLowerCase(), ql)).slice(0, 50).map((path) => ({ path }));
  }
  const search = debounce(async (q: string) => {
    const my = ++seq;
    try {
      const hits = await call<{ path: string }[]>("fs.search", { projectId: app.projectId, query: q, limit: 50 });
      if (my !== seq) return;
      items = (hits ?? []).map((x) => ({ path: x.path }));
      sel = 0;
      render(q);
    } catch { /* 保持现有列表 */ }
  }, 120);
  input.addEventListener("input", () => {
    const q = input.value.trim();
    if (mode === "tabs" || !q) {
      search.cancel();
      seq++;
      items = local(q);
      sel = mode === "tabs" && !q && items.length > 1 ? 1 : 0;
      render(q);
    } else search(q);
  });
  input.addEventListener("keydown", (e) => {
    if (e.key === "ArrowDown") { sel = Math.min(sel + 1, items.length - 1); mark(); }
    else if (e.key === "ArrowUp") { sel = Math.max(sel - 1, 0); mark(); }
    else if (e.key === "PageDown") { sel = Math.min(sel + 10, items.length - 1); mark(); }
    else if (e.key === "PageUp") { sel = Math.max(sel - 10, 0); mark(); }
    else if (e.key === "Enter" && !e.isComposing) choose(sel);
    else if (e.key === "Escape") close();
    else return;
    e.preventDefault();
    e.stopPropagation();
  });
  items = local("");
  sel = mode === "tabs" && items.length > 1 ? 1 : 0;
  render("");
}

function recentRank(p: string): number {
  const i = recentFiles().indexOf(p);
  return i < 0 ? 999 : i;
}

function fuzzy(s: string, q: string): boolean {
  let j = 0;
  for (let i = 0; i < s.length && j < q.length; i++) if (s[i] === q[j]) j++;
  return j === q.length;
}

/** 跳转到行[:列] */
export function goToLine() {
  const tab = app.active;
  if (tab?.kind !== "text") return;
  const st = stateOf(tab)!;
  const cur = st.doc.lineAt(st.selection.main.head);
  const input = h("input.pp-palette-field", {
    type: "text", spellcheck: false, autocomplete: "off",
    placeholder: t("editor.goToLine.placeholder", cur.number, st.doc.lines),
  });
  const hint = h("div.pp-palette-hint", null, t("editor.goToLine.hint", 1, st.doc.lines));
  const { close } = frame(input, hint, () => {});
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.isComposing) {
      e.preventDefault();
      const m = /^\s*(\d+)?\s*(?:[:,]\s*(\d+))?\s*$/.exec(input.value);
      if (!m || (!m[1] && !m[2])) { input.classList.add("invalid"); return; }
      const view = app.view;
      const s = view.state;
      const ln = Math.max(1, Math.min(m[1] ? +m[1] : cur.number, s.doc.lines));
      const line = s.doc.line(ln);
      const pos = Math.min(line.from + Math.max(0, (m[2] ? +m[2] : 1) - 1), line.to);
      close();
      pushHistory();
      view.dispatch({ selection: { anchor: pos }, effects: EditorView.scrollIntoView(pos, { y: "center" }) });
      activate(tab);
    } else if (e.key === "Escape") {
      e.preventDefault();
      close();
    }
    e.stopPropagation();
  });
  input.addEventListener("input", () => input.classList.remove("invalid"));
}

export function closePalette() {
  open?.close();
}
