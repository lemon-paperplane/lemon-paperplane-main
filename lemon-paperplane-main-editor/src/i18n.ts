// 文案：dist/i18n/<locale>.json（构建时从 shared/i18n 摘出 editor.* 与少量 common.*）。
// Windows 上再叠加 dist/i18n/windows/<locale>.json（废纸篓 → 回收站这类平台说法），Mac/Linux 不读。
import { isWindows } from "./util";

let dict: Record<string, string> = {};
let current = "en";
const listeners: (() => void)[] = [];

export function normalizeLocale(l?: string | null): string {
  if (!l || l === "system") l = navigator.language || "en";
  return /^zh/i.test(l) ? "zh-Hans" : "en";
}

async function fetchDict(url: string): Promise<Record<string, string> | null> {
  try {
    const res = await fetch(url);
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

export async function loadLocale(l?: string | null): Promise<void> {
  const loc = normalizeLocale(l);
  const [base, overlay] = await Promise.all([
    fetchDict(`i18n/${loc}.json`),
    isWindows ? fetchDict(`i18n/windows/${loc}.json`) : Promise.resolve(null),
  ]);
  if (base) {
    dict = overlay ? { ...base, ...overlay } : base;
    current = loc;
  }
  document.documentElement.lang = current;
  for (const f of listeners) f();
}

export function locale(): string {
  return current;
}

/** t("editor.x", a, b) → 替换 {0} {1} */
export function t(key: string, ...args: (string | number)[]): string {
  const s = dict[key] ?? key;
  return args.length ? s.replace(/\{(\d+)\}/g, (_, i) => String(args[+i] ?? "")) : s;
}

export function onLocaleChange(f: () => void) {
  listeners.push(f);
}
