// 小工具：DOM 构造、平台判断、路径处理、防抖。

type Attrs = Record<string, any> | null | undefined;
type Child = Node | string | number | null | undefined | false | Child[];

type TagOf<S extends string> = S extends `${infer T}.${string}` ? T : S;
type ElOf<S extends string> = TagOf<S> extends keyof HTMLElementTagNameMap ? HTMLElementTagNameMap[TagOf<S>] : HTMLElement;

/** h 创建元素：h("div.cls1.cls2", {onclick, title, style}, ...children) */
export function h<S extends string>(sel: S, attrs?: Attrs, ...children: Child[]): ElOf<S> {
  const [tag, ...classes] = sel.split(".");
  const el = document.createElement(tag || "div") as any;
  if (classes.length) el.className = classes.join(" ");
  if (attrs) {
    for (const k in attrs) {
      const v = attrs[k];
      if (v == null || v === false) continue;
      if (k.startsWith("on")) el.addEventListener(k.slice(2), v);
      else if (k === "style" && typeof v === "object") Object.assign(el.style, v);
      else if (k === "class") el.className += (el.className ? " " : "") + v;
      else if (k === "dataset") Object.assign(el.dataset, v);
      else if (k in el && k !== "list" && k !== "type") el[k] = v;
      else el.setAttribute(k, v === true ? "" : String(v));
    }
  }
  append(el, children);
  return el;
}

function append(el: Node, children: Child[]) {
  for (const c of children) {
    if (c == null || c === false) continue;
    if (Array.isArray(c)) append(el, c);
    else el.appendChild(typeof c === "object" ? c : document.createTextNode(String(c)));
  }
}

export const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
export const isWindows = /Win/.test(navigator.platform || navigator.userAgent);
export const platform: "macos" | "windows" | "linux" = isMac ? "macos" : isWindows ? "windows" : "linux";

export function basename(p: string): string {
  const i = p.lastIndexOf("/");
  return i < 0 ? p : p.slice(i + 1);
}

export function dirname(p: string): string {
  const i = p.lastIndexOf("/");
  return i < 0 ? "" : p.slice(0, i);
}

export function joinPath(dir: string, name: string): string {
  return dir ? dir + "/" + name : name;
}

export function extname(p: string): string {
  const b = basename(p);
  const i = b.lastIndexOf(".");
  return i <= 0 ? "" : b.slice(i).toLowerCase();
}

export function debounce<T extends (...a: any[]) => void>(fn: T, ms: number): T & { cancel(): void } {
  let t: ReturnType<typeof setTimeout> | undefined;
  const f = ((...a: any[]) => {
    if (t) clearTimeout(t);
    t = setTimeout(() => { t = undefined; fn(...a); }, ms);
  }) as T & { cancel(): void };
  f.cancel = () => { if (t) clearTimeout(t); t = undefined; };
  return f;
}

/** 把 glob（* ? ** [abc]）转成正则 */
export function globToRegExp(glob: string): RegExp {
  let re = "";
  for (let i = 0; i < glob.length; i++) {
    const c = glob[i];
    if (c === "*") {
      if (glob[i + 1] === "*") { re += ".*"; i++; if (glob[i + 1] === "/") i++; }
      else re += "[^/]*";
    } else if (c === "?") re += "[^/]";
    else if (c === "[") {
      const j = glob.indexOf("]", i);
      if (j < 0) re += "\\[";
      else { re += "[" + glob.slice(i + 1, j).replace(/^!/, "^") + "]"; i = j; }
    } else re += c.replace(/[.+^${}()|\\]/g, "\\$&");
  }
  return new RegExp("^" + re + "$", "i");
}

export function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export function clamp(v: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, v));
}

/** 读写 localStorage（WebView 可能用非持久存储，失败就忽略） */
export const store = {
  get<T>(key: string, def: T): T {
    try {
      const v = localStorage.getItem("pp-editor." + key);
      return v == null ? def : (JSON.parse(v) as T);
    } catch { return def; }
  },
  set(key: string, v: unknown) {
    try { localStorage.setItem("pp-editor." + key, JSON.stringify(v)); } catch { /* 忽略 */ }
  },
};

/** 等宽/界面字体（按平台取 design-tokens 的字体族） */
export function fontStack(list: string, generic: string): string {
  return list.split(",").map((s) => s.trim()).filter((s) => s && s !== "system")
    .map((s) => (/\s/.test(s) ? `"${s}"` : s)).concat(generic).join(", ");
}

/** 推断缩进：制表符行多则用制表符；否则取相邻行缩进差里出现最多的宽度 */
export function guessIndent(text: string, defaultSize: number): { spaces: boolean; size: number } | null {
  let tabs = 0, spaces = 0, prev = 0, n = 0;
  const widths = new Map<number, number>();
  for (const line of text.split("\n", 2000)) {
    if (!line.trim()) continue;
    if (line[0] === "\t") { tabs++; continue; }
    const w = /^ */.exec(line)![0].length;
    if (w) spaces++;
    const d = Math.abs(w - prev);
    if (d > 1 && d <= 8) widths.set(d, (widths.get(d) ?? 0) + 1);
    prev = w;
    if (++n > 1000) break;
  }
  if (!tabs && !spaces) return null;
  if (tabs > spaces) return { spaces: false, size: defaultSize };
  let best = 0, size = defaultSize;
  for (const [w, c] of widths) if (c > best || (c === best && w < size)) { best = c; size = w; }
  return { spaces: true, size };
}
