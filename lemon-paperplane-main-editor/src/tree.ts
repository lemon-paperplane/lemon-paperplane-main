// 左侧文件树：fs.list 按需逐级展开；新建、重命名、删除到废纸篓、在访达中显示。
import { app, on, openFile, closeTab, emit, retargetTab } from "./app";
import { call, type FSEntry } from "./api";
import { h, dirname, joinPath, isWindows } from "./util";
import { icon, iconButton, showMenu, dialog, toast } from "./ui";
import { t } from "./i18n";
import { fileIcon } from "./fileicons";
import { copyText, absolutePath, revealPath, setLocateImpl } from "./actions";
import { revealKey } from "./tabs";

interface Node {
  entry: FSEntry;
  depth: number;
  row: HTMLElement;
  kids: HTMLElement | null;
  expanded: boolean;
  loaded: boolean;
}

const nodes = new Map<string, Node>();
let rootBox: HTMLElement;
let rootNode: Node;
let selected: string | null = null;
let panel: HTMLElement;
let ensureVisible: () => void = () => {};
let listening = false;

export function createTree(onCollapse: () => void, show: () => void): HTMLElement {
  ensureVisible = show;
  rootBox = h("div.pp-tree-body", { tabIndex: 0, role: "tree" });
  rootBox.addEventListener("keydown", onKey);
  rootBox.addEventListener("contextmenu", (e) => {
    if (e.target === rootBox) { e.preventDefault(); dirMenu(rootNode, e); }
  });
  const title = h("span.pp-tree-title", null, app.project?.name ?? "");
  title.title = app.project?.path ?? "";
  panel = h("div.pp-tree", null,
    h("div.pp-tree-header", null, title,
      iconButton("file-plus", t("editor.tree.newFile"), () => newEntry(targetDir(), false), true),
      iconButton("folder-plus", t("editor.tree.newFolder"), () => newEntry(targetDir(), true), true),
      iconButton("locate-fixed", t("editor.tree.locate"), () => app.active && locate(app.active.path), true),
      iconButton("chevrons-down-up", t("editor.tree.collapseAll"), collapseAll, true),
      iconButton("panel-left-close", t("editor.tree.hide"), onCollapse, true)),
    rootBox);
  nodes.clear();
  selected = null;
  rootNode = { entry: { name: "", path: "", dir: true, size: 0, mtime: "" }, depth: -1, row: h("div"), kids: rootBox, expanded: true, loaded: false };
  nodes.set("", rootNode);
  void load(rootNode);
  setLocateImpl(locate);
  if (!listening) {
    listening = true;
    on("active", () => highlightActive());
  }
  return panel;
}

function targetDir(): string {
  const n = selected != null ? nodes.get(selected) : null;
  if (!n) return "";
  return n.entry.dir ? n.entry.path : dirname(n.entry.path);
}

async function load(n: Node) {
  let res: { entries: FSEntry[]; truncated: boolean };
  try {
    res = await call("fs.list", { projectId: app.projectId, path: n.entry.path });
  } catch (e: any) {
    if (n === rootNode) rootBox.replaceChildren(h("div.pp-empty-small", null, e.message));
    return;
  }
  n.loaded = true;
  const keep = new Set(res.entries.map((e) => e.path));
  // 去掉已不存在的子节点
  for (const [p, c] of nodes) if (p && dirname(p) === n.entry.path && !keep.has(p)) dropNode(p, c);
  const els: HTMLElement[] = [];
  for (const e of res.entries) {
    let c = nodes.get(e.path);
    if (c && c.entry.dir !== e.dir) { dropNode(e.path, c); c = undefined; }
    if (!c) {
      c = { entry: e, depth: n.depth + 1, row: null as any, kids: null, expanded: false, loaded: false };
      c.row = makeRow(c);
      nodes.set(e.path, c);
    } else {
      c.entry = e;
    }
    els.push(c.row);
    if (c.kids) els.push(c.kids);
  }
  if (res.truncated) els.push(h("div.pp-tree-more", { style: { paddingLeft: indent(n.depth + 1) } }, "…"));
  n.kids!.replaceChildren(...els);
  highlightActive();
}

function dropNode(path: string, n: Node) {
  n.row.remove();
  n.kids?.remove();
  for (const p of [...nodes.keys()]) if (p === path || p.startsWith(path + "/")) nodes.delete(p);
}

function indent(depth: number): string {
  return 8 + depth * 16 + "px";
}

function makeRow(n: Node): HTMLElement {
  const e = n.entry;
  const chevron = e.dir ? h("span.pp-tree-chevron", null, icon("chevron-right", 14)) : h("span.pp-tree-chevron");
  const row = h("div.pp-tree-row" + (e.ignored ? ".ignored" : "") + (e.hidden ? ".hidden" : ""),
    { role: "treeitem", style: { paddingLeft: indent(n.depth) }, title: e.path, dataset: { path: e.path } },
    chevron,
    e.dir ? icon(e.symlink ? "folder-symlink" : "folder", 16, "pp-file-icon folder") : fileIcon(e.path),
    h("span.pp-tree-name", null, e.name));
  row.addEventListener("mousedown", (ev) => {
    if (ev.button !== 0) return;
    select(e.path);
    if (e.dir && (ev.target as HTMLElement).closest(".pp-tree-chevron")) void toggle(n);
  });
  row.addEventListener("click", (ev) => {
    if (e.dir && !(ev.target as HTMLElement).closest(".pp-tree-chevron") && ev.detail === 1) void toggle(n);
  });
  row.addEventListener("dblclick", () => { if (!e.dir) void openFile(e.path); });
  row.addEventListener("contextmenu", (ev) => {
    ev.preventDefault();
    select(e.path);
    if (e.dir) dirMenu(n, ev); else fileMenu(n, ev);
  });
  return row;
}

async function toggle(n: Node, open = !n.expanded) {
  if (!n.entry.dir) return;
  n.expanded = open;
  n.row.classList.toggle("expanded", open);
  if (open) {
    if (!n.kids) {
      n.kids = h("div.pp-tree-kids", { role: "group" });
      n.row.after(n.kids);
    }
    n.kids.hidden = false;
    if (!n.loaded) await load(n);
  } else if (n.kids) {
    n.kids.hidden = true;
  }
}

function collapseAll() {
  for (const n of nodes.values()) if (n !== rootNode && n.expanded) void toggle(n, false);
}

function select(path: string | null) {
  if (selected != null) nodes.get(selected)?.row.classList.remove("selected");
  selected = path;
  if (path != null) {
    const n = nodes.get(path);
    n?.row.classList.add("selected");
    n?.row.scrollIntoView({ block: "nearest" });
  }
}

function highlightActive() {
  rootBox.querySelector(".pp-tree-row.current")?.classList.remove("current");
  if (app.active) nodes.get(app.active.path)?.row.classList.add("current");
}

/** 展开到某个文件并选中 */
export async function locate(path: string, focus = true) {
  ensureVisible();
  const parts = path.split("/");
  let cur = "";
  for (let i = 0; i < parts.length - 1; i++) {
    cur = joinPath(cur, parts[i]);
    const n = nodes.get(cur) ?? (await (async () => { const p = nodes.get(dirname(cur)); if (p && !p.loaded) await load(p); return nodes.get(cur); })());
    if (!n) return;
    await toggle(n, true);
  }
  select(path);
  if (focus) rootBox.focus({ preventScroll: true });
}

/** 刷新已展开的目录（窗口获得焦点、文件操作之后） */
export async function refreshTree(dir?: string) {
  if (dir != null) {
    const n = nodes.get(dir);
    if (n?.loaded) await load(n);
    return;
  }
  for (const n of [...nodes.values()]) if (n.entry.dir && n.loaded && n.expanded) await load(n);
}

// ---------------------------------------------------------------------------
// 键盘：上下移动、左右收起展开、回车打开、F2 重命名、删除

function visibleRows(): HTMLElement[] {
  return [...rootBox.querySelectorAll<HTMLElement>(".pp-tree-row")].filter((r) => r.offsetParent !== null);
}

function onKey(e: KeyboardEvent) {
  const rows = visibleRows();
  const i = selected != null ? rows.findIndex((r) => r.dataset.path === selected) : -1;
  const n = selected != null ? nodes.get(selected) : undefined;
  switch (e.key) {
    case "ArrowDown": select(rows[Math.min(i + 1, rows.length - 1)]?.dataset.path ?? null); break;
    case "ArrowUp": select(rows[Math.max(i - 1, 0)]?.dataset.path ?? null); break;
    case "ArrowRight": if (n?.entry.dir) { if (!n.expanded) void toggle(n, true); else select(rows[i + 1]?.dataset.path ?? selected); } break;
    case "ArrowLeft":
      if (n?.entry.dir && n.expanded) void toggle(n, false);
      else if (n) { const p = dirname(n.entry.path); if (nodes.has(p) && p) select(p); }
      break;
    case "Enter": if (n) { if (n.entry.dir) void toggle(n); else void openFile(n.entry.path); } break;
    case "F2": if (n) void rename(n); break;
    case "Delete": case "Backspace": if (n && (e.key === "Delete" || e.metaKey)) void remove([n]); else return; break;
    default: return;
  }
  e.preventDefault();
  e.stopPropagation();
}

// ---------------------------------------------------------------------------
// 右键菜单与文件操作

function fileMenu(n: Node, e: MouseEvent) {
  showMenu([
    { label: t("editor.tree.open"), run: () => void openFile(n.entry.path) },
    { divider: true },
    { label: t("editor.tree.newFile"), icon: "file-plus", run: () => void newEntry(dirname(n.entry.path), false) },
    { label: t("editor.tree.newFolder"), icon: "folder-plus", run: () => void newEntry(dirname(n.entry.path), true) },
    { divider: true },
    { label: t("editor.tree.rename"), icon: "pencil", shortcut: "F2", run: () => void rename(n) },
    { label: t("editor.tree.delete"), icon: "trash-2", danger: true, run: () => void remove([n]) },
    { divider: true },
    { label: t("editor.action.copyPath"), icon: "copy", run: () => copyText(absolutePath(n.entry.path)) },
    { label: t("editor.action.copyRelativePath"), run: () => copyText(n.entry.path) },
    { label: t(revealKey()), icon: "folder-open", run: () => revealPath(n.entry.path) },
  ], { x: e.clientX, y: e.clientY });
}

function dirMenu(n: Node, e: MouseEvent) {
  const isRoot = n === rootNode;
  showMenu([
    { label: t("editor.tree.newFile"), icon: "file-plus", run: () => void newEntry(n.entry.path, false) },
    { label: t("editor.tree.newFolder"), icon: "folder-plus", run: () => void newEntry(n.entry.path, true) },
    { divider: true },
    { label: t("editor.tree.rename"), icon: "pencil", disabled: isRoot, run: () => void rename(n) },
    { label: t("editor.tree.delete"), icon: "trash-2", danger: true, disabled: isRoot, run: () => void remove([n]) },
    { divider: true },
    { label: t("editor.action.copyPath"), icon: "copy", run: () => copyText(absolutePath(n.entry.path)) },
    { label: t("editor.action.copyRelativePath"), disabled: isRoot, run: () => copyText(n.entry.path) },
    { label: t(revealKey()), icon: "folder-open", run: () => revealPath(n.entry.path) },
    { label: t("editor.tree.refresh"), icon: "refresh-cw", run: () => void refreshTree(n.entry.path) },
  ], { x: e.clientX, y: e.clientY });
}

function validName(v: string): string | null {
  const s = v.trim();
  if (!s) return t("editor.tree.nameEmpty");
  if (/(^|\/)\.\.?(\/|$)/.test(s) || s.startsWith("/") || /[\\:*?"<>|]/.test(s)) return t("editor.tree.nameInvalid");
  return null;
}

async function newEntry(dir: string, isDir: boolean) {
  const r = await dialog({
    title: t(isDir ? "editor.tree.newFolder" : "editor.tree.newFile"),
    message: dir ? dir + "/" : app.project?.name,
    input: { placeholder: t(isDir ? "editor.tree.folderName" : "editor.tree.fileName"), validate: validName },
    buttons: [{ id: "cancel", label: t("common.cancel"), kind: "secondary" }, { id: "ok", label: t("common.create"), kind: "primary" }],
  });
  if (r.button !== "ok") return;
  const path = joinPath(dir, r.value.trim().replace(/\/+$/, ""));
  try {
    const res = await call<{ path: string }>("fs.create", { projectId: app.projectId, path, dir: isDir });
    const parent = nodes.get(dir);
    if (parent && !parent.expanded) await toggle(parent, true);
    await refreshTree(dir);
    // 名字里带 / 时中间目录也要刷新
    await locate(res.path || path, isDir);
    if (!isDir) await openFile(res.path || path);
  } catch (e: any) {
    toast("danger", e.message);
  }
}

async function rename(n: Node) {
  const name = n.entry.name;
  const dot = n.entry.dir ? -1 : name.lastIndexOf(".");
  const r = await dialog({
    title: t("editor.tree.rename"),
    input: { value: name, selectRange: [0, dot > 0 ? dot : name.length], validate: validName },
    buttons: [{ id: "cancel", label: t("common.cancel"), kind: "secondary" }, { id: "ok", label: t("editor.tree.rename"), kind: "primary" }],
  });
  const to = joinPath(dirname(n.entry.path), r.value.trim());
  if (r.button !== "ok" || to === n.entry.path) return;
  const from = n.entry.path;
  try {
    const res = await call<{ path: string }>("fs.rename", { projectId: app.projectId, from, to });
    const dest = res.path || to;
    // 打开的标签跟着改名
    for (const tab of app.tabs) {
      if (tab.path === from || tab.path.startsWith(from + "/")) retargetTab(tab, dest + tab.path.slice(from.length));
    }
    await refreshTree(dirname(from));
    if (dirname(dest) !== dirname(from)) await refreshTree(dirname(dest));
    select(dest);
  } catch (e: any) {
    toast("danger", e.message);
  }
}

async function remove(list: Node[]) {
  const names = list.map((n) => n.entry.name).join("、");
  const r = await dialog({
    title: t("editor.tree.deleteTitle"),
    message: t(isWindows ? "editor.tree.deleteMessageWin" : "editor.tree.deleteMessage", names),
    buttons: [{ id: "cancel", label: t("common.cancel"), kind: "secondary" }, { id: "ok", label: t(isWindows ? "editor.tree.moveToRecycleBin" : "editor.tree.moveToTrash"), kind: "danger" }],
  });
  if (r.button !== "ok") return;
  try {
    const paths = list.map((n) => n.entry.path);
    await call("fs.delete", { projectId: app.projectId, paths });
    for (const tab of [...app.tabs]) {
      if (paths.some((p) => tab.path === p || tab.path.startsWith(p + "/"))) {
        if (tab.dirty) { tab.external = "deleted"; emit("external"); }
        else await closeTab(tab, { force: true });
      }
    }
    for (const p of new Set(paths.map(dirname))) await refreshTree(p);
    select(null);
  } catch (e: any) {
    toast("danger", e.message);
  }
}

export function treeHasFocus(): boolean {
  return rootBox.contains(document.activeElement);
}

export function focusTree() {
  if (selected == null && app.active) select(app.active.path);
  rootBox.focus();
}
