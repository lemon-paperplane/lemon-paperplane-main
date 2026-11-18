// 各插件 editor/ 目录下的语言与快捷键模块清单（scripts/build-lang.mjs 读取）。
// plugin：插件文件夹名去掉 lemon-paperplane-plugin- 前缀；file：相对插件目录；source：模块入口源码。
// 模块形如 export default function (cm, commands) { return Extension }，@codemirror/* 与 @lezer/* 运行时改写为读 window.cm。

/** @codemirror/lang-* 包：import { fn } from pkg; return fn(args) */
const pkg = (name, fn, args = "") =>
  `import { ${fn} } from "${name}";\nexport default function () { return ${fn}(${args}); }\n`;

/** legacy-modes：StreamLanguage.define(mode) 包成 LanguageSupport */
const legacy = (mode, exp = mode) =>
  `import { ${exp} } from "@codemirror/legacy-modes/mode/${mode}";\n` +
  `import { StreamLanguage, LanguageSupport } from "@codemirror/language";\n` +
  `export default function () { return new LanguageSupport(StreamLanguage.define(${exp})); }\n`;

/** 编辑器工程 lang/ 下自己写的模式 */
const local = (file) => `export { default } from "./lang/${file}";\n`;

export const MODULES = [
  // ---- 语言插件自带的着色
  { plugin: "java", file: "editor/lang-java.js", source: pkg("@codemirror/lang-java", "java") },
  { plugin: "kotlin", file: "editor/lang-kotlin.js", source: legacy("clike", "kotlin") },
  { plugin: "gradle", file: "editor/lang-groovy.js", source: legacy("groovy") },
  { plugin: "nodejs", file: "editor/lang-javascript.js", source: pkg("@codemirror/lang-javascript", "javascript", "{ jsx: true }") },
  { plugin: "nodejs", file: "editor/lang-typescript.js", source: pkg("@codemirror/lang-javascript", "javascript", "{ typescript: true }") },
  { plugin: "nodejs", file: "editor/lang-tsx.js", source: pkg("@codemirror/lang-javascript", "javascript", "{ jsx: true, typescript: true }") },
  { plugin: "nodejs", file: "editor/lang-json.js", source: pkg("@codemirror/lang-json", "json") },
  { plugin: "nodejs", file: "editor/lang-jsonc.js", source: local("jsonc.js") },
  { plugin: "vue", file: "editor/lang-vue.js", source: pkg("@codemirror/lang-vue", "vue") },
  { plugin: "uniapp", file: "editor/lang-vue.js", source: pkg("@codemirror/lang-vue", "vue") },
  { plugin: "uniapp", file: "editor/lang-jsonc.js", source: local("jsonc.js") },
  { plugin: "uniapp", file: "editor/lang-uts.js", source: pkg("@codemirror/lang-javascript", "javascript", "{ typescript: true }") },
  { plugin: "python", file: "editor/lang-python.js", source: pkg("@codemirror/lang-python", "python") },
  { plugin: "go", file: "editor/lang-go.js", source: pkg("@codemirror/lang-go", "go") },
  { plugin: "go", file: "editor/lang-gomod.js", source: local("gomod.js") },
  { plugin: "go", file: "editor/lang-gosum.js", source: local("gosum.js") },
  { plugin: "git", file: "editor/lang-gitignore.js", source: local("gitignore.js") },
  { plugin: "git", file: "editor/lang-diff.js", source: legacy("diff") },
  { plugin: "git", file: "editor/lang-gitcommit.js", source: local("gitcommit.js") },
  { plugin: "git", file: "editor/lang-gitconfig.js", source: legacy("properties") },

  // ---- 通用格式（lemon-paperplane-plugin-languages）
  {
    plugin: "languages", file: "editor/lang-markdown.js",
    // 代码块按语言名懒加载其他插件的着色（cm.paperplane.codeLanguages）
    source: `import { markdown } from "@codemirror/lang-markdown";\nexport default function (cm) { return markdown({ codeLanguages: (cm && cm.paperplane && cm.paperplane.codeLanguages) || [] }); }\n`,
  },
  { plugin: "languages", file: "editor/lang-yaml.js", source: pkg("@codemirror/lang-yaml", "yaml") },
  { plugin: "languages", file: "editor/lang-xml.js", source: pkg("@codemirror/lang-xml", "xml") },
  { plugin: "languages", file: "editor/lang-html.js", source: pkg("@codemirror/lang-html", "html") },
  { plugin: "languages", file: "editor/lang-css.js", source: pkg("@codemirror/lang-css", "css") },
  { plugin: "languages", file: "editor/lang-scss.js", source: pkg("@codemirror/lang-sass", "sass") },
  { plugin: "languages", file: "editor/lang-sass.js", source: pkg("@codemirror/lang-sass", "sass", "{ indented: true }") },
  { plugin: "languages", file: "editor/lang-less.js", source: pkg("@codemirror/lang-less", "less") },
  { plugin: "languages", file: "editor/lang-shell.js", source: legacy("shell") },
  { plugin: "languages", file: "editor/lang-sql.js", source: pkg("@codemirror/lang-sql", "sql") },
  { plugin: "languages", file: "editor/lang-dockerfile.js", source: legacy("dockerfile", "dockerFile") },
  { plugin: "languages", file: "editor/lang-toml.js", source: legacy("toml") },
  { plugin: "languages", file: "editor/lang-properties.js", source: legacy("properties") },
  { plugin: "languages", file: "editor/lang-makefile.js", source: local("makefile.js") },
  { plugin: "languages", file: "editor/lang-nginx.js", source: legacy("nginx") },
  { plugin: "languages", file: "editor/lang-protobuf.js", source: legacy("protobuf") },
  { plugin: "languages", file: "editor/lang-rust.js", source: pkg("@codemirror/lang-rust", "rust") },
  { plugin: "languages", file: "editor/lang-cpp.js", source: pkg("@codemirror/lang-cpp", "cpp") },
  { plugin: "languages", file: "editor/lang-csharp.js", source: legacy("clike", "csharp") },
  { plugin: "languages", file: "editor/lang-swift.js", source: legacy("swift") },
  { plugin: "languages", file: "editor/lang-php.js", source: pkg("@codemirror/lang-php", "php") },
  { plugin: "languages", file: "editor/lang-ruby.js", source: legacy("ruby") },
  { plugin: "languages", file: "editor/lang-lua.js", source: legacy("lua") },
  { plugin: "languages", file: "editor/lang-dart.js", source: legacy("clike", "dart") },
  { plugin: "languages", file: "editor/lang-scala.js", source: legacy("clike", "scala") },
  { plugin: "languages", file: "editor/lang-objectivec.js", source: legacy("clike", "objectiveC") },
  { plugin: "languages", file: "editor/lang-perl.js", source: legacy("perl") },
  { plugin: "languages", file: "editor/lang-powershell.js", source: legacy("powershell", "powerShell") },
  { plugin: "languages", file: "editor/lang-r.js", source: legacy("r") },
  { plugin: "languages", file: "editor/lang-cmake.js", source: legacy("cmake") },
  { plugin: "languages", file: "editor/lang-haskell.js", source: legacy("haskell") },
  { plugin: "languages", file: "editor/lang-clojure.js", source: legacy("clojure") },
  { plugin: "languages", file: "editor/lang-erlang.js", source: legacy("erlang") },
  { plugin: "languages", file: "editor/lang-julia.js", source: legacy("julia") },
  { plugin: "languages", file: "editor/lang-http.js", source: legacy("http") },
  { plugin: "languages", file: "editor/lang-vb.js", source: legacy("vb") },
  { plugin: "languages", file: "editor/lang-pascal.js", source: legacy("pascal") },
  { plugin: "languages", file: "editor/lang-fortran.js", source: legacy("fortran") },
  { plugin: "languages", file: "editor/lang-verilog.js", source: legacy("verilog") },
  { plugin: "languages", file: "editor/lang-vhdl.js", source: legacy("vhdl") },
  { plugin: "languages", file: "editor/lang-tcl.js", source: legacy("tcl") },
  { plugin: "languages", file: "editor/lang-elm.js", source: legacy("elm") },
  { plugin: "languages", file: "editor/lang-crystal.js", source: legacy("crystal") },
  { plugin: "languages", file: "editor/lang-d.js", source: legacy("d") },
  { plugin: "languages", file: "editor/lang-coffeescript.js", source: legacy("coffeescript", "coffeeScript") },
  { plugin: "languages", file: "editor/lang-pug.js", source: legacy("pug") },
  { plugin: "languages", file: "editor/lang-stylus.js", source: legacy("stylus") },
  { plugin: "languages", file: "editor/lang-gherkin.js", source: legacy("gherkin") },
  { plugin: "languages", file: "editor/lang-puppet.js", source: legacy("puppet") },
  { plugin: "languages", file: "editor/lang-latex.js", source: legacy("stex") },
  { plugin: "languages", file: "editor/lang-jinja2.js", source: legacy("jinja2") },
  { plugin: "languages", file: "editor/lang-nsis.js", source: legacy("nsis") },

  // ---- 快捷键方案（模式编辑）
  {
    plugin: "keymap-vim", file: "editor/keymap-vim.js",
    source: `import { vim, Vim } from "@replit/codemirror-vim";
let defined = false;
export default function (cm, commands) {
  if (!defined && commands) {
    defined = true;
    const run = (id) => (adapter) => commands.run(id, adapter && adapter.cm6 ? adapter.cm6 : adapter);
    Vim.defineEx("write", "w", run("save"));
    Vim.defineEx("wall", "wa", run("saveAll"));
    Vim.defineEx("quit", "q", run("closeTab"));
    Vim.defineEx("wq", "wq", (a) => { commands.run("save", a.cm6); commands.run("closeTab", a.cm6); });
    Vim.defineEx("xit", "x", (a) => { commands.run("save", a.cm6); commands.run("closeTab", a.cm6); });
    Vim.defineEx("edit", "e", run("quickOpen"));
  }
  return vim({ status: true });
}
`,
  },
  {
    plugin: "keymap-emacs", file: "editor/keymap-emacs.js",
    source: `import { emacs, EmacsHandler } from "@replit/codemirror-emacs";
let defined = false;
export default function (cm, commands) {
  if (!defined && commands) {
    defined = true;
    const bind = (keys, id) => EmacsHandler.bindKey(keys, (view) => commands.run(id, view));
    bind("C-x C-s", "save");
    bind("C-x s", "saveAll");
    bind("C-x C-f", "quickOpen");
    bind("C-x b", "quickSwitchTab");
    bind("C-x k", "closeTab");
    bind("M-g g", "goToLine");
    bind("M-g M-g", "goToLine");
    bind("C-x o", "nextTab");
    bind("M-%", "replace");
    bind("C-x C-c", "closeAllTabs");
  }
  return emacs();
}
`,
  },
];
