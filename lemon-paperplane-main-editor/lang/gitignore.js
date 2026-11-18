// .gitignore / .gitattributes / .dockerignore 等：注释、取反、通配符、属性。
import { StreamLanguage, LanguageSupport } from "@codemirror/language";

const gitignore = {
  name: "gitignore",
  token(stream) {
    if (stream.sol()) {
      if (stream.match(/^\s*#.*/)) return "comment";
      if (stream.match("!")) return "operator";
    }
    if (stream.eatSpace()) return null;
    if (stream.match(/^\\./)) return "escape";
    if (stream.match(/^\*\*|^[*?]/)) return "keyword";
    if (stream.match(/^\[[^\]]*\]/)) return "regexp";
    // .gitattributes 的属性：-text、eol=lf、!diff
    if (stream.string.slice(0, stream.start).trim() && stream.match(/^[-!]?[\w.-]+(?:=\S*)?/)) return "attributeName";
    if (stream.match(/^\/|^[^\s*?[\\/]+/)) return null;
    stream.next();
    return null;
  },
  languageData: { commentTokens: { line: "#" } },
};

export default function () {
  return new LanguageSupport(StreamLanguage.define(gitignore));
}
