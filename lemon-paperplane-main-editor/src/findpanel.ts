// 查找替换面板（按设计规范自绘，替代 @codemirror/search 的默认面板）。
import { EditorView, runScopeHandlers, type Panel, type ViewUpdate } from "@codemirror/view";
import { SearchQuery, getSearchQuery, setSearchQuery, findNext, findPrevious, replaceNext, replaceAll, selectMatches, closeSearchPanel } from "@codemirror/search";
import { h } from "./util";
import { icon } from "./ui";
import { t } from "./i18n";

let replaceVisible = false;
const MAX_COUNT = 9999;

/** 打开面板时是否展开替换行（命令 replace 置 true） */
export function setReplaceVisible(v: boolean) {
  replaceVisible = v;
}

export function createFindPanel(view: EditorView): Panel {
  let query = getSearchQuery(view.state);
  const toggle = (name: string, tip: string, get: () => boolean, set: (v: boolean) => void) => {
    const b = h("button.pp-find-toggle", { type: "button", "data-tip": tip, "aria-label": tip, "aria-pressed": String(get()) }, icon(name, 16));
    b.addEventListener("mousedown", (e) => e.preventDefault());
    b.addEventListener("click", () => { set(!get()); b.classList.toggle("on", get()); b.setAttribute("aria-pressed", String(get())); commit(); });
    b.classList.toggle("on", get());
    return b;
  };
  let caseSensitive = query.caseSensitive, regexp = query.regexp, wholeWord = query.wholeWord;
  const findInput = h("input.pp-find-input", { type: "text", value: query.search, placeholder: t("editor.find.placeholder"), "main-field": "true", spellcheck: false, autocomplete: "off", "aria-label": t("editor.find.placeholder") });
  const replaceInput = h("input.pp-find-input", { type: "text", value: query.replace, placeholder: t("editor.find.replacePlaceholder"), spellcheck: false, autocomplete: "off", "aria-label": t("editor.find.replacePlaceholder") });
  const count = h("span.pp-find-count");
  const findBox = h("div.pp-find-field", null, icon("search", 14, "lead"), findInput,
    toggle("case-sensitive", t("editor.find.matchCase"), () => caseSensitive, (v) => (caseSensitive = v)),
    toggle("whole-word", t("editor.find.wholeWord"), () => wholeWord, (v) => (wholeWord = v)),
    toggle("regex", t("editor.find.regex"), () => regexp, (v) => (regexp = v)));
  const replaceBox = h("div.pp-find-field", null, icon("replace", 14, "lead"), replaceInput);
  const btn = (name: string, tip: string, run: () => void) => {
    const b = h("button.pp-icon-btn.small", { type: "button", "data-tip": tip, "aria-label": tip }, icon(name, 14));
    b.addEventListener("mousedown", (e) => e.preventDefault());
    b.addEventListener("click", run);
    return b;
  };
  const textBtn = (label: string, run: () => void) => {
    const b = h("button.pp-btn.secondary.small", { type: "button" }, label);
    b.addEventListener("mousedown", (e) => e.preventDefault());
    b.addEventListener("click", run);
    return b;
  };
  const expand = h("button.pp-icon-btn.small.pp-find-expand", { type: "button", "data-tip": t("editor.find.toggleReplace"), "aria-label": t("editor.find.toggleReplace") }, icon("chevron-right", 14));
  expand.addEventListener("mousedown", (e) => e.preventDefault());
  expand.addEventListener("click", () => setReplace(!replaceVisible, true));
  const replaceRow = h("div.pp-find-row.replace", null,
    h("span.pp-find-gap"), replaceBox,
    textBtn(t("editor.find.replace"), () => { commit(); replaceNext(view); }),
    textBtn(t("editor.find.replaceAll"), () => { commit(); replaceAll(view); }));
  const dom = h("div.pp-find", { role: "search" },
    h("div.pp-find-row", null, expand, findBox, count,
      btn("arrow-up", t("editor.find.previous"), () => { commit(); findPrevious(view); }),
      btn("arrow-down", t("editor.find.next"), () => { commit(); findNext(view); }),
      btn("text-cursor-input", t("editor.find.selectAll"), () => { commit(); selectMatches(view); view.focus(); }),
      h("span.pp-flex"),
      btn("x", t("common.close"), () => closeSearchPanel(view))),
    replaceRow);

  function setReplace(v: boolean, focus = false) {
    replaceVisible = v;
    dom.classList.toggle("with-replace", v);
    if (v && focus) replaceInput.focus();
  }
  setReplace(replaceVisible);

  function commit() {
    const q = new SearchQuery({ search: findInput.value, caseSensitive, regexp, wholeWord, replace: replaceInput.value });
    if (!q.eq(query)) {
      query = q;
      view.dispatch({ effects: setSearchQuery.of(q) });
    }
  }

  const onKey = (e: KeyboardEvent) => {
    if (runScopeHandlers(view, e, "search-panel")) { e.preventDefault(); return; }
    if (e.key === "Enter" && !e.isComposing) {
      e.preventDefault();
      commit();
      if (e.target === replaceInput) replaceNext(view);
      else if (e.shiftKey) findPrevious(view);
      else findNext(view);
    } else if (e.key === "Escape") {
      e.preventDefault();
      closeSearchPanel(view);
      view.focus();
    }
  };
  findInput.addEventListener("keydown", onKey);
  replaceInput.addEventListener("keydown", onKey);
  findInput.addEventListener("input", commit);
  replaceInput.addEventListener("input", commit);

  let countTimer: ReturnType<typeof setTimeout> | undefined;
  function scheduleCount() {
    if (countTimer) return;
    countTimer = setTimeout(() => { countTimer = undefined; updateCount(); }, 30);
  }
  function updateCount() {
    const q = getSearchQuery(view.state);
    findBox.classList.toggle("invalid", !!q.search && !q.valid);
    if (!q.search || !q.valid) { count.textContent = ""; return; }
    const sel = view.state.selection.main;
    let n = 0, cur = 0;
    const cursor = q.getCursor(view.state) as Iterator<{ from: number; to: number }>;
    for (let r = cursor.next(); !r.done; r = cursor.next()) {
      n++;
      if (r.value.from === sel.from && r.value.to === sel.to) cur = n;
      if (n > MAX_COUNT) break;
    }
    count.textContent = n === 0 ? t("editor.find.noResults") : n > MAX_COUNT ? `${MAX_COUNT}+` : cur ? `${cur}/${n}` : t("editor.find.count", n);
    count.classList.toggle("none", n === 0);
  }

  return {
    dom,
    top: true,
    mount() {
      findInput.focus();
      findInput.select();
      updateCount();
    },
    update(u: ViewUpdate) {
      for (const tr of u.transactions) for (const e of tr.effects) {
        if (e.is(setSearchQuery) && !e.value.eq(query)) {
          query = e.value;
          findInput.value = query.search;
          replaceInput.value = query.replace;
          caseSensitive = query.caseSensitive; regexp = query.regexp; wholeWord = query.wholeWord;
          dom.querySelectorAll(".pp-find-toggle").forEach((b, i) => b.classList.toggle("on", [caseSensitive, wholeWord, regexp][i]));
        }
      }
      if (u.docChanged || u.selectionSet || u.transactions.some((tr) => tr.effects.some((e) => e.is(setSearchQuery)))) scheduleCount();
    },
    destroy() {
      if (countTimer) clearTimeout(countTimer);
    },
  };
}

/** 焦点移到替换框（命令 replace） */
export function focusReplace(view: EditorView) {
  const panel = view.dom.querySelector(".pp-find") as HTMLElement | null;
  if (!panel) return;
  panel.classList.add("with-replace");
  replaceVisible = true;
  const inputs = panel.querySelectorAll("input");
  const find = inputs[0] as HTMLInputElement, rep = inputs[1] as HTMLInputElement;
  (find.value ? rep : find).focus();
  (find.value ? rep : find).select();
}
