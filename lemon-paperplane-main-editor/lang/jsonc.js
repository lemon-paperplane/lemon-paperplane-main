// JSON with Comments / JSON5（tsconfig.json、uni-app 的 pages.json、manifest.json 等）：允许注释、尾逗号、单引号、无引号键。
import { StreamLanguage, LanguageSupport } from "@codemirror/language";

const jsonc = {
  name: "jsonc",
  startState: () => ({ inBlock: false }),
  token(stream, state) {
    if (state.inBlock) {
      if (stream.skipTo("*/")) { stream.next(); stream.next(); state.inBlock = false; } else stream.skipToEnd();
      return "comment";
    }
    if (stream.eatSpace()) return null;
    if (stream.match("//")) { stream.skipToEnd(); return "comment"; }
    if (stream.match("/*")) { state.inBlock = true; return "comment"; }
    const ch = stream.peek();
    if (ch === '"' || ch === "'") {
      stream.next();
      let esc = false, c;
      while ((c = stream.next()) != null) {
        if (c === ch && !esc) break;
        esc = !esc && c === "\\";
      }
      // 后面紧跟冒号的是属性名
      return /^\s*:/.test(stream.string.slice(stream.pos)) ? "propertyName" : "string";
    }
    if (stream.match(/^[+-]?(?:0x[0-9a-fA-F]+|(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?|Infinity|NaN)/)) return "number";
    if (stream.match(/^(?:true|false|null)\b/)) return "atom";
    if (stream.match(/^[A-Za-z_$][\w$]*/)) return /^\s*:/.test(stream.string.slice(stream.pos)) ? "propertyName" : "variableName";
    if (stream.match(/^[{}[\]]/)) return "bracket";
    if (stream.match(/^[,:]/)) return "punctuation";
    stream.next();
    return null;
  },
  languageData: { commentTokens: { line: "//", block: { open: "/*", close: "*/" } }, closeBrackets: { brackets: ["(", "[", "{", "'", '"'] } },
};

export default function () {
  return new LanguageSupport(StreamLanguage.define(jsonc));
}
