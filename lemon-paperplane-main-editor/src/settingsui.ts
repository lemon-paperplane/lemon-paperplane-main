// 编辑器设置弹出面板（写回 settings.editor）：快捷键方案、配色、字号、缩进、换行、自动保存等。
import { app, applySettings, toggleWhitespace } from "./app";
import { h } from "./util";
import { popover, dropdown, switchControl } from "./ui";
import { t } from "./i18n";
import type { EditorSettings } from "./api";

export function openSettings(anchor: HTMLElement) {
  const s = app.settings;
  const set = (patch: Partial<EditorSettings>) => void applySettings(patch, true);
  const row = (label: string, control: HTMLElement) => h("div.pp-form-row", null, h("label.pp-form-label", null, label), h("div.pp-form-control", null, control));
  const keymaps = [...app.contribs.keymaps].sort((a, b) => a.name.localeCompare(b.name));
  const keymapOpts = [
    { value: "", label: t("editor.settings.keymapDefault", keymaps.find((k) => k.id === app.contribs.defaultKeymap)?.name ?? app.contribs.defaultKeymap) },
    ...keymaps.map((k) => ({ value: k.id, label: k.name })),
  ];
  const themeOpts = [
    { value: "", label: t("editor.settings.themeFollow") },
    { value: "paperplane-light", label: "Paperplane Light" },
    { value: "paperplane-dark", label: "Paperplane Dark" },
    ...app.contribs.editorThemes.map((x) => ({ value: x.id, label: x.name })),
  ];
  const num = (value: number, min: number, max: number, onChange: (n: number) => void) => {
    const el = h("input.pp-input.pp-number", { type: "number", value: String(value), min: String(min), max: String(max) });
    el.addEventListener("change", () => {
      const n = Math.round(+el.value);
      if (!Number.isFinite(n) || n < min || n > max) { el.classList.add("invalid"); return; }
      el.classList.remove("invalid");
      onChange(n);
    });
    return el;
  };
  const content = h("div.pp-settings", null,
    h("div.pp-settings-title", null, t("editor.settings.title")),
    row(t("editor.settings.keymap"), dropdown(keymapOpts, s.keymap || "", (v) => set({ keymap: v }))),
    row(t("editor.settings.theme"), dropdown(themeOpts, s.theme || "", (v) => set({ theme: v }))),
    row(t("editor.settings.fontSize"), num(s.fontSize || 13, 8, 32, (n) => set({ fontSize: n }))),
    row(t("editor.settings.tabSize"), num(s.tabSize || 4, 1, 16, (n) => set({ tabSize: n }))),
    row(t("editor.settings.insertSpaces"), switchControl(s.insertSpaces, (v) => set({ insertSpaces: v }))),
    row(t("editor.settings.wordWrap"), switchControl(s.wordWrap, (v) => set({ wordWrap: v }))),
    row(t("editor.settings.whitespace"), switchControl(app.settings.showWhitespace, () => toggleWhitespace())),
    row(t("editor.settings.autoSave"), dropdown([
      { value: "off", label: t("editor.settings.autoSaveOff") },
      { value: "afterDelay", label: t("editor.settings.autoSaveDelay") },
      { value: "onFocusChange", label: t("editor.settings.autoSaveFocus") },
    ], s.autoSave || "off", (v) => set({ autoSave: v as EditorSettings["autoSave"] }))),
    row(t("editor.settings.trimTrailing"), switchControl(s.trimTrailingWhitespace, (v) => set({ trimTrailingWhitespace: v }))),
    row(t("editor.settings.finalNewline"), switchControl(s.insertFinalNewline, (v) => set({ insertFinalNewline: v }))),
  );
  popover(content, anchor, { align: "end", above: true });
}
