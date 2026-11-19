// 终端页（xterm.js）：原生界面「终端」分段里的终端区域，一个网页装下工作区的全部终端，同一时间显示一个。
// 宿主用 WKWebView / WebView2 加载 /editor/terminal.html#token=…&theme=dark，
// 通过 window.paperplaneTerm 控制，网页经 messageHandlers.paperplane 回传（docs/editor.md「终端页」）：
//   宿主 → 网页：show(id) sync(ids) setTheme(t) focus() size() → {cols, rows}
//   网页 → 宿主：{type:"ready"} {type:"title", id, title} {type:"closed", id} {type:"openExternal", url} {type:"focus", value}
// 输出：GET /api/v1/terminals/<id>/stream?since=<偏移>（SSE）。只给正在显示的终端保持连接（WebKit 对同一主机最多 6 个连接），
// 切回来时按已收到的偏移续传，中间的输出从内核缓冲补齐。
// 输入：POST terminal.input，按顺序一个一个发，前一个没回来时把后面的合并。
import "@xterm/xterm/css/xterm.css";
import "./gen/tokens.css";
import { Terminal, type ITheme } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { Unicode11Addon } from "@xterm/addon-unicode11";
import { WebglAddon } from "@xterm/addon-webgl";
import { ansi, colors, fonts } from "./gen/tokens";
import { fontStack, isMac, isWindows, platform } from "./util";

type Msg = Record<string, unknown> & { type: string };

function post(m: Msg) {
  const w = window as any;
  const target = w.webkit?.messageHandlers?.paperplane ?? w.chrome?.webview;
  if (target) {
    try { target.postMessage(m); } catch { /* 宿主已释放 */ }
  } else if (m.type === "openExternal") {
    window.open(String(m.url), "_blank", "noopener");
  }
}

/** 复制到剪贴板（Windows 上 Ctrl+C 复制选中的终端文字） */
function copyText(text: string) {
  navigator.clipboard?.writeText(text).catch(() => {
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.style.position = "fixed";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.select();
    document.execCommand("copy");
    ta.remove();
    current?.term.focus();
  });
}

const params = new URLSearchParams(location.hash.slice(1));
const token = params.get("token") ?? "";
let scheme: "light" | "dark" = params.get("theme") === "dark" ? "dark" : "light";
document.documentElement.dataset.theme = scheme;
document.documentElement.dataset.platform = platform;
const fontFamily = fontStack((fonts.mono as any)[platform] ?? "", "monospace");
const fontSize = Number(params.get("fontSize")) || 12;

function themeOf(s: "light" | "dark"): ITheme {
  const c = colors[s];
  const a = ansi[s];
  return {
    background: c.consoleBg, foreground: c.consoleText,
    cursor: c.textPrimary, cursorAccent: c.consoleBg,
    selectionBackground: c.consoleSelection, selectionInactiveBackground: c.bgSelectedInactive,
    scrollbarSliderBackground: c.scrollbar, scrollbarSliderHoverBackground: c.scrollbar, scrollbarSliderActiveBackground: c.scrollbar,
    black: a[0], red: a[1], green: a[2], yellow: a[3], blue: a[4], magenta: a[5], cyan: a[6], white: a[7],
    brightBlack: a[8], brightRed: a[9], brightGreen: a[10], brightYellow: a[11],
    brightBlue: a[12], brightMagenta: a[13], brightCyan: a[14], brightWhite: a[15],
  };
}

function applyPageTheme() {
  document.documentElement.dataset.theme = scheme;
  document.body.style.background = colors[scheme].consoleBg;
}

async function call(op: string, args: Record<string, unknown>): Promise<any> {
  const res = await fetch(`/api/v1/call/${op}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify(args),
  });
  const body = await res.json().catch(() => null);
  if (!res.ok || body?.error) throw new Error(body?.error?.message || res.statusText);
  return body?.result;
}

const host = document.getElementById("host")!;

function b64(s: string): Uint8Array {
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

class Term {
  readonly el = document.createElement("div");
  readonly term: Terminal;
  readonly fit = new FitAddon();
  private webgl: WebglAddon | null = null;
  private es: EventSource | null = null;
  private offset = 0; // 已写进 xterm 的输出末尾偏移
  private retry = 0;
  private retryTimer = 0;
  private pending = ""; // 待发送的输入
  private sending = false;
  private resizeTimer = 0;
  private sentSize = "";
  attached = false;
  disposed = false;

  constructor(readonly id: string) {
    this.el.className = "pp-term";
    host.appendChild(this.el);
    this.term = new Terminal({
      fontFamily, fontSize, lineHeight: 1.2, letterSpacing: 0,
      cursorBlink: true, cursorStyle: "bar", cursorInactiveStyle: "outline",
      scrollback: 10000, allowProposedApi: true, macOptionIsMeta: false, macOptionClickForcesSelection: true,
      rightClickSelectsWord: isMac, drawBoldTextInBrightColors: false, minimumContrastRatio: 1,
      theme: themeOf(scheme),
    });
    this.term.loadAddon(this.fit);
    this.term.loadAddon(new WebLinksAddon((ev, uri) => {
      // 和系统终端一样：⌘ 点击才打开（Windows 上和 Windows Terminal 一样 Ctrl 点击），普通点击留给选择文字
      if (isMac ? ev.metaKey : isWindows ? ev.ctrlKey : true) post({ type: "openExternal", url: uri });
    }));
    const u = new Unicode11Addon();
    this.term.loadAddon(u);
    this.term.unicode.activeVersion = "11"; // 中文、emoji 按两格宽
    this.term.open(this.el);
    this.term.onData((d) => this.input(d));
    this.term.onBinary((d) => this.input(d));
    this.term.onTitleChange((title) => post({ type: "title", id: this.id, title }));
    this.term.onResize(() => this.scheduleResize());
    this.term.attachCustomKeyEventHandler((e) => this.keys(e));
    this.term.textarea?.addEventListener("focus", () => post({ type: "focus", value: true }));
    this.term.textarea?.addEventListener("blur", () => post({ type: "focus", value: false }));
  }

  /**
   * macOS 上和系统终端 / VS Code 一致的快捷键：⌘K 清屏，⌘←/→ 行首行尾，⌥←/→ 按词移动，⌘⌫ 删到行首，⌥⌫ 删词。
   * Windows 上和 Windows Terminal 一致：有选中文字时 Ctrl+C 复制（没有时照常发 ^C 中断），Ctrl+V 粘贴，
   * Ctrl+Shift+C / Ctrl+Shift+V 复制 / 粘贴，Ctrl+Shift+K 清屏。
   */
  private keys(e: KeyboardEvent): boolean {
    if (e.type !== "keydown") return true;
    const mod = isMac ? e.metaKey : e.ctrlKey && e.shiftKey;
    if (mod && e.key.toLowerCase() === "k") { this.term.clear(); return false; }
    if (isWindows && e.ctrlKey && !e.altKey && !e.metaKey) {
      const k = e.key.toLowerCase();
      if (k === "c" && (e.shiftKey || this.term.hasSelection())) {
        e.preventDefault();
        const text = this.term.getSelection();
        if (text) copyText(text);
        if (!e.shiftKey) this.term.clearSelection();
        return false;
      }
      // 不让 xterm 把 Ctrl+V 当成 ^V 发出去：交给浏览器的粘贴事件（xterm 自己处理粘贴，含括号粘贴模式）
      if (k === "v") return false;
    }
    if (!isMac) return true;
    const seq = (s: string) => { e.preventDefault(); this.input(s); return false; };
    if (e.metaKey && !e.altKey && !e.ctrlKey) {
      if (e.key === "ArrowLeft") return seq("\x01");
      if (e.key === "ArrowRight") return seq("\x05");
      if (e.key === "Backspace") return seq("\x15");
    }
    if (e.altKey && !e.metaKey && !e.ctrlKey) {
      if (e.key === "ArrowLeft") return seq("\x1bb");
      if (e.key === "ArrowRight") return seq("\x1bf");
      if (e.key === "Backspace") return seq("\x17");
    }
    return true;
  }

  show() {
    for (const t of terms.values()) if (t !== this) t.hide();
    this.el.classList.add("on");
    if (!this.webgl) this.loadWebgl();
    this.fitTries = 0;
    this.fitNow();
    this.attach();
    this.term.focus();
  }

  hide() {
    if (!this.el.classList.contains("on")) return;
    this.el.classList.remove("on");
    this.detach();
  }

  private loadWebgl() {
    try {
      const w = new WebglAddon();
      w.onContextLoss(() => { w.dispose(); this.webgl = null; });
      this.term.loadAddon(w);
      this.webgl = w;
    } catch {
      this.webgl = null; // 没有 WebGL 时用默认的 DOM 渲染
    }
  }

  fitNow() {
    if (!this.el.classList.contains("on") || host.clientWidth < 40 || host.clientHeight < 20) return;
    // 字体还没测好时算出来的行列数不可信（会让 shell 按两三列重画提示符），等下一次
    const d = this.fit.proposeDimensions();
    if (!d || d.cols < 10 || d.rows < 3) {
      requestAnimationFrame(() => this.fitLater());
      return;
    }
    if (d.cols !== this.term.cols || d.rows !== this.term.rows) this.term.resize(d.cols, d.rows);
    this.scheduleResize();
  }

  private fitTries = 0;
  private fitLater() {
    if (this.disposed || ++this.fitTries > 30) return;
    this.fitNow();
  }

  private scheduleResize() {
    clearTimeout(this.resizeTimer);
    this.resizeTimer = window.setTimeout(() => {
      const key = `${this.term.cols}x${this.term.rows}`;
      if (key === this.sentSize || this.disposed || !this.el.classList.contains("on")) return;
      this.sentSize = key;
      call("terminal.resize", { id: this.id, cols: this.term.cols, rows: this.term.rows }).catch(() => { this.sentSize = ""; });
    }, 60);
  }

  private attach() {
    if (this.attached || this.disposed) return;
    this.attached = true;
    this.connect();
  }

  private detach() {
    this.attached = false;
    clearTimeout(this.retryTimer);
    this.es?.close();
    this.es = null;
  }

  private connect() {
    if (!this.attached || this.disposed) return;
    const url = `/api/v1/terminals/${encodeURIComponent(this.id)}/stream?since=${this.offset}&token=${encodeURIComponent(token)}`;
    const es = new EventSource(url);
    this.es = es;
    es.onopen = () => { this.retry = 0; };
    es.onmessage = (ev) => {
      const m = JSON.parse(ev.data) as { o: number; d: string };
      let data = b64(m.d);
      const end = m.o + data.length;
      if (end <= this.offset) return; // 已经写过
      if (m.o < this.offset) data = data.subarray(this.offset - m.o);
      this.offset = end;
      this.term.write(data);
    };
    es.addEventListener("closed", () => {
      es.close();
      this.dispose();
      post({ type: "closed", id: this.id });
    });
    es.onerror = () => {
      es.close();
      if (this.es === es) this.es = null;
      if (!this.attached || this.disposed) return;
      // 断开（daemon 重启、跟不上被断开）：确认终端还在再按偏移续传
      this.retry++;
      this.retryTimer = window.setTimeout(async () => {
        try {
          const list: { id: string }[] = await call("terminal.list", {});
          if (!list.some((t) => t.id === this.id)) {
            this.dispose();
            post({ type: "closed", id: this.id });
            return;
          }
        } catch { /* daemon 暂时连不上，继续重试 */ }
        this.connect();
      }, Math.min(3000, 200 * this.retry));
    };
  }

  private input(d: string) {
    if (this.disposed) return;
    this.pending += d;
    this.flushInput();
  }

  private async flushInput() {
    if (this.sending || !this.pending) return;
    this.sending = true;
    const data = this.pending;
    this.pending = "";
    try {
      await call("terminal.input", { id: this.id, data });
    } catch {
      /* 终端已退出或关闭：输入丢弃 */
    }
    this.sending = false;
    if (this.pending) this.flushInput();
  }

  setTheme(s: "light" | "dark") { this.term.options.theme = themeOf(s); }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    if (current === this) current = null;
    this.detach();
    clearTimeout(this.resizeTimer);
    this.term.dispose();
    this.el.remove();
    terms.delete(this.id);
  }
}

const terms = new Map<string, Term>();
let current: Term | null = null;

new ResizeObserver(() => current?.fitNow()).observe(host);
// 等宽字体加载完字宽会变，重新算一次行列
document.fonts?.ready.then(() => current?.fitNow());

(window as any).paperplaneTerm = {
  /** 显示（必要时创建）某个终端 */
  show(id: string) {
    let t = terms.get(id);
    if (!t) { t = new Term(id); terms.set(id, t); }
    current = t;
    t.show();
  },
  /** 宿主的终端列表变了：关掉不在列表里的 */
  sync(ids: string[]) {
    const keep = new Set(ids);
    for (const t of [...terms.values()]) if (!keep.has(t.id)) { if (current === t) current = null; t.dispose(); }
  },
  setTheme(t: "light" | "dark") {
    scheme = t;
    applyPageTheme();
    for (const x of terms.values()) x.setTheme(t);
  },
  focus() { current?.term.focus(); },
  /** 当前区域能放下的行列数（新建终端时按它启动 shell，避免一启动就改尺寸） */
  size() {
    const probe = current ?? null;
    if (probe) {
      const d = probe.fit.proposeDimensions();
      if (d && d.cols >= 10 && d.rows >= 3) return { cols: d.cols, rows: d.rows };
    }
    // 还没有终端时按字号估算
    const cw = fontSize * 0.6, ch = Math.round(fontSize * 1.2);
    return { cols: Math.max(20, Math.floor(host.clientWidth / cw)), rows: Math.max(5, Math.floor(host.clientHeight / ch)) };
  },
};

applyPageTheme();
post({ type: "ready" });
