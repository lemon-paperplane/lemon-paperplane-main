// Git 提交信息（COMMIT_EDITMSG 等）与 git-rebase-todo：标题行、注释、尾注、变基指令。
import { StreamLanguage, LanguageSupport } from "@codemirror/language";

const REBASE = /^(?:p|pick|r|reword|e|edit|s|squash|f|fixup|x|exec|b|break|d|drop|l|label|t|reset|m|merge|u|update-ref)\b/;

const gitcommit = {
  name: "gitcommit",
  startState: () => ({ line: 0 }),
  blankLine(state) { state.line++; },
  token(stream, state) {
    if (stream.sol()) {
      state.line++;
      if (stream.match(/^#.*/)) return "comment";
      if (stream.match(REBASE)) return "keyword";
      if (stream.match(/^[A-Z][\w-]*(?=: )/)) return "attributeName"; // Signed-off-by: 等尾注
      if (state.line === 1) {
        // 标题行：超过 72 个字符的部分标出来
        if (stream.match(/^.{0,72}/)) return "heading";
        stream.skipToEnd();
        return "invalid";
      }
    }
    if (stream.match(/^\b[0-9a-f]{7,40}\b/)) return "number";
    stream.skipToEnd();
    return null;
  },
  languageData: { commentTokens: { line: "#" } },
};

export default function () {
  return new LanguageSupport(StreamLanguage.define(gitcommit));
}
