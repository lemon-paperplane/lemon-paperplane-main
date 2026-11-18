// 编辑器网页入口：布局、界面级命令、宿主桥接、焦点与外部修改检测。
import "./gen/tokens.css";
import "./styles.css";
import { runScopeHandlers, type EditorView } from "@codemirror/view";
import { openSearchPanel } from "@codemirror/search";
import { setToken } from "./api";
import { exposeHostApi, post, hasHost } from "./bridge";
import { loadLocale, t } from "./i18n";
import {
  app, on, emit, initCore, openFile, activate, closeTab, closeTabs, saveTab, saveAll, reopenClosed, anyDirty,
  setHostTheme, applySettings, toggleWhitespace, checkExternal, refreshFromHost, refreshGit, onWindowBlur, navigate,
  reloadTab, resolveConflict, openCompare, updateTitle, setGitClickHandler, focusEditor, editorRoot,
} from "./app";
import { registerCommands, runCommand, type Command } from "./commands";
import { shortcutFor } from "./keymaps";
import { h, fontStack, platform, store, clamp } from "./util";
import { icon, iconButton, dialog, dialogOpen, installTooltips, popover, toast } from "./ui";
import { fonts } from "./gen/tokens";
import { createTabBar, revealKey } from "./tabs";
import { createTree, locate, refreshTree, focusTree } from "./tree";
import { createStatusBar } from "./statusbar";
import { quickOpen, goToLine, closePalette } from "./palette";
import { initFindInFiles, openFindInFiles } from "./findinfiles";
import { setReplaceVisible, focusReplace } from "./findpanel";
import { openSettings } from "./settingsui";
import { nextChange, revertBlock, type ChangeBlock } from "./gitgutter";
import { revealPath } from "./actions";

const params = new URLSearchParams(location.hash.slice(1));
app.projectId = params.get("projectId") ?? "";
setToken(params.get("token") ?? "");

// 字体按平台取 design-tokens
const rootStyle = document.documentElement.style;
rootStyle.setProperty("--font-ui", fontStack((fonts.ui as any)[platform] ?? "", "-apple-system, BlinkMacSystemFont, system-ui, sans-serif"));
rootStyle.setProperty("--font-mono", fontStack((fonts.mono as any)[platform] ?? "", "monospace"));
document.documentElement.dataset.platform = platform;

// ---------------------------------------------------------------------------
// 布局

const cmHost = h("div.pp-cm");
const placeholder = h("div.pp-placeholder");
const banner = h("div.pp-banner-host");
const editorArea = h("div.pp-editor-area", null, cmHost, placeholder);
const island = h("div.pp-island", { id: "pp-island" });
const sidebar = h("aside.pp-sidebar");
const splitter = h("div.pp-splitter", { role: "separator" });
const main = h("main.pp-main", null, island);
const root = h("div.pp-app", null, sidebar, splitter, main);
document.body.appendChild(root);

let sidebarOpen = store.get("sidebar.open", true);
const showSidebarBtn = iconButton("panel-left-open", t("editor.tree.show"), () => setSidebar(true), true);

function setSidebar(open: boolean) {
  sidebarOpen = open;
  store.set("sidebar.open", open);
  root.classList.toggle("no-sidebar", !open);
  showSidebarBtn.hidden = open;
}

function initSplitter() {
  const w = clamp(store.get("sidebar.width", 232), 180, 480);
  rootStyle.setProperty("--sidebar-width", w + "px");
  splitter.addEventListener("mousedown", (e) => {
    e.preventDefault();
    const startX = e.clientX, startW = sidebar.offsetWidth;
    root.classList.add("resizing");
    const move = (ev: MouseEvent) => rootStyle.setProperty("--sidebar-width", clamp(startW + ev.clientX - startX, 180, Math.min(480, innerWidth - 300)) + "px");
    const up = () => {
      root.classList.remove("resizing");
      document.removeEventListener("mousemove", move);
      document.removeEventListener("mouseup", up);
      store.set("sidebar.width", sidebar.offsetWidth);
    };
    document.addEventListener("mousemove", move);
    document.addEventListener("mouseup", up);
  });
}

let keymapRenderer: () => void = () => {};
on("settings", () => keymapRenderer());
on("contribs", () => keymapRenderer());

function buildChrome() {
  sidebar.replaceChildren(createTree(() => setSidebar(false), () => setSidebar(true)));
  const settingsBtn = iconButton("settings-2", t("editor.settings.title"), (e) => openSettings(e.currentTarget as HTMLElement), true);
  const keymapEl = h("button.pp-status-item", { type: "button", "data-tip": t("editor.settings.keymap"), onclick: (e: MouseEvent) => openSettings(e.currentTarget as HTMLElement) });
  const renderKeymap = () => {
    const k = app.contribs.keymaps.find((x) => x.id === app.keymap?.id);
    keymapEl.replaceChildren(icon("keyboard", 12), h("span", null, k?.name ?? "—"));
  };
  keymapRenderer = renderKeymap;
  renderKeymap();
  const status = createStatusBar([keymapEl, settingsBtn]);
  island.replaceChildren(createTabBar(showSidebarBtn), banner, editorArea);
  main.replaceChildren(island, status);
  initFindInFiles(island);
  emit("tabs");
  emit("active");
  emit("status");
}

// ---------------------------------------------------------------------------
// 占位（空、二进制、过大、出错）与外部修改提示条

function renderPlaceholder() {
  const tab = app.active;
  const text = tab?.kind === "text";
  cmHost.hidden = !text;
  placeholder.hidden = text;
  if (text) return;
  const empty = (ic: string, title: string, btn?: { label: string; run: () => void }) =>
    h("div.pp-empty", null, icon(ic, 32), h("div.pp-empty-title", null, title),
      btn ? h("button.pp-btn.primary", { type: "button", onclick: btn.run }, btn.label) : null);
  if (!tab) {
    const sc = app.keymap ? shortcutFor(app.keymap.bindings, "quickOpen") : "";
    placeholder.replaceChildren(empty("file-text", t("editor.empty.title"), { label: t("editor.empty.quickOpen") + (sc ? `  ${sc}` : ""), run: () => quickOpen() }));
  } else if (tab.kind === "loading") {
    placeholder.replaceChildren(h("div.pp-empty.loading", null, icon("loader-circle", 20, "spin")));
  } else if (tab.kind === "binary" && tab.imageUrl) {
    placeholder.replaceChildren(h("div.pp-image-preview", null, h("img", { src: tab.imageUrl, alt: tab.path, draggable: false })));
  } else if (tab.kind === "binary") {
    const ext = tab.path.slice(tab.path.lastIndexOf(".")).toLowerCase();
    const isImage = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif"].includes(ext);
    placeholder.replaceChildren(empty(isImage ? "file-image" : "binary", t(isImage ? "editor.file.imageTooLarge" : "editor.file.binary"), { label: t(revealKey()), run: () => revealPath(tab.path) }));
  } else if (tab.kind === "tooLarge") {
    placeholder.replaceChildren(empty("file-question", t("editor.file.tooLarge"), { label: t(revealKey()), run: () => revealPath(tab.path) }));
  } else {
    placeholder.replaceChildren(empty("circle-alert", tab.error || t("common.failed"), { label: t("editor.file.retry"), run: () => { closeTab(tab, { force: true }).then(() => openFile(tab.path)); } }));
  }
}

function renderBanner() {
  const tab = app.active;
  const bar = (tone: string, ic: string, text: string, buttons: [string, () => void, string?][]) =>
    h("div.pp-banner." + tone, { role: "alert" }, icon(ic, 16, "tone"), h("span.pp-banner-text", null, text),
      ...buttons.map(([label, run, kind]) => h("button.pp-btn.small." + (kind ?? "secondary"), { type: "button", onclick: run }, label)));
  if (!tab || tab.kind !== "text") { banner.replaceChildren(); return; }
  if (tab.external === "modified") {
    banner.replaceChildren(bar("warning", "triangle-alert", t("editor.external.modified"), [
      [t("editor.conflict.compare"), () => void openCompare(tab), "ghost"],
      [t("editor.external.keepMine"), () => void resolveConflict(tab)],
      [t("editor.conflict.reload"), () => void reloadTab(tab), "primary"],
    ]));
  } else if (tab.external === "deleted") {
    banner.replaceChildren(bar("danger", "circle-alert", t("editor.external.deleted"), [
      [t("editor.tab.close"), () => void closeTab(tab, { force: true })],
      [t("editor.deleted.recreate"), () => void saveTab(tab, { create: true }), "primary"],
    ]));
  } else if (tab.readOnly && tab.size > 5 * 1024 * 1024) {
    banner.replaceChildren(bar("info", "info", t("editor.file.largeReadOnly"), []));
  } else {
    banner.replaceChildren();
  }
}

on("active", () => { renderPlaceholder(); renderBanner(); });
on("tabs", renderPlaceholder);
on("external", renderBanner);

// Git 标记点开：显示 HEAD 里的原内容，可回滚
setGitClickHandler((view: EditorView, block: ChangeBlock, dom: HTMLElement) => {
  const old = block.oldLines;
  const content = h("div.pp-git-pop", null,
    h("div.pp-git-pop-head", null,
      h("span.pp-git-pop-title", null, t(block.kind === "added" ? "editor.git.added" : block.kind === "deleted" ? "editor.git.deleted" : "editor.git.modified")),
      iconButton("arrow-up", t("editor.git.previous"), () => { close(); nextChange(view, -1); }, true),
      iconButton("arrow-down", t("editor.git.next"), () => { close(); nextChange(view, 1); }, true),
      iconButton("undo-2", t("editor.git.revert"), () => { close(); revertBlock(view, block); }, true)),
    old.length ? h("pre.pp-git-pop-old", null, old.slice(0, 200).join("\n") + (old.length > 200 ? "\n…" : "")) : null);
  const close = popover(content, dom);
});

// ---------------------------------------------------------------------------
// 界面级命令

const g = (run: (view: EditorView) => unknown): Command => ({ global: true, run: (v) => { void run(v); return true; } });
const textTab = () => (app.active?.kind === "text" ? app.active : null);
const tabAt = (d: number) => {
  if (!app.tabs.length || !app.active) return;
  const i = app.tabs.indexOf(app.active);
  activate(app.tabs[(i + d + app.tabs.length) % app.tabs.length]);
};

registerCommands({
  save: g(() => { const tab = app.active; if (tab?.kind === "text") return saveTab(tab, { force: !tab.dirty && !!tab.external }); }),
  saveAll: g(() => saveAll()),
  closeTab: g(() => app.active && closeTab(app.active)),
  closeOtherTabs: g(() => closeTabs(app.tabs.filter((x) => x !== app.active))),
  closeTabsToRight: g(() => app.active && closeTabs(app.tabs.slice(app.tabs.indexOf(app.active) + 1))),
  closeAllTabs: g(() => closeTabs(app.tabs)),
  nextTab: g(() => tabAt(1)),
  previousTab: g(() => tabAt(-1)),
  reopenClosedTab: g(() => reopenClosed()),
  quickOpen: g(() => quickOpen()),
  quickSwitchTab: g(() => quickOpen("tabs")),
  findInFiles: g(() => openFindInFiles()),
  goToLine: g(() => goToLine()),
  find: { global: true, run: (v) => { if (!textTab()) return false; setReplaceVisible(false); openSearchPanel(v); const p = v.dom.querySelector(".pp-find"); p?.classList.remove("with-replace"); return true; } },
  replace: { global: true, run: (v) => { if (!textTab() || app.active!.readOnly) return false; setReplaceVisible(true); openSearchPanel(v); focusReplace(v); return true; } },
  toggleSidebar: g(() => setSidebar(!sidebarOpen)),
  focusFileTree: g(() => { setSidebar(true); focusTree(); }),
  revealInTree: g(() => app.active && locate(app.active.path)),
  revealInFinder: g(() => app.active && revealPath(app.active.path)),
  newFile: g(() => { setSidebar(true); (document.querySelector(".pp-tree-header .pp-icon-btn") as HTMLElement | null)?.click(); }),
  toggleWordWrap: g(() => applySettings({ wordWrap: !app.settings.wordWrap }, true)),
  toggleWhitespace: g(() => toggleWhitespace()),
  zoomIn: g(() => applySettings({ fontSize: Math.min(32, (app.settings.fontSize || 13) + 1) }, false)),
  zoomOut: g(() => applySettings({ fontSize: Math.max(8, (app.settings.fontSize || 13) - 1) }, false)),
  resetZoom: g(() => applySettings({ fontSize: 13 }, false)),
  navigateBack: g(() => navigate(-1)),
  navigateForward: g(() => navigate(1)),
  nextChange: { run: (v) => nextChange(v, 1) },
  previousChange: { run: (v) => nextChange(v, -1) },
  reloadFromDisk: g(() => { const tab = textTab(); if (tab) return tab.dirty ? resolveConflict(tab) : reloadTab(tab); }),
  compareWithDisk: g(() => { const tab = textTab(); if (tab) return openCompare(tab); }),
  openSettings: g(() => openSettings(document.querySelector(".pp-statusbar .pp-icon-btn") as HTMLElement)),
  focusEditor: g(() => focusEditor()),
});

// 焦点不在编辑区（文件树、快速打开、查找框之外）时，界面级快捷键走 global 作用域
window.addEventListener("keydown", (e) => {
  if (e.defaultPrevented || e.isComposing || !app.view || dialogOpen() || document.querySelector(".pp-menu")) return;
  if (app.view.dom.contains(e.target as Node)) return;
  if (runScopeHandlers(app.view, e, "global")) e.preventDefault();
});

// ---------------------------------------------------------------------------
// 宿主接口

async function requestClose() {
  closePalette();
  if (!anyDirty()) { post({ type: "closeWindow" }); return; }
  const dirty = app.tabs.filter((x) => x.dirty);
  const r = await dialog({
    title: t("editor.unsaved.title"),
    message: dirty.length === 1 ? t("editor.unsaved.message", dirty[0].path) : t("editor.unsaved.messageMany", dirty.length),
    buttons: [
      { id: "discard", label: t("editor.unsaved.discard"), kind: "ghost" },
      { id: "cancel", label: t("common.cancel"), kind: "secondary" },
      { id: "save", label: t("editor.unsaved.saveAll"), kind: "primary" },
    ],
  });
  if (r.button === "discard" || (r.button === "save" && (await saveAll()))) post({ type: "closeWindow" });
  else post({ type: "closeCancelled" });
}

exposeHostApi({
  openFile: (path, line) => { void openFile(path, { line: line ?? undefined }); },
  setTheme: (theme) => { setHostTheme(theme); emit("settings"); },
  setLocale: async (l) => { await loadLocale(l); buildChrome(); updateTitle(); },
  requestClose: () => { void requestClose(); },
  saveAll: () => { void saveAll(); },
});

// ---------------------------------------------------------------------------
// 焦点、可见性与外部修改检测（每 3 秒，窗口不可见时暂停）

let pollTimer: ReturnType<typeof setInterval> | undefined;
function startPolling() {
  stopPolling();
  pollTimer = setInterval(() => { if (document.visibilityState === "visible") void checkExternal(); }, 3000);
}
function stopPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = undefined;
}

window.addEventListener("focus", () => {
  void checkExternal();
  void refreshFromHost();
  void refreshTree();
  if (app.active) void refreshGit(app.active);
});
window.addEventListener("blur", onWindowBlur);
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") { void checkExternal(); startPolling(); } else stopPolling();
});
window.addEventListener("pagehide", stopPolling);
// 没有宿主（浏览器调试）时，关页面前提示未保存
if (!hasHost) window.addEventListener("beforeunload", (e) => { if (anyDirty()) { e.preventDefault(); e.returnValue = ""; } });
// 文件拖进来不让 WebView 跳转
window.addEventListener("dragover", (e) => e.preventDefault());
window.addEventListener("drop", (e) => { if (!(e.target as HTMLElement)?.closest?.(".cm-content")) e.preventDefault(); });

// ---------------------------------------------------------------------------
// 启动

async function start() {
  setHostTheme(params.get("theme") === "dark" ? "dark" : "light");
  installTooltips();
  initSplitter();
  setSidebar(sidebarOpen);
  try {
    await Promise.all([loadLocale(params.get("locale")), initCore(cmHost)]);
  } catch (e: any) {
    await loadLocale(params.get("locale"));
    root.replaceChildren(h("div.pp-empty.fatal", null, icon("circle-alert", 32), h("div.pp-empty-title", null, t("editor.error.start")),
      h("div.pp-empty-detail", null, e?.message ?? String(e)),
      h("button.pp-btn.primary", { type: "button", onclick: () => location.reload() }, t("editor.file.retry"))));
    post({ type: "ready" });
    return;
  }
  editorRoot().dataset.theme = app.editorThemeKind;
  buildChrome();
  updateTitle();
  post({ type: "ready" });
  const file = params.get("file");
  if (file) {
    const line = parseInt(params.get("line") ?? "", 10);
    await openFile(file, { line: Number.isFinite(line) ? line : undefined });
    if (sidebarOpen) void locate(file, false);
  }
  startPolling();
  if (!app.keymap?.bindings.length) toast("warning", t("editor.error.keymapLoad", ""));
}

void start();
// 调试用：命令可在控制台直接执行
(window as any).paperplaneEditor = { app, runCommand: (id: string) => runCommand(id, app.view) };
