// 编辑器核心：标签页（每个标签一个 EditorState，共用一个 EditorView）、打开/保存/关闭、外部修改检测、设置与主题。
import { Compartment, EditorState, Text, Transaction, type Extension, type StateEffect, type TransactionSpec } from "@codemirror/state";
import {
  EditorView, lineNumbers, highlightActiveLineGutter, highlightSpecialChars, drawSelection, dropCursor,
  rectangularSelection, crosshairCursor, highlightActiveLine, highlightWhitespace, highlightTrailingWhitespace,
} from "@codemirror/view";
import { history } from "@codemirror/commands";
import { indentOnInput, bracketMatching, foldGutter, codeFolding, indentUnit } from "@codemirror/language";
import { autocompletion, closeBrackets, completeAnyWord } from "@codemirror/autocomplete";
import { search, highlightSelectionMatches } from "@codemirror/search";
import { call, ApiError, fetchJSON, type Contributions, type EditorSettings, type FSFile, type LanguageInfo, type Project, type VCSDiff } from "./api";
import { post } from "./bridge";
import { t } from "./i18n";
import { dialog, toast, icon } from "./ui";
import { basename, dirname, debounce, fontStack, platform, store, guessIndent } from "./util";
import { fonts } from "./gen/tokens";
import { paperplaneTheme, themeFromJSON, fontTheme, type ThemeJSON } from "./theme";
import { resolveLanguage, loadLanguage, commentData, indentFolding, setLanguages, languageById } from "./languages";
import { loadKeymap, type LoadedKeymap } from "./keymaps";
import { createFindPanel } from "./findpanel";
import { gitGutter, setGitBlocks, blocksFromDiff, type ChangeBlock } from "./gitgutter";

export type TabKind = "loading" | "text" | "binary" | "tooLarge" | "error";

export interface Tab {
  id: number;
  path: string;
  kind: TabKind;
  state: EditorState | null;
  savedDoc: Text | null;
  dirty: boolean;
  /** 只改了编码/换行符（内容没变也要能保存） */
  metaDirty: boolean;
  mtime: string;
  size: number;
  encoding: string;
  bom: boolean;
  lineEnding: "lf" | "crlf" | "mixed";
  /** mixed 时占多数的换行符，保存时用它 */
  preferredLineEnding?: "lf" | "crlf";
  /** 用户选「用其他编码重新打开」时的编码，之后的重新载入也按它解码 */
  forcedEncoding?: string;
  /** 图片预览（data URL，关闭标签即释放） */
  imageUrl?: string;
  readOnly: boolean;
  error?: string;
  lang: LanguageInfo | null;
  /** 用户在状态栏手动选的语言 id（"" 表示纯文本） */
  langOverride?: string;
  indent: { spaces: boolean; size: number };
  configVersion: number;
  scroll?: StateEffect<unknown>;
  external: null | "modified" | "deleted";
  saving: boolean;
}

const DEFAULT_SETTINGS: EditorSettings = {
  keymap: "", theme: "", fontSize: 13, fontFamily: "", tabSize: 4, insertSpaces: true,
  wordWrap: false, autoSave: "off", trimTrailingWhitespace: false, insertFinalNewline: true, showWhitespace: false,
};

export const app = {
  projectId: "",
  project: null as Project | null,
  settings: { ...DEFAULT_SETTINGS } as EditorSettings,
  contribs: { languages: [], keymaps: [], editorThemes: [], defaultKeymap: "" } as Contributions,
  /** 宿主传入的明暗 */
  theme: "light" as "light" | "dark",
  tabs: [] as Tab[],
  active: null as Tab | null,
  view: null as unknown as EditorView,
  keymap: null as LoadedKeymap | null,
  configVersion: 1,
  closed: [] as string[],
  editorThemeKind: "light" as "light" | "dark",
};

// ---------------------------------------------------------------------------
// 事件

type Ev = "tabs" | "active" | "status" | "settings" | "external" | "contribs";
const listeners = new Map<Ev, (() => void)[]>();
export function on(ev: Ev, f: () => void) {
  listeners.set(ev, [...(listeners.get(ev) ?? []), f]);
}
export function emit(ev: Ev) {
  for (const f of listeners.get(ev) ?? []) f();
}

// ---------------------------------------------------------------------------
// 扩展与 Compartment

const keymapC = new Compartment(), themeC = new Compartment(), fontC = new Compartment(), wrapC = new Compartment(), wsC = new Compartment();
const langC = new Compartment(), indentC = new Compartment(), readOnlyC = new Compartment(), commentC = new Compartment();

let gitClick: (view: EditorView, block: ChangeBlock, dom: HTMLElement) => void = () => {};
export function setGitClickHandler(f: typeof gitClick) {
  gitClick = f;
}

function foldMarker(open: boolean): HTMLElement {
  const el = document.createElement("span");
  el.className = "pp-fold-marker";
  el.appendChild(icon(open ? "chevron-down" : "chevron-right", 14));
  return el;
}

let staticExt: Extension[] | null = null;
function staticExtensions(): Extension[] {
  return (staticExt ??= [
    lineNumbers(),
    gitGutter((v, b, d) => gitClick(v, b, d)),
    foldGutter({ markerDOM: foldMarker }),
    codeFolding({ placeholderText: "…" }),
    indentFolding,
    highlightActiveLineGutter(),
    highlightSpecialChars(),
    history(),
    drawSelection(),
    dropCursor(),
    EditorState.allowMultipleSelections.of(true),
    indentOnInput(),
    bracketMatching(),
    closeBrackets(),
    autocompletion({ icons: false }),
    EditorState.languageData.of(() => [{ autocomplete: completeAnyWord }]),
    rectangularSelection(),
    crosshairCursor(),
    highlightActiveLine(),
    highlightSelectionMatches(),
    search({ top: true, createPanel: createFindPanel }),
    EditorView.contentAttributes.of({ spellcheck: "false", autocorrect: "off", autocapitalize: "off", translate: "no" }),
    EditorView.updateListener.of((u) => {
      if (u.view !== app.view) return;
      const tab = app.active;
      if (u.docChanged && tab?.kind === "text") {
        updateDirty(tab, u.state.doc);
        if (!u.transactions.some((tr) => tr.annotation(Transaction.remote))) scheduleAutoSave();
      }
      if (u.docChanged || u.selectionSet) emit("status");
    }),
  ]);
}

function globalExtensions(): Extension[] {
  return [
    keymapC.of(app.keymap?.extension ?? []),
    themeC.of(currentThemeExt),
    fontC.of(currentFontExt()),
    wrapC.of(app.settings.wordWrap ? EditorView.lineWrapping : []),
    wsC.of(app.settings.showWhitespace ? [highlightWhitespace(), highlightTrailingWhitespace()] : []),
  ];
}

function globalEffects(): StateEffect<unknown>[] {
  return [
    keymapC.reconfigure(app.keymap?.extension ?? []),
    themeC.reconfigure(currentThemeExt),
    fontC.reconfigure(currentFontExt()),
    wrapC.reconfigure(app.settings.wordWrap ? EditorView.lineWrapping : []),
    wsC.reconfigure(app.settings.showWhitespace ? [highlightWhitespace(), highlightTrailingWhitespace()] : []),
  ];
}

function indentExt(i: { spaces: boolean; size: number }): Extension {
  return [EditorState.tabSize.of(i.size), indentUnit.of(i.spaces ? " ".repeat(i.size) : "\t")];
}

function createState(tab: Tab, content: string): EditorState {
  return EditorState.create({
    doc: content,
    extensions: [
      staticExtensions(),
      globalExtensions(),
      langC.of([]),
      commentC.of(commentData(tab.lang)),
      indentC.of(indentExt(tab.indent)),
      readOnlyC.of(EditorState.readOnly.of(tab.readOnly)),
    ],
  });
}

let emptyState: EditorState | null = null;
function getEmptyState(): EditorState {
  return (emptyState ??= EditorState.create({ doc: "", extensions: [staticExtensions(), globalExtensions(), EditorState.readOnly.of(true)] }));
}

/** 当前（或指定）标签的最新状态 */
export function stateOf(tab: Tab): EditorState | null {
  return tab === app.active && tab.kind === "text" ? app.view.state : tab.state;
}

/** 修改标签的状态（活动标签走 view.dispatch） */
function updateTabState(tab: Tab, spec: TransactionSpec) {
  if (tab === app.active && tab.kind === "text") app.view.dispatch(spec);
  else if (tab.state) tab.state = tab.state.update(spec).state;
}

// ---------------------------------------------------------------------------
// 主题、字体、设置

let currentThemeExt: Extension = paperplaneTheme("light");
function currentFontExt(): Extension {
  const fam = app.settings.fontFamily?.trim() ? fontStack(app.settings.fontFamily, "monospace") : fontStack((fonts.mono as any)[platform] ?? "", "monospace");
  return fontTheme(fam, app.settings.fontSize || 13);
}

export function editorRoot(): HTMLElement {
  return document.getElementById("pp-island")!;
}

async function resolveEditorTheme(): Promise<void> {
  const id = app.settings.theme || "";
  let kind: "light" | "dark" = app.theme;
  let ext: Extension | null = null;
  if (id === "paperplane-light") kind = "light";
  else if (id === "paperplane-dark") kind = "dark";
  else if (id) {
    const def = app.contribs.editorThemes.find((x) => x.id === id);
    if (def) {
      try {
        const j = await fetchJSON<ThemeJSON>(def.file);
        kind = (j.kind ?? def.kind) === "dark" ? "dark" : "light";
        ext = themeFromJSON({ ...j, kind });
      } catch (e: any) {
        toast("danger", t("editor.error.themeLoad", def.name));
      }
    }
  }
  currentThemeExt = ext ?? paperplaneTheme(kind);
  app.editorThemeKind = kind;
  editorRoot().dataset.theme = kind;
}

/** 配置变化：重配活动视图，其他标签在激活时再补（configVersion） */
export function reconfigure() {
  app.configVersion++;
  const tab = app.active;
  app.view.dispatch({ effects: globalEffects() });
  if (tab?.kind === "text") tab.configVersion = app.configVersion;
  else emptyState = null;
  emit("status");
}

function syncConfig(tab: Tab) {
  if (tab.state && tab.configVersion !== app.configVersion) {
    tab.state = tab.state.update({ effects: globalEffects() }).state;
    tab.configVersion = app.configVersion;
  }
}

export function setHostTheme(theme: "light" | "dark") {
  app.theme = theme === "dark" ? "dark" : "light";
  document.documentElement.dataset.theme = app.theme;
  if (!app.settings.theme) {
    editorRoot().dataset.theme = app.theme;
    app.editorThemeKind = app.theme;
    currentThemeExt = paperplaneTheme(app.theme);
    if (app.view) reconfigure();
  }
}

export async function applySettings(s: Partial<EditorSettings>, persist: boolean) {
  const prev = app.settings;
  app.settings = { ...DEFAULT_SETTINGS, ...prev, ...s };
  if (persist) {
    try {
      await call("settings.update", { editor: app.settings });
    } catch (e: any) {
      toast("danger", e.message);
    }
  }
  if (app.settings.keymap !== prev.keymap || !app.keymap) await reloadKeymap();
  if (app.settings.theme !== prev.theme) await resolveEditorTheme();
  reconfigure();
  emit("settings");
}

export async function reloadKeymap() {
  const k = await loadKeymap(app.contribs.keymaps, app.settings.keymap, app.contribs.defaultKeymap);
  if (k.error) toast("danger", t("editor.error.keymapLoad", k.error));
  app.keymap = k;
}

export function toggleWhitespace() {
  void applySettings({ showWhitespace: !app.settings.showWhitespace }, true);
}

/** 启动：读设置与贡献，创建视图 */
export async function initCore(parent: HTMLElement) {
  const [settings, contribs, project] = await Promise.all([
    call<{ editor?: EditorSettings }>("settings.get").catch(() => ({} as any)),
    call<Contributions>("ui.contributions").catch(() => null),
    call<Project>("project.get", { projectId: app.projectId }).catch(() => null),
  ]);
  app.project = project;
  setContributions(contribs);
  app.settings = { ...DEFAULT_SETTINGS, ...(settings?.editor ?? {}) };
  await Promise.all([reloadKeymap(), resolveEditorTheme()]);
  app.view = new EditorView({ state: getEmptyState(), parent });
  emit("settings");
}

function setContributions(c: Contributions | null) {
  app.contribs = {
    languages: c?.languages ?? [],
    keymaps: c?.keymaps ?? [],
    editorThemes: c?.editorThemes ?? [],
    defaultKeymap: c?.defaultKeymap || (platform === "macos" ? "eclipse-macos" : "eclipse-windows"),
  };
  setLanguages(app.contribs.languages);
}

let lastRefresh = Date.now();
/** 窗口重新获得焦点：设置、插件贡献可能在 App 里改过 */
export async function refreshFromHost() {
  if (Date.now() - lastRefresh < 5000) return;
  lastRefresh = Date.now();
  const [settings, contribs] = await Promise.all([
    call<{ editor?: EditorSettings }>("settings.get").catch(() => null),
    call<Contributions>("ui.contributions").catch(() => null),
  ]);
  if (contribs && JSON.stringify(contribs) !== JSON.stringify(app.contribs)) {
    setContributions(contribs);
    await reloadKeymap();
    app.configVersion++;
    emit("contribs");
  }
  if (settings?.editor && JSON.stringify({ ...DEFAULT_SETTINGS, ...settings.editor }) !== JSON.stringify(app.settings)) {
    await applySettings(settings.editor, false);
  }
}

// ---------------------------------------------------------------------------
// 标签页

let nextId = 1;

function newTab(path: string): Tab {
  return {
    id: nextId++, path, kind: "loading", state: null, savedDoc: null, dirty: false, metaDirty: false, mtime: "", size: 0,
    encoding: "utf-8", bom: false, lineEnding: "lf", readOnly: false, lang: null,
    indent: { spaces: app.settings.insertSpaces, size: app.settings.tabSize || 4 }, configVersion: 0, external: null, saving: false,
  };
}

export function anyDirty(): boolean {
  return app.tabs.some((t) => t.dirty);
}

let lastDirty = false;
function updateDirty(tab: Tab, doc: Text) {
  const d = app.tabs.includes(tab) && (tab.metaDirty || (!!tab.savedDoc && !doc.eq(tab.savedDoc)));
  if (d !== tab.dirty) {
    tab.dirty = d;
    emit("tabs");
  }
  const any = anyDirty();
  if (any !== lastDirty) {
    lastDirty = any;
    post({ type: "dirty", dirty: any });
  }
}

export function updateTitle() {
  const name = app.project?.name ?? "";
  const file = app.active ? basename(app.active.path) : "";
  post({ type: "title", title: file ? `${name} — ${file}` : name });
}

/** 打开文件（已打开则切换），可定位到行列或选中范围 */
export async function openFile(path: string, opts: { line?: number; col?: number; select?: { from: number; to: number }; focus?: boolean; history?: boolean } = {}) {
  path = path.replace(/^\/+/, "").replace(/\\/g, "/");
  if (!path) return;
  if (opts.history !== false) pushHistory();
  let tab = app.tabs.find((t) => t.path === path);
  if (!tab) {
    tab = newTab(path);
    const i = app.active ? app.tabs.indexOf(app.active) + 1 : app.tabs.length;
    app.tabs.splice(i, 0, tab);
    activate(tab, opts.focus !== false);
    await loadTab(tab);
    // 读完内容才有编辑区可聚焦
    if (opts.focus !== false && tab === app.active) focusEditor();
  } else {
    activate(tab, opts.focus !== false);
  }
  remember(path);
  if (tab.kind === "text" && (opts.line || opts.select)) jump(tab, opts);
}

function jump(tab: Tab, opts: { line?: number; col?: number; select?: { from: number; to: number } }) {
  const st = stateOf(tab)!;
  let anchor: number, head: number;
  if (opts.select) {
    anchor = Math.min(opts.select.from, st.doc.length);
    head = Math.min(opts.select.to, st.doc.length);
  } else {
    const line = st.doc.line(Math.max(1, Math.min(opts.line!, st.doc.lines)));
    anchor = head = Math.min(line.from + Math.max(0, (opts.col ?? 1) - 1), line.to);
  }
  updateTabState(tab, { selection: { anchor, head }, effects: EditorView.scrollIntoView(head, { y: "center" }) });
  if (tab !== app.active) tab.scroll = undefined;
}

async function loadTab(tab: Tab) {
  let f: FSFile;
  try {
    f = await call<FSFile>("fs.read", { projectId: app.projectId, path: tab.path });
  } catch (e: any) {
    tab.kind = "error";
    tab.error = e.message;
    afterKindChange(tab);
    return;
  }
  if (!app.tabs.includes(tab)) return; // 读的时候已关闭
  applyFile(tab, f);
  afterKindChange(tab);
  if (tab.kind === "text") {
    void attachLanguage(tab);
    void refreshGit(tab);
  }
}

function applyFile(tab: Tab, f: FSFile) {
  tab.mtime = f.mtime;
  tab.size = f.size;
  tab.external = null;
  tab.imageUrl = f.dataUrl;
  if (f.tooLarge) { tab.kind = "tooLarge"; return; }
  if (f.binary) { tab.kind = "binary"; return; }
  tab.kind = "text";
  tab.encoding = f.encoding || "utf-8";
  tab.bom = !!f.bom;
  tab.lineEnding = f.lineEnding || "lf";
  tab.preferredLineEnding = f.preferredLineEnding;
  tab.readOnly = !!f.readOnly;
  tab.lang = tab.langOverride != null ? languageById(tab.langOverride) : resolveLanguage(tab.path);
  tab.indent = detectIndent(f.content) ?? { spaces: app.settings.insertSpaces, size: app.settings.tabSize || 4 };
  tab.state = createState(tab, f.content);
  tab.savedDoc = tab.state.doc;
  tab.dirty = false;
  tab.metaDirty = false;
  tab.configVersion = app.configVersion;
}

function afterKindChange(tab: Tab) {
  if (tab === app.active) {
    if (tab.kind === "text" && tab.state) app.view.setState(tab.state);
    emit("active");
  }
  emit("tabs");
  emit("status");
}

export function detectIndent(text: string): { spaces: boolean; size: number } | null {
  return guessIndent(text, app.settings.tabSize || 4);
}

async function attachLanguage(tab: Tab) {
  const d = tab.lang;
  updateTabState(tab, { effects: [commentC.reconfigure(commentData(d)), langC.reconfigure([])] });
  if (!d) { emit("status"); return; }
  try {
    const ext = await loadLanguage(d);
    if (tab.lang !== d || tab.kind !== "text") return;
    updateTabState(tab, { effects: langC.reconfigure(ext as Extension) });
  } catch (e: any) {
    console.error("[paperplane] language", d.id, e);
    toast("danger", t("editor.error.languageLoad", d.name));
  }
  emit("status");
}

export function setTabLanguage(tab: Tab, id: string) {
  tab.langOverride = id;
  tab.lang = id ? languageById(id) : null;
  void attachLanguage(tab);
}

export function setTabIndent(tab: Tab, indent: { spaces: boolean; size: number }) {
  tab.indent = indent;
  updateTabState(tab, { effects: indentC.reconfigure(indentExt(indent)) });
  emit("status");
}

export function setTabLineEnding(tab: Tab, le: "lf" | "crlf") {
  if (tab.lineEnding === le) return;
  tab.lineEnding = le;
  markDirtyForMeta(tab);
}

export function setTabEncoding(tab: Tab, enc: string, bom: boolean) {
  tab.encoding = enc;
  tab.bom = bom;
  markDirtyForMeta(tab);
}

function markDirtyForMeta(tab: Tab) {
  tab.metaDirty = true;
  updateDirty(tab, stateOf(tab)!.doc);
  emit("status");
}

export function activate(tab: Tab | null, focus = true) {
  const prev = app.active;
  if (prev === tab) { if (focus) focusEditor(); return; }
  if (prev?.kind === "text") {
    prev.state = app.view.state;
    prev.scroll = app.view.scrollSnapshot();
    if (app.settings.autoSave === "onFocusChange" && prev.dirty) void saveTab(prev, { quiet: true });
  }
  app.active = tab;
  if (tab?.kind === "text" && tab.state) {
    syncConfig(tab);
    app.view.setState(tab.state);
    if (tab.scroll) app.view.dispatch({ effects: tab.scroll });
    else app.view.dispatch({ effects: EditorView.scrollIntoView(tab.state.selection.main.head, { y: "center" }) });
  } else {
    app.view.setState(getEmptyState());
  }
  emit("active");
  emit("tabs");
  emit("status");
  updateTitle();
  if (focus) focusEditor();
}

export function focusEditor() {
  if (app.active?.kind === "text") setTimeout(() => { if (app.active?.kind === "text") app.view.focus(); });
}

/** 关闭标签；有未保存修改时先问。返回是否已关闭 */
export async function closeTab(tab: Tab, opts: { force?: boolean } = {}): Promise<boolean> {
  if (tab.dirty && !opts.force) {
    activate(tab, false);
    const r = await dialog({
      title: t("editor.unsaved.title"),
      message: t("editor.unsaved.message", basename(tab.path)),
      buttons: [
        { id: "discard", label: t("editor.unsaved.discard"), kind: "ghost" },
        { id: "cancel", label: t("common.cancel"), kind: "secondary" },
        { id: "save", label: t("common.save"), kind: "primary" },
      ],
    });
    if (r.button === "save") { if (!(await saveTab(tab))) return false; }
    else if (r.button !== "discard") return false;
  }
  const i = app.tabs.indexOf(tab);
  if (i < 0) return true;
  app.tabs.splice(i, 1);
  app.closed = [tab.path, ...app.closed.filter((p) => p !== tab.path)].slice(0, 20);
  if (app.active === tab) {
    app.active = null;
    activate(app.tabs[i] ?? app.tabs[i - 1] ?? null);
  }
  // 释放：状态、文档都不再被引用
  tab.state = null;
  tab.savedDoc = null;
  tab.scroll = undefined;
  emit("tabs");
  updateDirty(tab, Text.empty);
  return true;
}

export async function closeTabs(list: Tab[]): Promise<boolean> {
  for (const tab of [...list]) if (!(await closeTab(tab))) return false;
  return true;
}

export async function reopenClosed() {
  const p = app.closed.shift();
  if (p) await openFile(p);
}

/** 文件改名/移动后更新标签路径（语言按新文件名重新匹配） */
export function retargetTab(tab: Tab, path: string) {
  tab.path = path;
  if (tab.kind === "text" && tab.langOverride == null) {
    const lang = resolveLanguage(path);
    if (lang !== tab.lang) { tab.lang = lang; void attachLanguage(tab); }
  }
  emit("tabs");
  emit("status");
  if (tab === app.active) updateTitle();
}

export function moveTab(tab: Tab, to: number) {
  const i = app.tabs.indexOf(tab);
  if (i < 0) return;
  app.tabs.splice(i, 1);
  app.tabs.splice(Math.max(0, Math.min(to, app.tabs.length)), 0, tab);
  emit("tabs");
}

// ---------------------------------------------------------------------------
// 保存

/** 保存前按设置清理：去行尾空白、补末尾换行 */
function prepareForSave(tab: Tab) {
  const st = stateOf(tab)!;
  const changes: { from: number; to?: number; insert?: string }[] = [];
  if (app.settings.trimTrailingWhitespace) {
    for (let n = 1; n <= st.doc.lines; n++) {
      const l = st.doc.line(n);
      const m = /[ \t]+$/.exec(l.text);
      if (m) changes.push({ from: l.to - m[0].length, to: l.to });
    }
  }
  if (app.settings.insertFinalNewline && st.doc.length && st.doc.sliceString(st.doc.length - 1) !== "\n") {
    changes.push({ from: st.doc.length, insert: "\n" });
  }
  if (changes.length) updateTabState(tab, { changes, userEvent: "input" });
}

export async function saveTab(tab: Tab, opts: { force?: boolean; quiet?: boolean; create?: boolean } = {}): Promise<boolean> {
  if (tab.kind !== "text" || tab.readOnly || tab.saving) return false;
  if (!tab.dirty && !opts.force && !opts.create) return true;
  if (tab.external === "modified" && opts.quiet) return false; // 自动保存不覆盖外部修改
  prepareForSave(tab);
  const doc = stateOf(tab)!.doc;
  tab.saving = true;
  try {
    const r = await call<{ mtime: string; size: number }>("fs.write", {
      projectId: app.projectId, path: tab.path, content: doc.toString(), encoding: tab.encoding, bom: tab.bom,
      lineEnding: savedLineEnding(tab),
      expectedMtime: opts.force || opts.create ? undefined : tab.mtime || undefined,
      create: opts.create || undefined,
    });
    tab.mtime = r.mtime;
    tab.size = r.size;
    tab.savedDoc = doc;
    tab.metaDirty = false;
    tab.external = null;
    if (tab.lineEnding === "mixed") tab.lineEnding = savedLineEnding(tab);
    updateDirty(tab, stateOf(tab)!.doc);
    emit("external");
    emit("status");
    void refreshGit(tab);
    if (!opts.quiet) flashStatus(t("common.saved"));
    return true;
  } catch (e) {
    const err = e as ApiError;
    if (err.reason === "modifiedExternally") {
      tab.external = "modified";
      emit("external");
      if (opts.quiet) return false;
      return resolveConflict(tab);
    }
    if (err.reason === "deletedExternally") {
      tab.external = "deleted";
      emit("external");
      if (opts.quiet) return false;
      const r = await dialog({
        title: t("editor.deleted.title"), message: t("editor.deleted.message", basename(tab.path)),
        buttons: [{ id: "cancel", label: t("common.cancel"), kind: "secondary" }, { id: "create", label: t("editor.deleted.recreate"), kind: "primary" }],
      });
      tab.saving = false;
      return r.button === "create" ? saveTab(tab, { create: true }) : false;
    }
    toast("danger", t("editor.error.save", basename(tab.path), err.message));
    return false;
  } finally {
    tab.saving = false;
  }
}

/** 保存冲突：覆盖 / 重新载入 / 对比 */
export async function resolveConflict(tab: Tab): Promise<boolean> {
  activate(tab, false);
  const r = await dialog({
    title: t("editor.conflict.title"),
    message: t("editor.conflict.message", basename(tab.path)),
    buttons: [
      { id: "compare", label: t("editor.conflict.compare"), kind: "ghost" },
      { id: "reload", label: t("editor.conflict.reload"), kind: "secondary" },
      { id: "overwrite", label: t("editor.conflict.overwrite"), kind: "danger" },
    ],
  });
  tab.saving = false;
  if (r.button === "overwrite") return saveTab(tab, { force: true });
  if (r.button === "reload") { await reloadTab(tab); return false; }
  if (r.button === "compare") { await openCompare(tab); return false; }
  return false;
}

export async function saveAll(): Promise<boolean> {
  let ok = true;
  for (const tab of app.tabs) if (tab.dirty) ok = (await saveTab(tab, { quiet: false })) && ok;
  return ok;
}

const autoSaveSoon = debounce(() => {
  for (const tab of app.tabs) if (tab.dirty && !tab.external) void saveTab(tab, { quiet: true });
}, 1000);

function scheduleAutoSave() {
  if (app.settings.autoSave === "afterDelay") autoSaveSoon();
}

export function onWindowBlur() {
  if (app.settings.autoSave === "onFocusChange") {
    for (const tab of app.tabs) if (tab.dirty && !tab.external) void saveTab(tab, { quiet: true });
  }
}

// 状态栏短暂提示
let flashText = "";
let flashTimer: ReturnType<typeof setTimeout> | undefined;
export function flashStatus(text: string) {
  flashText = text;
  clearTimeout(flashTimer);
  flashTimer = setTimeout(() => { flashText = ""; emit("status"); }, 2500);
  emit("status");
}
export function statusFlash(): string {
  return flashText;
}

// ---------------------------------------------------------------------------
// 重新载入与外部修改检测

/** 从磁盘重新载入（只替换不同的部分，光标与撤销历史保留） */
/** 混合换行符的文件保存时统一成占多数的那种 */
function savedLineEnding(tab: Tab): "lf" | "crlf" {
  if (tab.lineEnding === "mixed") return tab.preferredLineEnding === "crlf" ? "crlf" : "lf";
  return tab.lineEnding === "crlf" ? "crlf" : "lf";
}

/** 重新从磁盘载入；encoding 给出时按该编码解码（用其他编码重新打开） */
export async function reloadTab(tab: Tab, encoding?: string) {
  if (encoding) tab.forcedEncoding = encoding;
  let f: FSFile;
  try {
    f = await call<FSFile>("fs.read", { projectId: app.projectId, path: tab.path, encoding: tab.forcedEncoding });
  } catch (e: any) {
    toast("danger", e.message);
    return;
  }
  if (!app.tabs.includes(tab)) return;
  if (tab.kind !== "text" || f.binary || f.tooLarge) {
    applyFile(tab, f);
    afterKindChange(tab);
    if (tab.kind === "text") { void attachLanguage(tab); void refreshGit(tab); }
    return;
  }
  const st = stateOf(tab)!;
  const cur = st.doc.toString();
  const next = f.content;
  if (cur !== next) {
    let a = 0;
    const max = Math.min(cur.length, next.length);
    while (a < max && cur.charCodeAt(a) === next.charCodeAt(a)) a++;
    let b = 0;
    while (b < max - a && cur.charCodeAt(cur.length - 1 - b) === next.charCodeAt(next.length - 1 - b)) b++;
    updateTabState(tab, { changes: { from: a, to: cur.length - b, insert: next.slice(a, next.length - b) }, annotations: Transaction.remote.of(true) });
  }
  tab.mtime = f.mtime;
  tab.size = f.size;
  tab.encoding = f.encoding || tab.encoding;
  tab.bom = !!f.bom;
  tab.lineEnding = f.lineEnding || tab.lineEnding;
  tab.preferredLineEnding = f.preferredLineEnding;
  tab.external = null;
  tab.savedDoc = stateOf(tab)!.doc;
  tab.metaDirty = false;
  if (tab.readOnly !== !!f.readOnly) {
    tab.readOnly = !!f.readOnly;
    updateTabState(tab, { effects: readOnlyC.reconfigure(EditorState.readOnly.of(tab.readOnly)) });
  }
  updateDirty(tab, tab.savedDoc);
  emit("external");
  emit("status");
  void refreshGit(tab);
}

let checking = false;
/** 对打开的文件调 fs.stat：没改过的静默重新载入，改过的提示 */
export async function checkExternal() {
  if (checking || !app.projectId) return;
  const tabs = app.tabs.filter((t) => (t.kind === "text" || t.kind === "binary" || t.kind === "tooLarge") && !t.saving && t.mtime);
  if (!tabs.length) return;
  checking = true;
  try {
    const stats = await call<{ path: string; exists: boolean; mtime?: string }[]>("fs.stat", { projectId: app.projectId, paths: tabs.map((t) => t.path) });
    for (const s of stats ?? []) {
      const tab = app.tabs.find((t) => t.path === s.path);
      if (!tab || tab.saving) continue;
      if (!s.exists) {
        if (tab.external !== "deleted") { tab.external = "deleted"; emit("external"); }
      } else if (s.mtime && s.mtime !== tab.mtime) {
        if (!tab.dirty) await reloadTab(tab);
        else if (tab.external !== "modified") { tab.external = "modified"; emit("external"); }
      } else if (tab.external === "deleted") {
        tab.external = null;
        emit("external");
      }
    }
  } catch { /* 下次再查 */ }
  finally { checking = false; }
}

// ---------------------------------------------------------------------------
// Git 改动行标记

export async function refreshGit(tab: Tab) {
  const roots = app.project?.vcsRoots ?? [];
  if (!roots.length || !app.project || tab.kind !== "text") return;
  const norm = (p: string) => p.replace(/\\/g, "/").replace(/\/+$/, "");
  const abs = norm(app.project.path) + "/" + tab.path;
  let root = null as null | { path: string };
  for (const r of roots) {
    const rp = norm(r.path);
    if ((abs.startsWith(rp + "/")) && (!root || rp.length > norm(root.path).length)) root = r;
  }
  if (!root) return;
  const rel = abs.slice(norm(root.path).length + 1);
  let blocks: ChangeBlock[] = [];
  try {
    const d = await call<VCSDiff>("vcs.diff", { projectId: app.projectId, root: root.path, path: rel, context: 0 });
    blocks = blocksFromDiff(d);
  } catch { /* 不在仓库里或没有 git 插件 */ }
  if (!app.tabs.includes(tab) || tab.kind !== "text") return;
  updateTabState(tab, { effects: setGitBlocks.of(blocks) });
}

// ---------------------------------------------------------------------------
// 导航历史（后退/前进）与最近文件

const nav: { path: string; pos: number }[] = [];
let navIndex = -1;

export function pushHistory() {
  const tab = app.active;
  if (tab?.kind !== "text") return;
  const pos = app.view.state.selection.main.head;
  const last = nav[navIndex];
  if (last && last.path === tab.path && Math.abs(last.pos - pos) < 2) return;
  nav.splice(navIndex + 1);
  nav.push({ path: tab.path, pos });
  if (nav.length > 50) nav.shift();
  navIndex = nav.length - 1;
}

export async function navigate(dir: 1 | -1): Promise<boolean> {
  if (dir < 0 && navIndex === nav.length - 1) {
    pushHistory();
    navIndex = nav.length - 1;
  }
  const target = nav[navIndex + dir];
  if (!target) return false;
  navIndex += dir;
  await openFile(target.path, { history: false, select: { from: target.pos, to: target.pos } });
  return true;
}

function remember(path: string) {
  const key = "recent." + app.projectId;
  const list = store.get<string[]>(key, []).filter((p) => p !== path);
  list.unshift(path);
  store.set(key, list.slice(0, 30));
}

export function recentFiles(): string[] {
  return store.get<string[]>("recent." + app.projectId, []);
}

// ---------------------------------------------------------------------------
// 对比（懒加载 @codemirror/merge）

export async function openCompare(tab: Tab) {
  let disk: FSFile;
  try {
    disk = await call<FSFile>("fs.read", { projectId: app.projectId, path: tab.path, encoding: tab.forcedEncoding });
  } catch (e: any) {
    toast("danger", e.message);
    return;
  }
  const { showCompare } = await import("./compare");
  const st = stateOf(tab)!;
  showCompare({
    title: basename(tab.path),
    path: dirname(tab.path),
    disk: disk.content,
    mine: st.doc.toString(),
    extensions: globalExtensions(),
    language: tab.lang ? loadLanguage(tab.lang).catch(() => []) : Promise.resolve([]),
    onKeepMine: async (content) => {
      if (content !== st.doc.toString()) {
        const cur = stateOf(tab)!;
        updateTabState(tab, { changes: { from: 0, to: cur.doc.length, insert: content } });
      }
      tab.mtime = disk.mtime;
      tab.external = null;
      await saveTab(tab, { force: true });
    },
    onUseDisk: () => reloadTab(tab),
  });
}
