// Git 改动行标记：vcs.diff（工作区相对 HEAD，context 0）→ 行号旁的新增/修改/删除标记；点标记可看原内容并回滚。
import { RangeSet, StateEffect, StateField, type Extension } from "@codemirror/state";
import { EditorView, GutterMarker, gutter } from "@codemirror/view";
import type { VCSDiff } from "./api";

export interface ChangeBlock {
  kind: "added" | "modified" | "deleted";
  /** 新文件里的行号范围（1 起，含两端）；deleted 时 from=to=删除点所在行（0 表示文件开头） */
  from: number;
  to: number;
  oldLines: string[];
}

class ChangeMarker extends GutterMarker {
  constructor(readonly block: ChangeBlock, readonly edge: "top" | "bottom" | "") { super(); }
  eq(o: ChangeMarker) { return o.block === this.block && o.edge === this.edge; }
  toDOM() {
    const el = document.createElement("div");
    el.className = "pp-git-mark " + this.block.kind + (this.edge ? " " + this.edge : "");
    return el;
  }
}

export const setGitBlocks = StateEffect.define<ChangeBlock[]>();

interface GitState { blocks: ChangeBlock[]; marks: RangeSet<ChangeMarker> }

export const gitField = StateField.define<GitState>({
  create: () => ({ blocks: [], marks: RangeSet.empty }),
  update(v, tr) {
    for (const e of tr.effects) {
      if (e.is(setGitBlocks)) return { blocks: e.value, marks: buildMarks(tr.state.doc, e.value) };
    }
    return tr.docChanged ? { blocks: v.blocks, marks: v.marks.map(tr.changes) } : v;
  },
});

function buildMarks(doc: { lines: number; line(n: number): { from: number } }, blocks: ChangeBlock[]): RangeSet<ChangeMarker> {
  const ranges = [];
  for (const b of blocks) {
    if (b.kind === "deleted") {
      const n = Math.min(Math.max(b.from, 1), doc.lines);
      ranges.push(new ChangeMarker(b, b.from === 0 ? "top" : "bottom").range(doc.line(n).from));
      continue;
    }
    for (let n = b.from; n <= b.to && n <= doc.lines; n++) ranges.push(new ChangeMarker(b, "").range(doc.line(n).from));
  }
  return RangeSet.of(ranges, true);
}

/** 把 vcs.diff 的块拆成连续改动段并分类 */
export function blocksFromDiff(d: VCSDiff | null): ChangeBlock[] {
  const out: ChangeBlock[] = [];
  if (!d || d.binary || d.tooLarge) return out;
  for (const h of d.hunks ?? []) {
    let dels: string[] = [];
    let adds: number[] = [];
    // 纯删除的块（newLines 为 0）里 newStart 是删除点前一行；其余是第一行新内容
    let lastNew = h.newLines === 0 ? h.newStart : h.newStart - 1;
    const flush = () => {
      if (adds.length && dels.length) out.push({ kind: "modified", from: adds[0], to: adds[adds.length - 1], oldLines: dels });
      else if (adds.length) out.push({ kind: "added", from: adds[0], to: adds[adds.length - 1], oldLines: [] });
      else if (dels.length) out.push({ kind: "deleted", from: lastNew, to: lastNew, oldLines: dels });
      dels = []; adds = [];
    };
    for (const l of h.lines ?? []) {
      if (l.kind === "-") { if (adds.length) flush(); dels.push(l.text); }
      else if (l.kind === "+") { adds.push(l.new ?? lastNew + 1); lastNew = l.new ?? lastNew + 1; }
      else { flush(); lastNew = l.new ?? lastNew + 1; }
    }
    flush();
  }
  return out;
}

export function gitGutter(onClick: (view: EditorView, block: ChangeBlock, dom: HTMLElement) => void): Extension {
  return [
    gitField,
    gutter({
      class: "pp-git-gutter",
      markers: (v) => v.state.field(gitField).marks,
      domEventHandlers: {
        mousedown(view, line, event) {
          const marks = view.state.field(gitField).marks;
          let found: ChangeMarker | null = null;
          marks.between(line.from, line.from, (_f, _t, m) => { found = m; return false; });
          if (!found) return false;
          onClick(view, (found as ChangeMarker).block, event.target as HTMLElement);
          return true;
        },
      },
    }),
  ];
}

/** 下一处/上一处改动（循环；按标记的当前位置，编辑后也准） */
export function nextChange(view: EditorView, dir: 1 | -1): boolean {
  const field = view.state.field(gitField, false);
  if (!field || !field.blocks.length) return false;
  const starts: number[] = [];
  const seen = new Set<ChangeBlock>();
  field.marks.between(0, view.state.doc.length, (from, _t, m) => {
    if (!seen.has(m.block)) { seen.add(m.block); starts.push(view.state.doc.lineAt(from).number); }
  });
  if (!starts.length) return false;
  starts.sort((a, b) => a - b);
  const cur = view.state.doc.lineAt(view.state.selection.main.head).number;
  const target = dir > 0 ? starts.find((s) => s > cur) ?? starts[0] : [...starts].reverse().find((s) => s < cur) ?? starts[starts.length - 1];
  const pos = view.state.doc.line(target).from;
  view.dispatch({ selection: { anchor: pos }, effects: EditorView.scrollIntoView(pos, { y: "center" }) });
  return true;
}

/** 改动段在当前文档里的行号范围（标记会随编辑移动） */
function currentLines(view: EditorView, b: ChangeBlock): [number, number] | null {
  let lo = Infinity, hi = -Infinity;
  view.state.field(gitField).marks.between(0, view.state.doc.length, (from, _t, m) => {
    if (m.block === b) { const n = view.state.doc.lineAt(from).number; lo = Math.min(lo, n); hi = Math.max(hi, n); }
  });
  return lo === Infinity ? null : [lo, hi];
}

/** 回滚一段改动到 HEAD 的内容 */
export function revertBlock(view: EditorView, b: ChangeBlock) {
  const range = currentLines(view, b);
  if (!range) return;
  const doc = view.state.doc;
  const nl = view.state.lineBreak;
  if (b.kind === "deleted") {
    const text = b.oldLines.join(nl);
    if (b.from === 0) view.dispatch({ changes: { from: 0, insert: text + nl }, userEvent: "input" });
    else view.dispatch({ changes: { from: doc.line(range[0]).to, insert: nl + text }, userEvent: "input" });
    return;
  }
  const a = doc.line(range[0]), z = doc.line(range[1]);
  if (b.kind === "added") {
    const from = a.from > 0 ? a.from - nl.length : a.from;
    const to = a.from > 0 ? z.to : Math.min(z.to + nl.length, doc.length);
    view.dispatch({ changes: { from, to }, userEvent: "delete" });
  } else {
    view.dispatch({ changes: { from: a.from, to: z.to, insert: b.oldLines.join(nl) }, userEvent: "input" });
  }
}
