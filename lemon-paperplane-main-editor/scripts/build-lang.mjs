#!/usr/bin/env node
// 把 npm 上的语言包 / 快捷键包打成编辑器插件模块（docs/editor.md 第 4 节）。
//
// 模块是单文件 ES module：export default function (cm, commands) { return Extension }。
// @codemirror/{state,view,language,commands,search,autocomplete} 与 @lezer/{common,lr,highlight}
// 不打进模块，import 改写为读取编辑器提供的 window.cm 共享命名空间（CodeMirror 要求单实例）；
// 语法包（@codemirror/lang-*、@codemirror/legacy-modes、@lezer/<语言>）照常打进模块。
// 模块不能有相对 import（插件文件经带 token 的地址提供），所以一律单文件、不分块。
//
// 用法：
//   node scripts/build-lang.mjs                       按 scripts/lang-modules.mjs 构建全部内置模块到各插件目录
//   node scripts/build-lang.mjs java nodejs           只构建这些插件的模块
//   node scripts/build-lang.mjs --root <目录>          插件文件夹所在目录（默认 lemon-paperplane/）
//   node scripts/build-lang.mjs --entry <入口.js> --out <插件>/editor/lang-x.js
//   node scripts/build-lang.mjs --package @codemirror/lang-rust --export rust [--args "{...}"] --out <插件>/editor/lang-rust.js
//   node scripts/build-lang.mjs --legacy shell [--export shell] --out <插件>/editor/lang-shell.js
import * as esbuild from "esbuild";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { MODULES } from "./lang-modules.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, "..");

/** 共享运行时包 → window.cm 上的路径 */
export const SHARED = {
  "@codemirror/state": "state",
  "@codemirror/view": "view",
  "@codemirror/language": "language",
  "@codemirror/commands": "commands",
  "@codemirror/search": "search",
  "@codemirror/autocomplete": "autocomplete",
  "@codemirror/streamparser": "legacy",
  "@lezer/common": "lezer.common",
  "@lezer/lr": "lezer.lr",
  "@lezer/highlight": "lezer.highlight",
};

/** 可以打进模块的 CodeMirror/Lezer 包（语法与语言数据，不是单例） */
const BUNDLABLE = /^@codemirror\/(lang-[\w-]+|legacy-modes)(\/|$)|^@lezer\/(?!common$|lr$|highlight$)[\w-]+$/;

const sharedPlugin = {
  name: "paperplane-shared-cm",
  setup(b) {
    b.onResolve({ filter: /^@(codemirror|lezer)\// }, (args) => {
      if (SHARED[args.path]) return { path: args.path, namespace: "pp-shared" };
      if (BUNDLABLE.test(args.path)) return undefined;
      return { errors: [{ text: `${args.path} 是 CodeMirror 运行时包，编辑器的 window.cm 没有提供，模块不能自带一份` }] };
    });
    b.onLoad({ filter: /.*/, namespace: "pp-shared" }, (args) => {
      const p = SHARED[args.path].split(".").map((k) => `[${JSON.stringify(k)}]`).join("");
      // 以 CommonJS 形式导出，esbuild 把具名 import 变成属性访问（运行时读取，不需要列出导出名）
      return {
        contents: `var cm = globalThis.cm; if (!cm || !cm${p}) throw new Error(${JSON.stringify("Lemon Paperplane editor: window.cm." + SHARED[args.path] + " is not available")}); module.exports = cm${p};`,
        loader: "js",
      };
    });
  },
};

export async function buildModule({ source, entry, out, minify = true }) {
  const opts = {
    absWorkingDir: ROOT,
    bundle: true,
    format: "esm",
    platform: "browser",
    target: ["safari15", "chrome100"],
    minify,
    legalComments: "none",
    charset: "utf8",
    write: false,
    outfile: out,
    plugins: [sharedPlugin],
    logLevel: "silent",
    banner: { js: "// Lemon Paperplane editor module. Built by lemon-paperplane-main-editor/scripts/build-lang.mjs" },
  };
  if (source != null) opts.stdin = { contents: source, resolveDir: ROOT, sourcefile: path.basename(out), loader: "js" };
  else opts.entryPoints = [path.resolve(entry)];
  const r = await esbuild.build(opts);
  const code = r.outputFiles[0].text;
  if (/(^|[^.\w])import\s*\(|from\s*["']\.{0,2}\//.test(code.replace(/\/\/.*$/gm, ""))) {
    throw new Error(`${out}: 模块里还有 import，必须是单文件`);
  }
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, code);
  return code.length;
}

function parseArgs(argv) {
  const o = { only: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith("--")) o[a.slice(2)] = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : true;
    else o.only.push(a);
  }
  return o;
}

async function main() {
  const o = parseArgs(process.argv.slice(2));
  if (o.out) {
    const out = path.resolve(o.out);
    let source;
    if (o.package) {
      const fn = o.export || "default";
      source = fn === "default"
        ? `import f from "${o.package}";\nexport default function () { return f(${o.args ?? ""}); }\n`
        : `import { ${fn} } from "${o.package}";\nexport default function () { return ${fn}(${o.args ?? ""}); }\n`;
    } else if (o.legacy) {
      const exp = o.export || o.legacy;
      source = `import { ${exp} } from "@codemirror/legacy-modes/mode/${o.legacy}";\nimport { StreamLanguage, LanguageSupport } from "@codemirror/language";\nexport default function () { return new LanguageSupport(StreamLanguage.define(${exp})); }\n`;
    }
    const n = await buildModule({ source, entry: o.entry, out });
    console.log(`${path.relative(process.cwd(), out)}  ${(n / 1024).toFixed(1)} KB`);
    return;
  }
  const root = path.resolve(o.root || path.resolve(ROOT, "../.."));
  const list = MODULES.filter((m) => !o.only.length || o.only.includes(m.plugin));
  if (!list.length) throw new Error("没有匹配的模块：" + o.only.join(", "));
  let total = 0;
  for (const m of list) {
    const dir = path.join(root, "lemon-paperplane-plugin-" + m.plugin);
    if (!fs.existsSync(dir)) { console.warn(`跳过：${dir} 不存在`); continue; }
    const out = path.join(dir, m.file);
    const n = await buildModule({ source: m.source, out });
    total += n;
    console.log(`${m.plugin.padEnd(14)} ${m.file.padEnd(32)} ${(n / 1024).toFixed(1).padStart(7)} KB`);
  }
  console.log(`共 ${list.length} 个模块，${(total / 1024).toFixed(1)} KB`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((e) => { console.error(e.errors?.map((x) => x.text).join("\n") || e); process.exit(1); });
}
