// go.mod / go.work：指令、模块路径、版本号、注释。
import { StreamLanguage, LanguageSupport } from "@codemirror/language";

const DIRECTIVES = /^(?:module|go|toolchain|godebug|require|replace|exclude|retract|use|tool|ignore)\b/;

const gomod = {
  name: "gomod",
  token(stream) {
    if (stream.eatSpace()) return null;
    if (stream.match("//")) { stream.skipToEnd(); return "comment"; }
    if (stream.sol() || /^\s*$/.test(stream.string.slice(0, stream.start))) {
      if (stream.match(DIRECTIVES)) return "keyword";
    }
    if (stream.match("=>")) return "operator";
    if (stream.match(/^[()]/)) return "bracket";
    if (stream.match(/^v\d+\.\d+\.\d+[\w.+-]*/)) return "number";
    if (stream.match(/^\d+\.\d+(?:\.\d+)?(?:rc\d+|beta\d+)?/)) return "number";
    if (stream.match(/^"(?:[^"\\]|\\.)*"?/) || stream.match(/^`[^`]*`?/)) return "string";
    if (stream.match(/^[\w.\-/~+@]+/)) return "variableName";
    stream.next();
    return null;
  },
  languageData: { commentTokens: { line: "//" } },
};

export default function () {
  return new LanguageSupport(StreamLanguage.define(gomod));
}
