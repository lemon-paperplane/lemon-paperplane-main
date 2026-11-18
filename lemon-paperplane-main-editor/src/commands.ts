// 命令表：所有编辑与界面动作都是具名命令（id 见 COMMANDS.md），快捷键方案只做「键 → 命令」映射。
import { EditorSelection, type ChangeSpec, type SelectionRange } from "@codemirror/state";
import type { EditorView } from "@codemirror/view";
import * as C from "@codemirror/commands";
import * as S from "@codemirror/search";
import * as L from "@codemirror/language";
import { startCompletion, closeCompletion } from "@codemirror/autocomplete";
import { getIndentUnit, indentString } from "@codemirror/language";

export interface Command {
  run: (view: EditorView) => boolean;
  /** 界面级命令：焦点不在编辑区（文件树、查找框、快速打开）时也响应 */
  global?: boolean;
}

type Fn = (view: EditorView) => boolean;
// CodeMirror 的命令自己处理只读（EditorState.readOnly）
const ed = (f: Fn | ((t: { state: any; dispatch: any }) => boolean)): Command => ({ run: (v) => f(v) });

// ---------------------------------------------------------------------------
// 自定义编辑命令

/** 在当前行上方插入一行并保持缩进 */
const insertLineBefore: Fn = (view) => {
  const { state } = view;
  if (state.readOnly) return false;
  const changes: ChangeSpec[] = [];
  const ranges: SelectionRange[] = [];
  let shift = 0;
  const seen = new Set<number>();
  for (const r of state.selection.ranges) {
    const line = state.doc.lineAt(r.head);
    if (seen.has(line.number)) continue;
    seen.add(line.number);
    const indent = /^\s*/.exec(line.text)![0];
    changes.push({ from: line.from, insert: indent + state.lineBreak });
    ranges.push(EditorSelection.cursor(line.from + shift + indent.length));
    shift += indent.length + state.lineBreak.length;
  }
  view.dispatch({ changes, selection: EditorSelection.create(ranges), scrollIntoView: true, userEvent: "input" });
  return true;
};

/** 合并行：把下一行接到当前行末（去掉缩进，中间留一个空格） */
const joinLines: Fn = (view) => {
  const { state } = view;
  if (state.readOnly) return false;
  const changes: ChangeSpec[] = [];
  let last = -1;
  for (const r of state.selection.ranges) {
    const start = state.doc.lineAt(r.from).number;
    let end = state.doc.lineAt(r.to).number;
    if (end === start) end = start + 1;
    for (let n = Math.max(start, last + 1); n < end && n < state.doc.lines; n++) {
      const a = state.doc.line(n), b = state.doc.line(n + 1);
      const trailing = /\s*$/.exec(a.text)![0].length;
      const lead = /^\s*/.exec(b.text)![0].length;
      changes.push({ from: a.to - trailing, to: b.from + lead, insert: a.text.trim() && b.text.trim() ? " " : "" });
      last = n;
    }
  }
  if (!changes.length) return false;
  view.dispatch({ changes, scrollIntoView: true, userEvent: "delete" });
  return true;
};

function mapSelectionText(f: (s: string) => string): Fn {
  return (view) => {
    const { state } = view;
    if (state.readOnly) return false;
    let any = false;
    const tr = state.changeByRange((r) => {
      let range = r;
      if (r.empty) {
        const w = state.wordAt(r.head);
        if (!w) return { range: r };
        range = EditorSelection.range(w.from, w.to);
      }
      any = true;
      const text = f(state.sliceDoc(range.from, range.to));
      return { changes: { from: range.from, to: range.to, insert: text }, range: EditorSelection.range(range.from, range.from + text.length) };
    });
    if (!any) return false;
    view.dispatch(state.update(tr, { userEvent: "input" }));
    return true;
  };
}

/** 有选区时复制选区，否则复制当前行（IntelliJ 的 Duplicate） */
const duplicateLineOrSelection: Fn = (view) => {
  const { state } = view;
  if (state.readOnly) return false;
  if (state.selection.ranges.every((r) => r.empty)) return C.copyLineDown(view);
  const tr = state.changeByRange((r) => {
    if (r.empty) return { range: r };
    const text = state.sliceDoc(r.from, r.to);
    return { changes: { from: r.to, insert: text }, range: EditorSelection.range(r.to, r.to + text.length) };
  });
  view.dispatch(state.update(tr, { userEvent: "input.copyline", scrollIntoView: true }));
  return true;
};

/** Tab：有选区时整体缩进，否则插入一个缩进单位（按设置用空格或制表符，对齐到下一个制表位） */
export const insertIndent: Fn = (view) => {
  const { state } = view;
  if (state.readOnly) return false;
  if (state.selection.ranges.some((r) => !r.empty)) return C.indentMore(view);
  const unit = getIndentUnit(state);
  const useTab = state.facet(L.indentUnit).includes("\t");
  view.dispatch(state.update(state.changeByRange((r) => {
    const line = state.doc.lineAt(r.head);
    let col = 0;
    for (let i = line.from; i < r.head; i++) col = state.sliceDoc(i, i + 1) === "\t" ? col + state.tabSize - (col % state.tabSize) : col + 1;
    const insert = useTab ? "\t" : indentString(state, unit - (col % unit));
    return { changes: { from: r.head, insert }, range: EditorSelection.cursor(r.head + insert.length) };
  }), { userEvent: "input.indent", scrollIntoView: true }));
  return true;
};

/** 扩大/缩小选区（缩小按扩大的历史回退） */
let expandStack: { doc: unknown; sel: EditorSelection }[] = [];
const expandSelection: Fn = (view) => {
  const before = view.state.selection;
  if (!C.selectParentSyntax(view)) return false;
  const top = expandStack[expandStack.length - 1];
  if (!top || top.doc !== view.state.doc) expandStack = [];
  expandStack.push({ doc: view.state.doc, sel: before });
  return true;
};
const shrinkSelection: Fn = (view) => {
  const top = expandStack.pop();
  if (!top || top.doc !== view.state.doc) { expandStack = []; return false; }
  view.dispatch({ selection: top.sel, scrollIntoView: true });
  return true;
};

/** Eclipse Ctrl-K：有选中文本时用它作为查找内容，再查找下一个/上一个 */
function findWithSelection(dir: 1 | -1): Fn {
  return (view) => {
    const { state } = view;
    const sel = state.selection.main;
    if (!sel.empty) {
      const text = state.sliceDoc(sel.from, sel.to);
      if (!text.includes("\n")) {
        const q = S.getSearchQuery(state);
        if (q.search !== text || q.regexp) {
          view.dispatch({ effects: S.setSearchQuery.of(new S.SearchQuery({ search: text, caseSensitive: q.caseSensitive, wholeWord: q.wholeWord, replace: q.replace })) });
        }
      }
    }
    return dir > 0 ? S.findNext(view) : S.findPrevious(view);
  };
}

const escape: Fn = (view) => {
  if (closeCompletion(view)) return true;
  if (S.searchPanelOpen(view.state)) return S.closeSearchPanel(view);
  return C.simplifySelection(view);
};

// ---------------------------------------------------------------------------
// 命令表

export const commands: Record<string, Command> = {
  // 撤销
  undo: ed(C.undo),
  redo: ed(C.redo),
  undoSelection: ed(C.undoSelection),
  redoSelection: ed(C.redoSelection),
  // 选择
  selectAll: ed(C.selectAll),
  selectLine: ed(C.selectLine),
  expandSelection: { run: expandSelection },
  shrinkSelection: { run: shrinkSelection },
  selectNextOccurrence: ed(S.selectNextOccurrence),
  selectAllOccurrences: ed(S.selectSelectionMatches),
  addCursorAbove: ed(C.addCursorAbove),
  addCursorBelow: ed(C.addCursorBelow),
  simplifySelection: ed(C.simplifySelection),
  escape: { run: escape },
  cursorMatchingBracket: ed(C.cursorMatchingBracket),
  selectMatchingBracket: ed(C.selectMatchingBracket),
  // 光标移动（方案可重映射；基本移动键由内置标准键位提供）
  cursorDocStart: ed(C.cursorDocStart),
  cursorDocEnd: ed(C.cursorDocEnd),
  selectDocStart: ed(C.selectDocStart),
  selectDocEnd: ed(C.selectDocEnd),
  cursorLineStart: ed(C.cursorLineBoundaryBackward),
  cursorLineEnd: ed(C.cursorLineBoundaryForward),
  selectLineStart: ed(C.selectLineBoundaryBackward),
  selectLineEnd: ed(C.selectLineBoundaryForward),
  cursorWordLeft: ed(C.cursorGroupLeft),
  cursorWordRight: ed(C.cursorGroupRight),
  selectWordLeft: ed(C.selectGroupLeft),
  selectWordRight: ed(C.selectGroupRight),
  cursorSubwordLeft: ed(C.cursorSubwordBackward),
  cursorSubwordRight: ed(C.cursorSubwordForward),
  cursorPageUp: ed(C.cursorPageUp),
  cursorPageDown: ed(C.cursorPageDown),
  // 编辑
  deleteLine: ed(C.deleteLine),
  duplicateLine: ed(C.copyLineDown),
  copyLineDown: ed(C.copyLineDown),
  copyLineUp: ed(C.copyLineUp),
  duplicateLineOrSelection: { run: duplicateLineOrSelection },
  moveLineUp: ed(C.moveLineUp),
  moveLineDown: ed(C.moveLineDown),
  insertLineAfter: ed(C.insertBlankLine),
  insertLineBefore: { run: insertLineBefore },
  joinLines: { run: joinLines },
  splitLine: ed(C.splitLine),
  deleteWordBackward: ed(C.deleteGroupBackward),
  deleteWordForward: ed(C.deleteGroupForward),
  deleteToLineEnd: ed(C.deleteToLineEnd),
  deleteToLineStart: ed(C.deleteLineBoundaryBackward),
  transposeChars: ed(C.transposeChars),
  toUpperCase: { run: mapSelectionText((s) => s.toUpperCase()) },
  toLowerCase: { run: mapSelectionText((s) => s.toLowerCase()) },
  toggleCase: { run: mapSelectionText((s) => (s === s.toUpperCase() ? s.toLowerCase() : s.toUpperCase())) },
  trimTrailingWhitespace: ed(C.deleteTrailingWhitespace),
  // 注释与缩进
  toggleLineComment: ed(C.toggleLineComment),
  toggleBlockComment: ed(C.toggleBlockComment),
  toggleComment: ed(C.toggleComment),
  addBlockComment: ed(C.blockComment),
  removeBlockComment: ed(C.blockUncomment),
  indentMore: ed(C.indentMore),
  indentLess: ed(C.indentLess),
  indentSelection: ed(C.indentSelection),
  insertTab: { run: insertIndent },
  // 折叠
  fold: ed(L.foldCode),
  unfold: ed(L.unfoldCode),
  toggleFold: ed(L.toggleFold),
  foldAll: ed(L.foldAll),
  unfoldAll: ed(L.unfoldAll),
  // 补全
  startCompletion: ed(startCompletion),
  // 查找（面板内）
  findNext: ed(S.findNext),
  findPrevious: ed(S.findPrevious),
  findNextSelection: { run: findWithSelection(1) },
  findPreviousSelection: { run: findWithSelection(-1) },
  selectAllMatches: ed(S.selectMatches),
  replaceNext: ed(S.replaceNext),
  replaceAll: ed(S.replaceAll),
  closeFind: ed(S.closeSearchPanel),
};

/** 界面级命令由 app 注册（保存、标签页、快速打开等） */
export function registerCommands(map: Record<string, Command>) {
  Object.assign(commands, map);
}

export function runCommand(id: string, view: EditorView): boolean {
  const c = commands[id];
  if (!c) {
    console.warn("[paperplane] unknown command", id);
    return false;
  }
  try {
    return c.run(view);
  } catch (e) {
    console.error("[paperplane] command failed", id, e);
    return false;
  }
}

/** 给 module 类快捷键方案（Vim/Emacs）的命令接口：commands.save(view)、commands.run("save", view) */
export function commandApi(): Record<string, any> {
  const api: Record<string, any> = { run: runCommand, ids: () => Object.keys(commands) };
  for (const id of Object.keys(commands)) api[id] = (view: EditorView) => runCommand(id, view);
  return api;
}
