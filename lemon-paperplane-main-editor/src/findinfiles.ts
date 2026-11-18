// 在文件中查找（fs.grep）：编辑区下方可拖动高度的面板，结果按文件分组，点击跳转并选中匹配。
import { app, openFile, stateOf } from "./app";
import { call, type FSMatch } from "./api";
import { h, basename, dirname, debounce, escapeRegExp, store, clamp } from "./util";
import { icon, iconButton } from "./ui";
import { t } from "./i18n";
import { fileIcon } from "./fileicons";

let panel: HTMLElement | null = null;
let input: HTMLInputElement;
let include: HTMLInputElement;
let results: HTMLElement;
let summary: HTMLElement;
let opts = store.get("fif.opts", { caseSensitive: false, wholeWord: false, regex: false });
let seq = 0;
let host: HTMLElement;

export function initFindInFiles(container: HTMLElement) {
  host = container;
}

export function findInFilesOpen(): boolean {
  return !!panel?.isConnected;
}

export function closeFindInFiles() {
  panel?.remove();
  panel = null;
  if (app.active?.kind === "text") app.view.focus();
}

export function openFindInFiles() {
  // 有选中文本时作为查找内容
  let initial = "";
  const tab = app.active;
  if (tab?.kind === "text") {
    const st = stateOf(tab)!;
    const s = st.selection.main;
    if (!s.empty && s.to - s.from < 200) {
      const text = st.sliceDoc(s.from, s.to);
      if (!text.includes("\n")) initial = text;
    }
  }
  if (!panel) build();
  if (initial) { input.value = initial; void run(); }
  input.focus();
  input.select();
}

function build() {
  const toggle = (name: string, tip: string, key: keyof typeof opts) => {
    const b = h("button.pp-find-toggle" + (opts[key] ? ".on" : ""), { type: "button", "data-tip": tip, "aria-label": tip, "aria-pressed": String(opts[key]) }, icon(name, 16));
    b.addEventListener("mousedown", (e) => e.preventDefault());
    b.addEventListener("click", () => {
      opts = { ...opts, [key]: !opts[key] };
      store.set("fif.opts", opts);
      b.classList.toggle("on", opts[key]);
      b.setAttribute("aria-pressed", String(opts[key]));
      void run();
    });
    return b;
  };
  input = h("input.pp-find-input", { type: "text", placeholder: t("editor.findInFiles.placeholder"), spellcheck: false, autocomplete: "off", "aria-label": t("editor.findInFiles.placeholder") });
  include = h("input.pp-find-input", { type: "text", placeholder: t("editor.findInFiles.include"), spellcheck: false, autocomplete: "off", value: store.get("fif.include", ""), "aria-label": t("editor.findInFiles.include") });
  results = h("div.pp-fif-results", { tabIndex: -1 });
  summary = h("span.pp-find-count");
  const grip = h("div.pp-fif-grip");
  panel = h("div.pp-fif", { style: { height: store.get("fif.height", 260) + "px" } },
    grip,
    h("div.pp-fif-head", null,
      h("span.pp-fif-title", null, icon("text-search", 16), t("editor.findInFiles.title")),
      h("div.pp-find-field.grow", null, icon("search", 14, "lead"), input,
        toggle("case-sensitive", t("editor.find.matchCase"), "caseSensitive"),
        toggle("whole-word", t("editor.find.wholeWord"), "wholeWord"),
        toggle("regex", t("editor.find.regex"), "regex")),
      h("div.pp-find-field.include", null, icon("filter", 14, "lead"), include),
      summary,
      iconButton("x", t("common.close"), closeFindInFiles, true)),
    results);
  const later = debounce(() => void run(), 400);
  input.addEventListener("input", () => { if (input.value.trim().length >= 2) later(); });
  include.addEventListener("input", () => { store.set("fif.include", include.value); later(); });
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Enter" && !e.isComposing) { e.preventDefault(); later.cancel(); void run(); }
    else if (e.key === "Escape") { e.preventDefault(); closeFindInFiles(); }
    else if (e.key === "ArrowDown") { e.preventDefault(); moveSel(1); }
    else if (e.key === "ArrowUp") { e.preventDefault(); moveSel(-1); }
    else return;
    e.stopPropagation();
  };
  input.addEventListener("keydown", onKey);
  include.addEventListener("keydown", onKey);
  results.addEventListener("keydown", onKey);
  // 拖动调整高度
  grip.addEventListener("mousedown", (e) => {
    e.preventDefault();
    const startY = e.clientY, startH = panel!.offsetHeight;
    const move = (ev: MouseEvent) => {
      const hh = clamp(startH - (ev.clientY - startY), 120, host.offsetHeight - 120);
      panel!.style.height = hh + "px";
    };
    const up = () => {
      document.removeEventListener("mousemove", move);
      document.removeEventListener("mouseup", up);
      store.set("fif.height", panel!.offsetHeight);
    };
    document.addEventListener("mousemove", move);
    document.addEventListener("mouseup", up);
  });
  host.appendChild(panel);
}

function matcher(): RegExp | null {
  const q = input.value;
  try {
    let src = opts.regex ? q : escapeRegExp(q);
    if (opts.wholeWord) src = `\\b(?:${src})\\b`;
    return new RegExp(src, opts.caseSensitive ? "" : "i");
  } catch {
    return null;
  }
}

async function run() {
  const q = input.value;
  const my = ++seq;
  if (!q) { results.replaceChildren(); summary.textContent = ""; return; }
  summary.textContent = t("editor.findInFiles.searching");
  let res: { matches: FSMatch[]; truncated: boolean };
  try {
    res = await call("fs.grep", {
      projectId: app.projectId, query: q, regex: opts.regex, caseSensitive: opts.caseSensitive, wholeWord: opts.wholeWord,
      include: include.value.trim() || undefined, limit: 500,
    });
  } catch (e: any) {
    if (my !== seq) return;
    summary.textContent = "";
    results.replaceChildren(h("div.pp-fif-error", null, icon("circle-alert", 16), e.message));
    return;
  }
  if (my !== seq) return;
  const re = matcher();
  const groups = new Map<string, FSMatch[]>();
  for (const m of res.matches ?? []) groups.set(m.path, [...(groups.get(m.path) ?? []), m]);
  const n = res.matches?.length ?? 0;
  summary.textContent = n ? t(res.truncated ? "editor.findInFiles.countTruncated" : "editor.findInFiles.count", n, groups.size) : "";
  if (!n) {
    results.replaceChildren(h("div.pp-fif-empty", null, t("editor.findInFiles.noResults")));
    return;
  }
  const out: HTMLElement[] = [];
  for (const [path, ms] of groups) {
    const lines = h("div.pp-fif-lines");
    const head = h("div.pp-fif-file", null, h("span.pp-tree-chevron", null, icon("chevron-down", 14)), fileIcon(path),
      h("span.pp-fif-name", null, basename(path)), h("span.pp-fif-dir", null, dirname(path)), h("span.pp-fif-n", null, String(ms.length)));
    head.addEventListener("click", () => { const c = !lines.hidden; lines.hidden = c; head.classList.toggle("collapsed", c); });
    for (const m of ms) {
      const text = m.text;
      // 内核的列是码点计数，这里按 JS 正则重新定位匹配，用于高亮与选中
      let from = -1, len = 0;
      if (re) { const mm = re.exec(text); if (mm) { from = mm.index; len = mm[0].length; } }
      if (from < 0) { from = Math.max(0, m.column - 1); len = 0; }
      const lead = text.slice(0, from);
      const cut = lead.length > 60 ? "…" + lead.slice(lead.length - 50) : lead;
      const row = h("div.pp-fif-line", { tabIndex: -1, dataset: { path, line: String(m.line), from: String(from), len: String(len) } },
        h("span.pp-fif-ln", null, String(m.line)),
        h("span.pp-fif-text", null, cut.replace(/^\s+/, ""), h("mark", null, text.slice(from, from + len)), text.slice(from + len, from + len + 300)));
      row.addEventListener("click", () => jump(row));
      lines.appendChild(row);
    }
    out.push(head, lines);
  }
  results.replaceChildren(...out);
  results.scrollTop = 0;
}

let selRow: HTMLElement | null = null;
function moveSel(d: number) {
  const rows = [...results.querySelectorAll<HTMLElement>(".pp-fif-line")].filter((r) => r.offsetParent !== null);
  if (!rows.length) return;
  const i = selRow ? rows.indexOf(selRow) : -1;
  const next = rows[clamp(i + d, 0, rows.length - 1)];
  jump(next, false);
}

async function jump(row: HTMLElement, focus = true) {
  selRow?.classList.remove("active");
  selRow = row;
  row.classList.add("active");
  row.scrollIntoView({ block: "nearest" });
  const { path, line, from, len } = row.dataset;
  await openFile(path!, { line: +line!, col: +from! + 1, focus });
  const tab = app.active;
  if (tab?.kind === "text" && tab.path === path) {
    const st = app.view.state;
    const l = st.doc.line(Math.min(+line!, st.doc.lines));
    const a = Math.min(l.from + +from!, l.to);
    app.view.dispatch({ selection: { anchor: a, head: Math.min(a + +len!, l.to) } });
  }
}
