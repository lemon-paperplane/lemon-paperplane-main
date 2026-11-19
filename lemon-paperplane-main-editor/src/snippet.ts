// 代码输入框（单个 CodeMirror，没有标签页/文件树）：给原生界面里的「命令」这类输入框用。
// 宿主用 WKWebView / WebView2 加载 /editor/snippet.html#lang=shell&theme=dark&placeholder=…，
// 通过 window.paperplaneSnippet 设值/换主题/聚焦，网页经 messageHandlers.paperplane 回传：
//   {type:"ready"} {type:"change", value} {type:"submit"}（⌘↩/Ctrl+Enter） {type:"escape"} {type:"focus", value}
// 高度由宿主固定，内容多了在框内滚动
import "./gen/tokens.css";
import { Compartment, EditorState } from "@codemirror/state";
import { EditorView, drawSelection, highlightActiveLine, keymap, lineNumbers, placeholder } from "@codemirror/view";
import { defaultKeymap, history, historyKeymap, indentWithTab } from "@codemirror/commands";
import { StreamLanguage, bracketMatching, indentOnInput } from "@codemirror/language";
import { closeBrackets } from "@codemirror/autocomplete";
import { shell } from "@codemirror/legacy-modes/mode/shell";
import { powerShell } from "@codemirror/legacy-modes/mode/powershell";
import { paperplaneTheme } from "./theme";
import { fontStack, isMac, platform } from "./util";
import { fonts } from "./gen/tokens";

type Msg = { type: string; value?: unknown };

function post(m: Msg) {
  const w = window as any;
  if (w.webkit?.messageHandlers?.paperplane) w.webkit.messageHandlers.paperplane.postMessage(m);
  else if (w.chrome?.webview) w.chrome.webview.postMessage(m);
}

const params = new URLSearchParams(location.hash.slice(1));
const langs: Record<string, () => any> = {
  shell: () => StreamLanguage.define(shell),
  powershell: () => StreamLanguage.define(powerShell),
};
let theme: "light" | "dark" = params.get("theme") === "dark" ? "dark" : "light";
document.documentElement.dataset.theme = theme;
document.documentElement.dataset.platform = platform;
// Windows / Linux 上 ui-monospace 不生效（中文 Windows 会落到新宋体）：按设计变量取等宽字体；Mac 仍用 ui-monospace
if (!isMac) document.documentElement.style.setProperty("--font-mono", fontStack((fonts.mono as any)[platform] ?? "", "monospace"));
const themeC = new Compartment();

const view = new EditorView({
  parent: document.getElementById("root")!,
  state: EditorState.create({
    doc: "",
    extensions: [
      lineNumbers(),
      history(),
      drawSelection(),
      highlightActiveLine(),
      indentOnInput(),
      bracketMatching(),
      closeBrackets(),
      EditorView.lineWrapping,
      placeholder(params.get("placeholder") || ""),
      (langs[params.get("lang") || "shell"] ?? langs.shell)(),
      themeC.of(paperplaneTheme(theme)),
      keymap.of([
        { key: "Mod-Enter", run: () => { post({ type: "submit" }); return true; } },
        { key: "Escape", run: () => { post({ type: "escape" }); return true; } },
        ...defaultKeymap, ...historyKeymap, indentWithTab,
      ]),
      EditorView.domEventHandlers({
        focus: () => { post({ type: "focus", value: true }); },
        blur: () => { post({ type: "focus", value: false }); },
      }),
      EditorView.updateListener.of((u) => {
        if (u.docChanged) post({ type: "change", value: u.state.doc.toString() });
      }),
    ],
  }),
});

(window as any).paperplaneSnippet = {
  setValue(v: string) {
    if (v !== view.state.doc.toString()) view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: v } });
  },
  setTheme(t: "light" | "dark") {
    theme = t;
    document.documentElement.dataset.theme = t;
    view.dispatch({ effects: themeC.reconfigure(paperplaneTheme(t)) });
  },
  focus() { view.focus(); },
};

post({ type: "ready" });
