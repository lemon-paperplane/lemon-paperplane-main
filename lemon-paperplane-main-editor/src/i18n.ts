// 文案：dist/i18n/<locale>.json（构建时从 shared/i18n 摘出 editor.* 与少量 common.*）。

let dict: Record<string, string> = {};
let current = "en";
const listeners: (() => void)[] = [];

export function normalizeLocale(l?: string | null): string {
  if (!l || l === "system") l = navigator.language || "en";
  return /^zh/i.test(l) ? "zh-Hans" : "en";
}

export async function loadLocale(l?: string | null): Promise<void> {
  const loc = normalizeLocale(l);
  try {
    const res = await fetch(`i18n/${loc}.json`);
    if (res.ok) {
      dict = await res.json();
      current = loc;
    }
  } catch { /* 保留原文案 */ }
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
