// 标签栏：未保存标记、关闭、中键关闭、拖动排序、右键菜单。
import { app, on, activate, closeTab, closeTabs, moveTab, type Tab } from "./app";
import { h, basename, dirname } from "./util";
import { icon, showMenu } from "./ui";
import { t } from "./i18n";
import { shortcutFor } from "./keymaps";
import { fileIcon } from "./fileicons";
import { copyText, absolutePath, revealPath, locateInTree } from "./actions";

let bar: HTMLElement;
let list: HTMLElement;
let drag: { tab: Tab; startX: number; moved: boolean } | null = null;
let listening = false;

export function createTabBar(leading: HTMLElement): HTMLElement {
  list = h("div.pp-tabs", { role: "tablist" });
  list.addEventListener("wheel", (e) => {
    if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) { list.scrollLeft += e.deltaY; e.preventDefault(); }
  }, { passive: false });
  bar = h("div.pp-tabbar", null, leading, list);
  if (!listening) {
    listening = true;
    on("tabs", render);
    on("active", render);
  }
  render();
  return bar;
}

function sc(cmd: string): string {
  return app.keymap ? shortcutFor(app.keymap.bindings, cmd) : "";
}

function render() {
  const names = new Map<string, number>();
  for (const tab of app.tabs) names.set(basename(tab.path), (names.get(basename(tab.path)) ?? 0) + 1);
  list.replaceChildren(...app.tabs.map((tab) => {
    const name = basename(tab.path);
    const hint = (names.get(name) ?? 0) > 1 ? basename(dirname(tab.path)) || "/" : "";
    const close = h("button.pp-tab-close", { type: "button", tabIndex: -1, "data-tip": t("editor.tab.close") + (sc("closeTab") ? `  ${sc("closeTab")}` : ""), "aria-label": t("editor.tab.close") },
      icon("x", 14), h("span.pp-tab-dot"));
    close.addEventListener("mousedown", (e) => e.stopPropagation());
    close.addEventListener("click", (e) => { e.stopPropagation(); void closeTab(tab); });
    const el = h("div.pp-tab" + (tab === app.active ? ".active" : "") + (tab.dirty ? ".dirty" : "") + (tab.external === "deleted" ? ".deleted" : "") + (drag?.tab === tab && drag.moved ? ".dragging" : ""),
      { role: "tab", "aria-selected": String(tab === app.active), title: tab.path, dataset: { id: String(tab.id) } },
      fileIcon(tab.path, tab.kind),
      h("span.pp-tab-name", null, name),
      hint ? h("span.pp-tab-hint", null, hint) : null,
      tab.readOnly ? icon("lock", 12, "pp-tab-lock") : null,
      close);
    el.addEventListener("mousedown", (e) => onDown(e, tab));
    el.addEventListener("auxclick", (e) => { if (e.button === 1) { e.preventDefault(); void closeTab(tab); } });
    el.addEventListener("contextmenu", (e) => { e.preventDefault(); tabMenu(tab, e); });
    return el;
  }));
  const act = list.querySelector(".pp-tab.active") as HTMLElement | null;
  act?.scrollIntoView({ block: "nearest", inline: "nearest" });
}

function onDown(e: MouseEvent, tab: Tab) {
  if (e.button === 1) { e.preventDefault(); return; }
  if (e.button !== 0) return;
  activate(tab);
  drag = { tab, startX: e.clientX, moved: false };
  const move = (ev: MouseEvent) => {
    if (!drag) return;
    if (!drag.moved) {
      if (Math.abs(ev.clientX - drag.startX) < 4) return;
      drag.moved = true;
      render();
    }
    // 拖进哪个标签的位置就实时移到哪里
    const els = [...list.querySelectorAll<HTMLElement>(".pp-tab")];
    let to = els.length - 1;
    for (let i = 0; i < els.length; i++) {
      const r = els[i].getBoundingClientRect();
      if (ev.clientX < r.left + r.width / 2) { to = i; break; }
    }
    const from = app.tabs.indexOf(drag.tab);
    if (to > from) to--;
    if (to !== from && to >= 0) moveTab(drag.tab, to);
  };
  const up = () => {
    document.removeEventListener("mousemove", move);
    document.removeEventListener("mouseup", up);
    const was = drag?.moved;
    drag = null;
    if (was) render();
  };
  document.addEventListener("mousemove", move);
  document.addEventListener("mouseup", up);
}

function tabMenu(tab: Tab, e: MouseEvent) {
  const i = app.tabs.indexOf(tab);
  showMenu([
    { label: t("editor.tab.close"), shortcut: sc("closeTab"), run: () => void closeTab(tab) },
    { label: t("editor.tab.closeOthers"), disabled: app.tabs.length < 2, run: () => void closeTabs(app.tabs.filter((x) => x !== tab)) },
    { label: t("editor.tab.closeRight"), disabled: i === app.tabs.length - 1, run: () => void closeTabs(app.tabs.slice(i + 1)) },
    { label: t("editor.tab.closeAll"), shortcut: sc("closeAllTabs"), run: () => void closeTabs(app.tabs) },
    { divider: true },
    { label: t("editor.action.copyPath"), icon: "copy", run: () => copyText(absolutePath(tab.path)) },
    { label: t("editor.action.copyRelativePath"), run: () => copyText(tab.path) },
    { label: t("editor.tree.locate"), icon: "locate-fixed", run: () => locateInTree(tab.path) },
    { label: t(revealKey()), icon: "folder-open", run: () => revealPath(tab.path) },
  ], { x: e.clientX, y: e.clientY });
}

export function revealKey(): string {
  return /Mac/.test(navigator.platform) ? "editor.action.revealMac" : /Win/.test(navigator.platform) ? "editor.action.revealWindows" : "editor.action.revealLinux";
}
