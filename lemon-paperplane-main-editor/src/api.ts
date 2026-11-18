// 内核 API：同源 POST /api/v1/call/<op>，Authorization 用地址里的 token（docs/editor.md 第 5 节）。

export class ApiError extends Error {
  constructor(public code: string, message: string, public data?: any) {
    super(message);
  }
  get reason(): string | undefined {
    return this.data?.reason;
  }
}

let token = "";

export function setToken(t: string) {
  token = t;
}

export async function call<T = any>(op: string, args: Record<string, unknown> = {}): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`/api/v1/call/${op}`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify(args),
    });
  } catch (e: any) {
    throw new ApiError("network", e?.message || String(e));
  }
  let body: any = null;
  try { body = await res.json(); } catch { /* 非 JSON */ }
  if (!res.ok || body?.error) {
    const err = body?.error || {};
    throw new ApiError(err.code || "http_" + res.status, err.message || res.statusText, err.data);
  }
  return body?.result as T;
}

/** 插件文件引用 plugin:<id>/<path> → 同源地址（带 token，供 import()/fetch 使用） */
export function pluginFileUrl(ref: string): string {
  const m = /^plugin:([^/]+)\/(.+)$/.exec(ref);
  if (!m) return ref;
  const p = m[2].split("/").map(encodeURIComponent).join("/");
  return `/api/v1/plugins/${encodeURIComponent(m[1])}/files/${p}?token=${encodeURIComponent(token)}`;
}

export async function fetchJSON<T = any>(ref: string): Promise<T> {
  const res = await fetch(pluginFileUrl(ref), { headers: { Authorization: `Bearer ${token}` } });
  if (!res.ok) throw new ApiError("http_" + res.status, `${ref}: ${res.status}`);
  return res.json();
}

// ---------------------------------------------------------------------------
// 内核数据类型（与 lemon-paperplane-main-core/internal/engine/fs.go、model.go 对应）

export interface FSEntry { name: string; path: string; dir: boolean; size: number; mtime: string; symlink?: boolean; hidden?: boolean; ignored?: boolean }
export interface FSFile { path: string; content: string; encoding: string; bom?: boolean; lineEnding: "lf" | "crlf" | "mixed"; preferredLineEnding?: "lf" | "crlf"; size: number; mtime: string; binary?: boolean; tooLarge?: boolean; readOnly?: boolean; image?: boolean; dataUrl?: string }
export interface FSStat { path: string; exists: boolean; dir?: boolean; size?: number; mtime?: string }
export interface FSMatch { path: string; line: number; column: number; text: string }

export interface LanguageInfo {
  id: string; name: string; extensions?: string[]; filenames?: string[]; filenamePatterns?: string[];
  module: string; lineComment?: string; blockComment?: [string, string]; icon?: string; priority?: number; plugin: string;
}
export interface KeymapInfo { id: string; name: string; platform?: string; file?: string; module?: string; base?: string; plugin: string }
export interface EditorThemeInfo { id: string; name: string; kind: "light" | "dark"; file: string; plugin: string }
export interface Contributions { languages: LanguageInfo[]; keymaps: KeymapInfo[]; editorThemes: EditorThemeInfo[]; defaultKeymap: string }

export interface EditorSettings {
  keymap: string; theme: string; fontSize: number; fontFamily?: string; tabSize: number; insertSpaces: boolean;
  wordWrap: boolean; autoSave: "off" | "onFocusChange" | "afterDelay"; trimTrailingWhitespace: boolean; insertFinalNewline: boolean;
  showWhitespace: boolean;
}

export interface VCSRoot { path: string; relPath: string; vcs: string; plugin: string }
export interface Project { id: string; name: string; path: string; vcsRoots?: VCSRoot[] }
export interface VCSDiffLine { kind: " " | "+" | "-"; old?: number; new?: number; text: string }
export interface VCSDiff { path: string; binary?: boolean; tooLarge?: boolean; hunks: { oldStart: number; oldLines: number; newStart: number; newLines: number; lines: VCSDiffLine[] }[] }
