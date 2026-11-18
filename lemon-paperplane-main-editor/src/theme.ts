// 配色：内置 Paperplane Light / Dark（CSS 变量，随 data-theme 即时切换，颜色来自 design-tokens.json），
// 以及插件 editorThemes 的 JSON 主题（docs/editor.md 第 4 节）。
import { EditorView } from "@codemirror/view";
import type { Extension } from "@codemirror/state";
import { HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import { tags, Tag } from "@lezer/highlight";

const v = (k: string) => `var(--syn-${k})`;

/** Paperplane 内置着色：颜色是 CSS 变量，明暗切换不需要重建状态 */
export const paperplaneHighlight = HighlightStyle.define([
  { tag: [tags.keyword, tags.modifier, tags.controlKeyword, tags.operatorKeyword, tags.definitionKeyword, tags.moduleKeyword, tags.self, tags.null, tags.bool, tags.atom], color: v("keyword") },
  { tag: [tags.string, tags.special(tags.string), tags.character, tags.attributeValue, tags.docString], color: v("string") },
  { tag: [tags.number, tags.integer, tags.float, tags.unit], color: v("number") },
  { tag: [tags.comment, tags.lineComment, tags.blockComment], color: v("comment"), fontStyle: "italic" },
  { tag: tags.docComment, color: v("comment"), fontStyle: "italic" },
  { tag: [tags.function(tags.variableName), tags.function(tags.definition(tags.variableName)), tags.function(tags.propertyName)], color: v("function") },
  { tag: [tags.propertyName, tags.definition(tags.propertyName)], color: v("property") },
  { tag: [tags.typeName, tags.className, tags.namespace], color: v("type") },
  { tag: [tags.annotation, tags.meta, tags.macroName, tags.processingInstruction, tags.labelName], color: v("meta") },
  { tag: [tags.tagName, tags.angleBracket], color: v("tag") },
  { tag: tags.attributeName, color: v("attribute") },
  { tag: tags.regexp, color: v("regexp") },
  { tag: [tags.escape, tags.special(tags.variableName)], color: v("escape") },
  { tag: [tags.constant(tags.variableName), tags.standard(tags.variableName)], color: v("property"), fontStyle: "italic" },
  { tag: [tags.heading, tags.heading1, tags.heading2, tags.heading3], color: v("heading"), fontWeight: "600" },
  { tag: [tags.link, tags.url], color: v("link"), textDecoration: "underline" },
  { tag: tags.emphasis, fontStyle: "italic" },
  { tag: tags.strong, fontWeight: "600" },
  { tag: tags.strikethrough, textDecoration: "line-through" },
  { tag: [tags.punctuation, tags.separator, tags.bracket, tags.derefOperator], color: v("punctuation") },
  { tag: tags.inserted, color: v("inserted") },
  { tag: tags.deleted, color: v("deleted") },
  { tag: tags.changed, color: v("changed") },
  { tag: tags.invalid, color: v("invalid") },
  { tag: tags.contentSeparator, color: v("comment") },
]);

const c = (k: string) => `var(--c-${k})`;

/** Paperplane 编辑区外观（覆盖 CodeMirror 基础主题；颜色随 data-theme 的 CSS 变量变化） */
function paperplaneEditorTheme(dark: boolean): Extension {
  return EditorView.theme({
    "&": { backgroundColor: c("bgContent"), color: "var(--syn-text)", height: "100%" },
    "&.cm-focused": { outline: "none" },
    ".cm-scroller": { fontFamily: "var(--font-mono)", userSelect: "text", WebkitUserSelect: "text" },
    ".cm-content": { caretColor: c("textPrimary"), padding: "4px 0 40vh" },
    ".cm-line": { padding: "0 12px 0 6px" },
    ".cm-gutters": { backgroundColor: c("bgContent"), color: c("textTertiary"), border: "none", userSelect: "none", WebkitUserSelect: "none" },
    ".cm-lineNumbers .cm-gutterElement": { padding: "0 6px 0 14px", minWidth: "32px" },
    ".cm-activeLineGutter": { backgroundColor: "transparent", color: c("textPrimary") },
    ".cm-activeLine": { backgroundColor: "color-mix(in srgb, var(--c-accent) 6%, transparent)" },
    ".cm-cursor, .cm-dropCursor": { borderLeft: `2px solid ${c("textPrimary")}` },
    ".cm-selectionBackground": { backgroundColor: c("bgSelectedInactive") },
    "&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, .cm-content ::selection": { backgroundColor: c("consoleSelection") },
    ".cm-matchingBracket, &.cm-focused .cm-matchingBracket": { backgroundColor: c("accentSubtle"), outline: `1px solid ${c("accentSubtleBorder")}`, color: "inherit" },
    ".cm-nonmatchingBracket, &.cm-focused .cm-nonmatchingBracket": { backgroundColor: c("dangerSubtle"), color: c("danger") },
    ".cm-searchMatch": { backgroundColor: c("searchMatch"), outline: "none", borderRadius: "2px" },
    ".cm-searchMatch.cm-searchMatch-selected": { backgroundColor: c("searchMatchCurrent") },
    ".cm-selectionMatch": { backgroundColor: "color-mix(in srgb, var(--c-accent) 14%, transparent)" },
    ".cm-foldPlaceholder": { backgroundColor: c("neutralSubtle"), border: `1px solid ${c("neutralBorder")}`, color: c("textSecondary"), borderRadius: "4px", padding: "0 4px", margin: "0 2px" },
    ".cm-foldGutter .cm-gutterElement": { width: "16px", display: "flex", alignItems: "center", justifyContent: "center" },
    ".cm-highlightSpace": { backgroundImage: `radial-gradient(circle at 50% 55%, ${c("textDisabled")} 12%, transparent 13%)` },
    ".cm-highlightTab": { backgroundImage: "none", position: "relative" },
    ".cm-highlightTab::before": { content: '"\\2192"', position: "absolute", left: "1px", color: c("textDisabled") },
    ".cm-trailingSpace": { backgroundColor: c("dangerSubtle") },
    ".cm-specialChar": { color: c("danger") },
    ".cm-panels": { backgroundColor: c("bgSubtle"), color: c("textPrimary") },
    ".cm-panels.cm-panels-top": { borderBottom: `1px solid ${c("borderSubtle")}` },
    ".cm-panels.cm-panels-bottom": { borderTop: `1px solid ${c("borderSubtle")}` },
    ".cm-tooltip": { backgroundColor: c("bgElevated"), border: `1px solid ${c("border")}`, borderRadius: "6px", boxShadow: "var(--shadow-popup)", color: c("textPrimary") },
    ".cm-tooltip.cm-tooltip-autocomplete > ul": { fontFamily: "var(--font-mono)", maxHeight: "14em", padding: "3px" },
    ".cm-tooltip.cm-tooltip-autocomplete > ul > li": { padding: "2px 8px", borderRadius: "4px", lineHeight: "20px" },
    ".cm-tooltip.cm-tooltip-autocomplete > ul > li[aria-selected]": { backgroundColor: c("accent"), color: c("textOnAccent") },
    ".cm-completionMatchedText": { textDecoration: "none", fontWeight: "600", color: c("accent") },
    "li[aria-selected] .cm-completionMatchedText": { color: "inherit" },
    ".cm-completionDetail": { color: c("textTertiary"), marginLeft: "8px", fontStyle: "normal" },
  }, { dark });
}

const builtin = { light: [paperplaneEditorTheme(false), syntaxHighlighting(paperplaneHighlight)], dark: [paperplaneEditorTheme(true), syntaxHighlighting(paperplaneHighlight)] };

export function paperplaneTheme(kind: "light" | "dark"): Extension {
  return builtin[kind];
}

// ---------------------------------------------------------------------------
// 插件主题 JSON → Extension

export interface ThemeJSON {
  kind?: "light" | "dark";
  colors?: Record<string, string>;
  tokens?: Record<string, { color?: string; fontStyle?: string; background?: string }>;
}

/** 支持的 colors 键（其余忽略） */
export const THEME_COLOR_KEYS = [
  "background", "foreground", "cursor", "selection", "selectionInactive", "lineHighlight",
  "gutterBackground", "gutterForeground", "gutterActiveForeground", "gutterBorder",
  "matchingBracket", "searchMatch", "searchMatchCurrent", "selectionMatch", "whitespace",
  "foldPlaceholder", "panelBackground",
];

/** "keyword"、"definition(variableName)"、"function(propertyName)" → Tag */
export function parseTag(name: string): Tag | null {
  const m = /^(\w+)\((.+)\)$/.exec(name.trim());
  const T = tags as unknown as Record<string, any>;
  if (m) {
    const mod = T[m[1]];
    const inner = parseTag(m[2]);
    return typeof mod === "function" && inner ? mod(inner) : null;
  }
  const tag = T[name.trim()];
  return tag instanceof Tag ? tag : null;
}

export function themeFromJSON(j: ThemeJSON): Extension {
  const c = j.colors ?? {};
  const dark = j.kind === "dark";
  const spec: Record<string, Record<string, string>> = {};
  const set = (sel: string, prop: string, val?: string) => { if (val) (spec[sel] ??= {})[prop] = val; };
  set("&", "backgroundColor", c.background);
  set("&", "color", c.foreground);
  set(".cm-gutters", "backgroundColor", c.gutterBackground ?? c.background);
  set(".cm-gutters", "color", c.gutterForeground);
  set(".cm-gutters", "borderRight", c.gutterBorder ? `1px solid ${c.gutterBorder}` : undefined);
  set(".cm-activeLineGutter", "color", c.gutterActiveForeground);
  set(".cm-activeLineGutter", "backgroundColor", c.lineHighlight);
  set(".cm-activeLine", "backgroundColor", c.lineHighlight);
  set(".cm-cursor, .cm-dropCursor", "borderLeftColor", c.cursor);
  set(".cm-content", "caretColor", c.cursor);
  set("&.cm-focused > .cm-scroller > .cm-selectionLayer .cm-selectionBackground, ::selection", "backgroundColor", c.selection);
  set(".cm-selectionBackground", "backgroundColor", c.selectionInactive ?? c.selection);
  set(".cm-matchingBracket, &.cm-focused .cm-matchingBracket", "backgroundColor", c.matchingBracket);
  set(".cm-searchMatch", "backgroundColor", c.searchMatch);
  set(".cm-searchMatch.cm-searchMatch-selected", "backgroundColor", c.searchMatchCurrent);
  set(".cm-selectionMatch", "backgroundColor", c.selectionMatch);
  set(".cm-highlightSpace", "color", c.whitespace);
  set(".cm-highlightTab", "color", c.whitespace);
  set(".cm-foldPlaceholder", "backgroundColor", c.foldPlaceholder);
  set(".cm-panels", "backgroundColor", c.panelBackground);
  const specs: any[] = [];
  for (const [name, st] of Object.entries(j.tokens ?? {})) {
    const tag = parseTag(name);
    if (!tag) continue;
    const s: any = { tag };
    if (st.color) s.color = st.color;
    if (st.background) s.backgroundColor = st.background;
    const fs = st.fontStyle ?? "";
    if (/italic/.test(fs)) s.fontStyle = "italic";
    if (/bold/.test(fs)) s.fontWeight = "bold";
    if (/underline/.test(fs)) s.textDecoration = "underline";
    if (/strikethrough/.test(fs)) s.textDecoration = "line-through";
    specs.push(s);
  }
  // 先铺内置外观（布局与未给出的颜色），再叠主题自己的颜色
  const out: Extension[] = [paperplaneEditorTheme(dark), EditorView.theme(spec, { dark })];
  if (specs.length) out.push(syntaxHighlighting(HighlightStyle.define(specs, { themeType: dark ? "dark" : "light" })));
  // 主题没覆盖的 token 仍用内置色
  out.push(syntaxHighlighting(paperplaneHighlight, { fallback: true }));
  return out;
}

/** 字体：fontFamily 为空时用 design-tokens 的等宽字体 */
export function fontTheme(family: string, size: number): Extension {
  return EditorView.theme({
    "&": { fontSize: size + "px" },
    ".cm-scroller": { fontFamily: family, lineHeight: Math.round(size * 1.5) + "px" },
    ".cm-tooltip.cm-tooltip-autocomplete > ul": { fontFamily: family },
  });
}
