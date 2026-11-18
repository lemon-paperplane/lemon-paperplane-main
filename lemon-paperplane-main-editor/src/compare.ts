// 对比视图（懒加载 @codemirror/merge）：左边磁盘上的版本，右边我的修改（可继续编辑）。
import { MergeView } from "@codemirror/merge";
import { EditorState, StateEffect, type Extension } from "@codemirror/state";
import { EditorView, lineNumbers, highlightSpecialChars, drawSelection } from "@codemirror/view";
import { h } from "./util";
import { iconButton } from "./ui";
import { t } from "./i18n";

export interface CompareOptions {
  title: string;
  path: string;
  disk: string;
  mine: string;
  extensions: Extension;
  language: Promise<unknown>;
  onKeepMine: (content: string) => Promise<void> | void;
  onUseDisk: () => Promise<void> | void;
}

export function showCompare(o: CompareOptions) {
  const host = h("div.pp-compare-body");
  const close = () => {
    mv.destroy();
    scrim.remove();
    document.removeEventListener("keydown", onKey, true);
  };
  const keepBtn = h("button.pp-btn.primary", { type: "button", onclick: async () => { const c = mv.b.state.doc.toString(); close(); await o.onKeepMine(c); } }, t("editor.compare.keepMine"));
  const diskBtn = h("button.pp-btn.secondary", { type: "button", onclick: async () => { close(); await o.onUseDisk(); } }, t("editor.compare.useDisk"));
  const card = h("div.pp-compare", { role: "dialog", "aria-modal": "true" },
    h("div.pp-dialog-title", null,
      h("span", null, t("editor.compare.title", o.title)),
      iconButton("x", t("common.close"), close, true)),
    h("div.pp-compare-heads", null, h("div", null, t("editor.compare.disk")), h("div", null, t("editor.compare.mine"))),
    host,
    h("div.pp-dialog-buttons", null, h("button.pp-btn.secondary", { type: "button", onclick: close }, t("common.cancel")), diskBtn, keepBtn));
  const scrim = h("div.pp-scrim", null, card);
  document.body.appendChild(scrim);
  const base = [lineNumbers(), highlightSpecialChars(), drawSelection(), o.extensions];
  const mv = new MergeView({
    a: { doc: o.disk, extensions: [base, EditorState.readOnly.of(true), EditorView.editable.of(false)] },
    b: { doc: o.mine, extensions: base },
    parent: host,
    gutter: true,
    highlightChanges: true,
    collapseUnchanged: { margin: 3, minSize: 6 },
  });
  // 语言着色到了再补上
  o.language.then((ext) => {
    if (!scrim.isConnected || !ext) return;
    for (const v of [mv.a, mv.b]) v.dispatch({ effects: StateEffect.appendConfig.of(ext as Extension) });
  });
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(); }
  };
  document.addEventListener("keydown", onKey, true);
}
