// go.sum：模块路径、版本、h1: 校验和。
import { StreamLanguage, LanguageSupport } from "@codemirror/language";

const gosum = {
  name: "gosum",
  token(stream) {
    if (stream.eatSpace()) return null;
    if (stream.sol() && stream.match(/^\S+/)) return "variableName";
    if (stream.match(/^v\d+\.\d+\.\d+[^\s/]*(?:\/go\.mod)?/)) return "number";
    if (stream.match(/^h\d+:/)) return "keyword";
    if (stream.match(/^\S+/)) return "string";
    stream.next();
    return null;
  },
};

export default function () {
  return new LanguageSupport(StreamLanguage.define(gosum));
}
