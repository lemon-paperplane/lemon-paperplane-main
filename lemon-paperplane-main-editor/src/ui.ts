// 自绘界面组件：图标、菜单、对话框、提示、悬停说明（docs/design-system.md 第 7 节）。
import { ICONS } from "./gen/icons";
import { h, clamp } from "./util";
import { t } from "./i18n";

// ---------------------------------------------------------------------------
// 图标：shared/icons 内联 SVG，线宽按尺寸调细（icon.strokeScale）

export function icon(name: string, size = 16, cls = ""): SVGSVGElement {
  const sw = size <= 16 ? 1.5 : size <= 20 ? 1.75 : 2;
  const el = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  el.setAttribute("viewBox", "0 0 24 24");
  el.setAttribute("width", String(size));
  el.setAttribute("height", String(size));
  el.setAttribute("fill", "none");
  el.setAttribute("stroke", "currentColor");
  el.setAttribute("stroke-width", String(sw));
  el.setAttribute("stroke-linecap", "round");
  el.setAttribute("stroke-linejoin", "round");
  el.setAttribute("class", "pp-icon" + (cls ? " " + cls : ""));
  el.setAttribute("aria-hidden", "true");
  el.innerHTML = ICONS[name] ?? "";
  return el;
}

/** 图标按钮（28 或 22 见方），必须带说明 */
export function iconButton(name: string, tip: string, onclick: (e: MouseEvent) => void, small = false): HTMLButtonElement {
  const b = h("button.pp-icon-btn", { type: "button", "data-tip": tip, "aria-label": tip, onclick }, icon(name, small ? 14 : 16));
  if (small) b.classList.add("small");
  return b;
}

// ---------------------------------------------------------------------------
// 菜单

export interface MenuItem {
  label?: string;
  icon?: string;
  shortcut?: string;
  checked?: boolean;
  disabled?: boolean;
  danger?: boolean;
  divider?: boolean;
  header?: string;
  run?: () => void;
}

let openMenu: { el: HTMLElement; close: () => void } | null = null;

export function closeMenu() {
  openMenu?.close();
}

export function showMenu(items: MenuItem[], at: { x: number; y: number } | HTMLElement, opts: { align?: "start" | "end"; above?: boolean; minWidth?: number } = {}) {
  closeMenu();
  const prevFocus = document.activeElement as HTMLElement | null;
  const hasCheck = items.some((i) => i.checked != null);
  const hasIcon = items.some((i) => i.icon);
  const rows: HTMLElement[] = [];
  const el = h("div.pp-menu", { role: "menu", tabIndex: -1 });
  if (opts.minWidth) el.style.minWidth = opts.minWidth + "px";
  for (const it of items) {
    if (it.divider) { el.appendChild(h("div.pp-menu-divider")); continue; }
    if (it.header) { el.appendChild(h("div.pp-menu-header", null, it.header)); continue; }
    const row = h("div.pp-menu-item" + (it.disabled ? ".disabled" : "") + (it.danger ? ".danger" : ""), { role: "menuitem" },
      hasCheck ? h("span.pp-menu-check", null, it.checked ? icon("check", 14) : null) : null,
      hasIcon ? h("span.pp-menu-icon", null, it.icon ? icon(it.icon, 16) : null) : null,
      h("span.pp-menu-label", null, it.label ?? ""),
      it.shortcut ? h("span.pp-menu-shortcut", null, it.shortcut) : null);
    if (!it.disabled) {
      row.addEventListener("mouseenter", () => select(shown().indexOf(row)));
      row.addEventListener("mousedown", (e) => e.preventDefault());
      row.addEventListener("click", () => { close(); it.run?.(); });
      rows.push(row);
    }
    el.appendChild(row);
  }
  let sel = -1;
  const shown = () => rows.filter((r) => !r.hidden);
  function select(i: number) {
    const vis = shown();
    rows.forEach((r) => r.classList.remove("active"));
    sel = i;
    if (sel >= 0 && vis[sel]) { vis[sel].classList.add("active"); vis[sel].scrollIntoView({ block: "nearest" }); }
  }
  // 超过 12 项带筛选框
  let filter: HTMLInputElement | null = null;
  if (rows.length > 12) {
    filter = h("input.pp-input.pp-menu-filter", { type: "text", placeholder: t("common.filter"), spellcheck: false, autocomplete: "off" });
    filter.addEventListener("input", () => {
      const q = filter!.value.trim().toLowerCase();
      for (const c of el.children) {
        if (c === filter) continue;
        const r = c as HTMLElement;
        if (r.classList.contains("pp-menu-item")) r.hidden = !!q && !r.textContent!.toLowerCase().includes(q);
        else r.hidden = !!q;
      }
      select(q ? 0 : -1);
    });
    el.insertBefore(filter, el.firstChild);
  }
  el.addEventListener("keydown", (e) => {
    const n = shown().length;
    if (e.key === "ArrowDown") { select((sel + 1) % n); e.preventDefault(); }
    else if (e.key === "ArrowUp") { select((sel - 1 + n) % n); e.preventDefault(); }
    else if (e.key === "Enter" || (e.key === " " && e.target !== filter)) { shown()[sel]?.click(); e.preventDefault(); }
    else if (e.key === "Escape" || e.key === "Tab") { close(); e.preventDefault(); }
    e.stopPropagation();
  });
  document.body.appendChild(el);
  // 定位：贴锚点下方，空间不够翻到上方
  const r = el.getBoundingClientRect();
  let x: number, y: number;
  if (at instanceof HTMLElement) {
    const a = at.getBoundingClientRect();
    x = opts.align === "end" ? a.right - r.width : a.left;
    y = opts.above || a.bottom + r.height + 4 > innerHeight ? a.top - r.height - 4 : a.bottom + 4;
  } else {
    x = at.x; y = at.y;
    if (y + r.height > innerHeight - 4) y = Math.max(4, y - r.height);
  }
  el.style.left = clamp(x, 4, innerWidth - r.width - 4) + "px";
  el.style.top = clamp(y, 4, innerHeight - r.height - 4) + "px";
  (filter ?? el).focus({ preventScroll: true });
  const onDown = (e: MouseEvent) => { if (!el.contains(e.target as Node)) close(); };
  const onBlur = () => close();
  setTimeout(() => document.addEventListener("mousedown", onDown, true));
  window.addEventListener("blur", onBlur);
  function close() {
    if (openMenu?.el !== el) return;
    openMenu = null;
    el.remove();
    document.removeEventListener("mousedown", onDown, true);
    window.removeEventListener("blur", onBlur);
    if (document.activeElement === document.body || !document.activeElement) prevFocus?.focus({ preventScroll: true });
  }
  openMenu = { el, close };
}

// ---------------------------------------------------------------------------
// 对话框：scrim 遮罩 + 居中卡片，Esc 取消，回车主按钮

export interface DialogButton { id: string; label: string; kind?: "primary" | "secondary" | "danger" | "ghost" }
export interface DialogOptions {
  title: string;
  message?: string;
  buttons: DialogButton[];
  input?: { value?: string; placeholder?: string; selectRange?: [number, number]; validate?: (v: string) => string | null };
  content?: HTMLElement;
  width?: number;
}

let dialogDepth = 0;
export function dialogOpen(): boolean {
  return dialogDepth > 0;
}

export function dialog(o: DialogOptions): Promise<{ button: string | null; value: string }> {
  closeMenu();
  return new Promise((resolve) => {
    const prevFocus = document.activeElement as HTMLElement | null;
    dialogDepth++;
    let input: HTMLInputElement | null = null;
    let err: HTMLElement | null = null;
    const done = (button: string | null) => {
      const value = input?.value ?? "";
      if (button && input && o.input?.validate && o.buttons.find((b) => b.id === button)?.kind !== "secondary") {
        const msg = o.input.validate(value);
        if (msg) { err!.textContent = msg; input.classList.add("invalid"); input.focus(); return; }
      }
      dialogDepth--;
      scrim.remove();
      prevFocus?.focus({ preventScroll: true });
      resolve({ button, value });
    };
    const primary = o.buttons.find((b) => b.kind === "primary" || b.kind === "danger");
    const body = h("div.pp-dialog-body", null,
      o.message ? h("div.pp-dialog-message", null, o.message) : null,
      o.content ?? null,
      o.input ? [
        (input = h("input.pp-input", { type: "text", value: o.input.value ?? "", placeholder: o.input.placeholder ?? "", spellcheck: false, autocomplete: "off" })),
        (err = h("div.pp-dialog-error")),
      ] : null);
    input?.addEventListener("input", () => { input!.classList.remove("invalid"); err!.textContent = ""; });
    const card = h("div.pp-dialog", { role: "dialog", "aria-modal": "true", style: o.width ? { width: o.width + "px" } : undefined },
      h("div.pp-dialog-title", null, h("span", null, o.title), iconButton("x", t("common.close"), () => done(null), true)),
      body,
      h("div.pp-dialog-buttons", null, o.buttons.map((b) =>
        h("button.pp-btn." + (b.kind ?? "secondary"), { type: "button", onclick: () => done(b.id) }, b.label))));
    const scrim = h("div.pp-scrim", { onmousedown: (e: MouseEvent) => { if (e.target === scrim) e.preventDefault(); } }, card);
    scrim.addEventListener("keydown", (e) => {
      if (e.key === "Escape") { e.preventDefault(); done(null); }
      else if (e.key === "Enter" && !e.isComposing && primary && (e.target === input || !(e.target instanceof HTMLButtonElement))) { e.preventDefault(); done(primary.id); }
      e.stopPropagation();
    });
    document.body.appendChild(scrim);
    if (input) {
      input.focus();
      const r = o.input!.selectRange;
      if (r) input.setSelectionRange(r[0], r[1]); else input.select();
    } else {
      (card.querySelector(".pp-btn.primary, .pp-btn.danger") as HTMLElement | null ?? card.querySelector(".pp-btn") as HTMLElement)?.focus();
    }
  });
}

// ---------------------------------------------------------------------------
// 提示：右下角，最多 4 条，自动消失

let toastBox: HTMLElement | null = null;

export function toast(tone: "success" | "info" | "warning" | "danger", message: string, action?: { label: string; run: () => void }) {
  if (!toastBox) document.body.appendChild((toastBox = h("div.pp-toasts")));
  const ic = tone === "success" ? "circle-check" : tone === "danger" ? "circle-x" : tone === "warning" ? "triangle-alert" : "info";
  const el = h("div.pp-toast." + tone, { role: "status" },
    icon(ic, 16, "tone"),
    h("span.pp-toast-text", null, message),
    action ? h("button.pp-btn.link", { type: "button", onclick: () => { remove(); action.run(); } }, action.label) : null,
    iconButton("x", t("common.close"), () => remove(), true));
  toastBox.appendChild(el);
  while (toastBox.children.length > 4) toastBox.firstElementChild!.remove();
  const timer = setTimeout(remove, tone === "danger" || tone === "warning" ? 8000 : 4000);
  function remove() {
    clearTimeout(timer);
    el.remove();
  }
}

// ---------------------------------------------------------------------------
// 悬停说明：带 data-tip 的元素悬停 500ms 显示（事件委托，全局一个）

export function installTooltips() {
  let tip: HTMLElement | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let target: HTMLElement | null = null;
  const hide = () => {
    clearTimeout(timer);
    tip?.remove();
    tip = null;
    target = null;
  };
  document.addEventListener("mouseover", (e) => {
    const el = (e.target as HTMLElement)?.closest?.("[data-tip]") as HTMLElement | null;
    if (el === target) return;
    hide();
    if (!el) return;
    target = el;
    timer = setTimeout(() => {
      const text = el.dataset.tip;
      if (!text || !el.isConnected) return;
      tip = h("div.pp-tooltip", null, text);
      document.body.appendChild(tip);
      const a = el.getBoundingClientRect();
      const r = tip.getBoundingClientRect();
      let y = a.bottom + 6;
      if (y + r.height > innerHeight - 4) y = a.top - r.height - 6;
      tip.style.left = clamp(a.left + a.width / 2 - r.width / 2, 4, innerWidth - r.width - 4) + "px";
      tip.style.top = y + "px";
    }, 500);
  });
  document.addEventListener("mousedown", hide, true);
  document.addEventListener("keydown", hide, true);
  window.addEventListener("blur", hide);
}

/** 弹出面板（设置等）：贴锚点，点外面 / Esc 关闭 */
export function popover(content: HTMLElement, anchor: HTMLElement, opts: { align?: "start" | "end"; above?: boolean } = {}): () => void {
  closeMenu();
  const el = h("div.pp-popover", { tabIndex: -1 }, content);
  document.body.appendChild(el);
  const a = anchor.getBoundingClientRect();
  const r = el.getBoundingClientRect();
  const x = opts.align === "end" ? a.right - r.width : a.left;
  const y = opts.above || a.bottom + r.height + 6 > innerHeight ? a.top - r.height - 6 : a.bottom + 6;
  el.style.left = clamp(x, 4, innerWidth - r.width - 4) + "px";
  el.style.top = clamp(y, 4, innerHeight - r.height - 4) + "px";
  const onDown = (e: MouseEvent) => {
    const tgt = e.target as Node;
    if (!el.contains(tgt) && !document.querySelector(".pp-menu")?.contains(tgt)) close();
  };
  const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !document.querySelector(".pp-menu")) { e.preventDefault(); close(); } };
  setTimeout(() => document.addEventListener("mousedown", onDown, true));
  document.addEventListener("keydown", onKey, true);
  function close() {
    if (!el.isConnected) return;
    el.remove();
    document.removeEventListener("mousedown", onDown, true);
    document.removeEventListener("keydown", onKey, true);
  }
  return close;
}

/** 自绘下拉（外观同输入框，弹出菜单选择） */
export function dropdown<T extends string | number>(options: { value: T; label: string }[], value: T, onChange: (v: T) => void): HTMLElement {
  const label = h("span.pp-dropdown-label");
  const btn = h("button.pp-dropdown", { type: "button" }, label, icon("chevrons-up-down", 14));
  const set = (v: T) => {
    value = v;
    label.textContent = options.find((o) => o.value === v)?.label ?? String(v);
  };
  set(value);
  btn.addEventListener("click", () => {
    showMenu(options.map((o) => ({ label: o.label, checked: o.value === value, run: () => { set(o.value); onChange(o.value); } })), btn, { minWidth: btn.offsetWidth });
  });
  return btn;
}

/** 开关 30×18 */
export function switchControl(on: boolean, onChange: (v: boolean) => void): HTMLElement {
  const el = h("button.pp-switch" + (on ? ".on" : ""), { type: "button", role: "switch", "aria-checked": String(on) }, h("span.knob"));
  el.addEventListener("click", () => {
    on = !on;
    el.classList.toggle("on", on);
    el.setAttribute("aria-checked", String(on));
    onChange(on);
  });
  return el;
}
