// Makefile：目标、变量、指令、注释、配方行。
import { StreamLanguage, LanguageSupport } from "@codemirror/language";

const KEYWORDS = /^(?:ifeq|ifneq|ifdef|ifndef|else|endif|include|-include|sinclude|define|endef|export|unexport|override|private|vpath)\b/;

const makefile = {
  name: "makefile",
  startState: () => ({ recipe: false }),
  token(stream, state) {
    if (stream.sol()) {
      state.recipe = stream.peek() === "\t";
      if (!state.recipe && stream.match(/^\s*#.*/)) return "comment";
      if (!state.recipe && stream.match(/^[\w./%$(){}-]+(?=\s*::?(?!=))/)) return "variableName.function";
      if (!state.recipe && stream.match(/^\s*[\w.-]+(?=\s*(?:[:+?!]?=))/)) return "variableName";
    }
    if (stream.eatSpace()) return null;
    if (stream.match(/^\$[({][^)}]*[)}]/) || stream.match(/^\$[@<^?*%+|$]/)) return "variableName";
    if (!state.recipe && stream.match(KEYWORDS)) return "keyword";
    if (stream.match(/^#.*/)) return "comment";
    if (stream.match(/^"(?:[^"\\]|\\.)*"?/) || stream.match(/^'[^']*'?/)) return "string";
    if (!state.recipe && stream.match(/^(?:::?=|[+?!]?=|::?)/)) return "operator";
    if (state.recipe && stream.match(/^@/)) return "meta";
    stream.next();
    return null;
  },
  languageData: { commentTokens: { line: "#" } },
};

export default function () {
  return new LanguageSupport(StreamLanguage.define(makefile));
}
