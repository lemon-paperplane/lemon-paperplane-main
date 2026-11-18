// 语言着色：按文件名匹配插件贡献的 languages，按需 import() 插件里的语言模块（docs/editor.md 第 4 节）。
import { EditorState, type Extension } from "@codemirror/state";
import { Language, LanguageDescription, LanguageSupport, StreamLanguage, foldService, language } from "@codemirror/language";
import type { LanguageInfo } from "./api";
import { pluginFileUrl } from "./api";
import { cm, ensureShared } from "./cm";
import { basename, globToRegExp } from "./util";

let defs: LanguageInfo[] = [];
const patternCache = new Map<string, RegExp>();
const loaded = new Map<string, Promise<LanguageSupport | Extension>>();

export function setLanguages(list: LanguageInfo[]) {
  defs = list ?? [];
  // 给 Markdown 等嵌套语言用：按语言名/扩展名找其他插件的语言
  cm.paperplane.codeLanguages = defs.map((d) => LanguageDescription.of({
    name: d.name,
    alias: [d.id, ...(d.extensions ?? []).map((e) => e.replace(/^\./, ""))],
    extensions: (d.extensions ?? []).map((e) => e.replace(/^\./, "")),
    filename: d.filenames?.length ? new RegExp("^(" + d.filenames.map((f) => f.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|") + ")$") : undefined,
    load: async () => {
      const r = await loadLanguage(d);
      if (r instanceof LanguageSupport) return r;
      if (r instanceof Language) return new LanguageSupport(r);
      throw new Error("not a language");
    },
  }));
}

export function allLanguages(): LanguageInfo[] {
  return defs;
}

export function languageById(id: string): LanguageInfo | null {
  return defs.find((d) => d.id === id) ?? null;
}

/** 文件名精确匹配 > 文件名通配 > 扩展名（取最长）；同级取 priority 高的 */
export function resolveLanguage(path: string): LanguageInfo | null {
  const name = basename(path);
  const lower = name.toLowerCase();
  let best: LanguageInfo | null = null;
  let bestScore = -Infinity;
  for (const d of defs) {
    let rank = 0;
    if (d.filenames?.some((f) => f === name || f.toLowerCase() === lower)) rank = 3000;
    else if (d.filenamePatterns?.some((p) => patternOf(p).test(p.includes("/") ? path : name))) rank = 2000;
    else {
      for (const e of d.extensions ?? []) {
        const el = e.toLowerCase();
        if (lower.endsWith(el) && lower.length > el.length) rank = Math.max(rank, 1000 + el.length);
      }
    }
    if (!rank) continue;
    const score = rank * 1000 + (d.priority ?? 0);
    if (score > bestScore) { best = d; bestScore = score; }
  }
  return best;
}

function patternOf(p: string): RegExp {
  let re = patternCache.get(p);
  if (!re) patternCache.set(p, (re = globToRegExp(p)));
  return re;
}

/** 加载语言模块：export default function (cm) { return LanguageSupport | Language | Extension } */
export function loadLanguage(d: LanguageInfo): Promise<LanguageSupport | Extension> {
  let p = loaded.get(d.module);
  if (!p) {
    p = (async () => {
      await ensureShared();
      const mod = await importModule(d.module);
      const fn = mod.default ?? mod;
      if (typeof fn !== "function") throw new Error(`${d.module}: default export is not a function`);
      return await fn(cm);
    })();
    loaded.set(d.module, p);
    p.catch(() => loaded.delete(d.module));
  }
  return p;
}

/** import() 插件模块；内核 MIME 不对等原因失败时退回 fetch + Blob */
export async function importModule(ref: string): Promise<any> {
  const url = pluginFileUrl(ref);
  try {
    return await import(/* @vite-ignore */ url);
  } catch (e) {
    const res = await fetch(url);
    if (!res.ok) throw e;
    const blob = URL.createObjectURL(new Blob([await res.text()], { type: "text/javascript" }));
    try { return await import(/* @vite-ignore */ blob); } finally { URL.revokeObjectURL(blob); }
  }
}

/** 插件声明的注释符号作为语言数据的兜底（模块自己声明的优先） */
export function commentData(d: LanguageInfo | null): Extension {
  if (!d || (!d.lineComment && !d.blockComment?.[0])) return [];
  const commentTokens: any = {};
  if (d.lineComment) commentTokens.line = d.lineComment;
  if (d.blockComment?.[0]) commentTokens.block = { open: d.blockComment[0], close: d.blockComment[1] };
  return EditorState.languageData.of(() => [{ commentTokens }]);
}

/** 没有语法树折叠信息的语言（StreamLanguage、纯文本）按缩进折叠 */
export const indentFolding = foldService.of((state, from, to) => {
  const lang = state.facet(language);
  if (lang && !(lang instanceof StreamLanguage)) return null;
  const line = state.doc.lineAt(from);
  if (!line.text.trim()) return null;
  const ind = indentOf(line.text, state.tabSize);
  let end = -1;
  for (let n = line.number + 1; n <= state.doc.lines; n++) {
    const l = state.doc.line(n);
    if (!l.text.trim()) continue;
    if (indentOf(l.text, state.tabSize) <= ind) break;
    end = l.to;
  }
  return end > to ? { from: to, to: end } : null;
});

function indentOf(s: string, tab: number): number {
  let n = 0;
  for (const c of s) {
    if (c === " ") n++;
    else if (c === "\t") n += tab - (n % tab);
    else break;
  }
  return n;
}
