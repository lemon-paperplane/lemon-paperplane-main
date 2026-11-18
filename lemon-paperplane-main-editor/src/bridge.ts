// 与宿主窗口的桥接（docs/editor.md 第 5 节）。
// 网页 → 宿主：Mac/Linux WebKit 用 webkit.messageHandlers.paperplane，Windows WebView2 用 chrome.webview；
// 都没有时（浏览器里直接打开调试）降级为普通网页。

export type HostMessage =
  | { type: "ready" }
  | { type: "title"; title: string }
  | { type: "dirty"; dirty: boolean }
  | { type: "closeWindow" }
  | { type: "closeCancelled" }
  | { type: "reveal"; path: string }
  | { type: "openExternal"; url: string };

const w = globalThis as any;

function hostTarget(): { postMessage(m: unknown): void } | null {
  return w.webkit?.messageHandlers?.paperplane ?? w.chrome?.webview ?? null;
}

export const hasHost = hostTarget() != null;

export function post(msg: HostMessage) {
  const target = hostTarget();
  if (target) {
    try { target.postMessage(msg); } catch (e) { console.warn("[paperplane] postMessage", e); }
    return;
  }
  // 没有宿主：尽量用网页自己的能力
  switch (msg.type) {
    case "title": document.title = msg.title; break;
    case "closeWindow": window.close(); break;
    case "openExternal": window.open(msg.url, "_blank", "noopener"); break;
    default: break;
  }
}

/** 宿主调用的入口：window.paperplane.openFile(...) 等 */
export interface HostApi {
  openFile(path: string, line?: number): void;
  setTheme(theme: "light" | "dark"): void;
  setLocale(locale: string): void;
  requestClose(): void;
  saveAll(): void;
}

export function exposeHostApi(api: HostApi) {
  w.paperplane = api;
}
