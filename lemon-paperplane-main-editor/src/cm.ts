// 共享 CodeMirror 命名空间：window.cm。
// 语言/快捷键模块（插件里的 editor/*.js）不自带 @codemirror/* 与 @lezer/* 的运行时，
// 由 scripts/build-lang.mjs 把这些 import 改写为读取 window.cm 上的对应命名空间，保证单实例。
import * as state from "@codemirror/state";
import * as view from "@codemirror/view";
import * as language from "@codemirror/language";
import * as commands from "@codemirror/commands";
import * as search from "@codemirror/search";
import * as autocomplete from "@codemirror/autocomplete";
import * as common from "@lezer/common";
import * as highlight from "@lezer/highlight";

export interface SharedCM {
  state: typeof state;
  view: typeof view;
  language: typeof language;
  commands: typeof commands;
  search: typeof search;
  autocomplete: typeof autocomplete;
  lezer: { common: typeof common; highlight: typeof highlight; lr: any };
  /** StreamLanguage 与 CodeMirror 5 风格 legacy-modes 的工具 */
  legacy: { StreamLanguage: typeof language.StreamLanguage; StringStream: typeof language.StringStream };
  /** 编辑器提供给模块的辅助：codeLanguages 供 Markdown 等嵌套语言按需加载其他插件的语言 */
  paperplane: { codeLanguages: language.LanguageDescription[]; version: string };
}

export const cm: SharedCM = {
  state, view, language, commands, search, autocomplete,
  lezer: { common, highlight, lr: undefined },
  legacy: { StreamLanguage: language.StreamLanguage, StringStream: language.StringStream },
  paperplane: { codeLanguages: [], version: "1" },
};

// 浏览器里 globalThis 就是 window
(globalThis as any).cm = cm;

let lrLoading: Promise<void> | null = null;

/** @lezer/lr 只有语言模块用到，首次加载语言模块前再取（不占首屏） */
export function ensureShared(): Promise<void> {
  if (cm.lezer.lr) return Promise.resolve();
  return (lrLoading ??= import("@lezer/lr").then((m) => { cm.lezer.lr = m; }));
}
