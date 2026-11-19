#!/usr/bin/env node
// 编辑器网页构建：生成设计变量与图标 → esbuild 打包（代码分割，语言/对比等懒加载）→ 拷贝文案 → 写 index.html。
// 用法：node scripts/build.mjs [--watch] [--no-minify]
import * as esbuild from "esbuild";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, "..");
const SHARED = path.resolve(ROOT, "../shared");
const SRC = path.join(ROOT, "src");
const GEN = path.join(SRC, "gen");
const DIST = path.join(ROOT, "dist");
const watch = process.argv.includes("--watch");
const minify = !process.argv.includes("--no-minify");

fs.mkdirSync(GEN, { recursive: true });

// ---------------------------------------------------------------------------
// 1. 设计变量：shared/design-tokens.json → CSS 变量 + TS 常量

// 代码着色到设计变量的映射（参照 IntelliJ Light / Dark 的色相，取值一律来自 design-tokens.json）
const SYNTAX = {
  text:       { light: "textPrimary",   dark: "textPrimary" },
  keyword:    { light: "ansi:4",        dark: "graphLane6" },
  string:     { light: "graphLane2",    dark: "graphLane2" },
  number:     { light: "graphLane0",    dark: "graphLane5" },
  comment:    { light: "textTertiary",  dark: "textTertiary" },
  function:   { light: "graphLane5",    dark: "info" },
  property:   { light: "graphLane4",    dark: "ansi:5" },
  type:       { light: "textPrimary",   dark: "textPrimary" },
  meta:       { light: "graphLane3",    dark: "graphLane3" },
  tag:        { light: "ansi:4",        dark: "graphLane3" },
  attribute:  { light: "graphLane0",    dark: "ansi:7" },
  regexp:     { light: "graphLane7",    dark: "graphLane7" },
  escape:     { light: "graphLane6",    dark: "graphLane6" },
  heading:    { light: "accent",        dark: "info" },
  link:       { light: "textLink",      dark: "textLink" },
  punctuation:{ light: "textSecondary", dark: "textSecondary" },
  inserted:   { light: "diffAddedText", dark: "diffAddedText" },
  deleted:    { light: "diffRemovedText", dark: "diffRemovedText" },
  changed:    { light: "warning",       dark: "warning" },
  invalid:    { light: "danger",        dark: "danger" },
};

function genTokens() {
  const t = JSON.parse(fs.readFileSync(path.join(SHARED, "design-tokens.json"), "utf8"));
  const css = [];
  css.push("/* 由 scripts/build.mjs 从 shared/design-tokens.json 生成，不要手改 */");
  css.push(":root {");
  for (const [k, v] of Object.entries(t.spacing)) css.push(`  --sp-${k}: ${v}px;`);
  for (const [k, v] of Object.entries(t.radius)) css.push(`  --r-${k}: ${v}px;`);
  for (const [k, v] of Object.entries(t.size)) css.push(`  --size-${k}: ${v}px;`);
  for (const [k, v] of Object.entries(t.typography)) {
    css.push(`  --fs-${k}: ${v.size}px;`, `  --fw-${k}: ${v.weight};`, `  --lh-${k}: ${v.lineHeight}px;`);
  }
  for (const [k, v] of Object.entries(t.motion)) css.push(`  --motion-${k}: ${v}ms;`);
  css.push("}");
  for (const scheme of ["light", "dark"]) {
    css.push(`[data-theme="${scheme}"] {`);
    css.push(`  color-scheme: ${scheme};`);
    for (const [k, v] of Object.entries(t.color[scheme])) css.push(`  --c-${k}: ${v};`);
    const mul = scheme === "dark" ? 2 : 1;
    for (const [k, s] of Object.entries(t.shadow)) {
      const a = Math.min(1, s.opacity * mul).toFixed(2);
      css.push(`  --shadow-${k}: ${s.x}px ${s.y}px ${s.blur}px rgba(0,0,0,${a});`);
    }
    // 代码着色（Paperplane Light / Dark）：全部取自设计变量的颜色
    for (const [k, ref] of Object.entries(SYNTAX)) {
      const r = ref[scheme];
      const v = r.startsWith("ansi:") ? t.ansi[scheme][+r.slice(5)] : t.color[scheme][r];
      if (!v) throw new Error(`代码着色引用了不存在的设计变量 ${r}`);
      css.push(`  --syn-${k}: ${v};`);
    }
    css.push("}");
  }
  fs.writeFileSync(path.join(GEN, "tokens.css"), css.join("\n") + "\n");
  const ts = `// 由 scripts/build.mjs 从 shared/design-tokens.json 生成，不要手改
export const colors = ${JSON.stringify(t.color)} as const;
export const ansi = ${JSON.stringify(t.ansi)} as const;
export const fonts = ${JSON.stringify(t.font)} as const;
export const typography = ${JSON.stringify(t.typography)} as const;
export const motion = ${JSON.stringify(t.motion)} as const;
export type ColorKey = keyof typeof colors.light;
`;
  fs.writeFileSync(path.join(GEN, "tokens.ts"), ts);
}

// ---------------------------------------------------------------------------
// 2. 图标：只内联源码里用到的 shared/icons。icon("x") / iconButton("x") / icon: "x" 引用的名字必须存在；
//    另外凡是与图标同名的字符串字面量也收进来（覆盖 fileIconName() 这类按条件返回名字的写法）。
function genIcons() {
  const dir = path.join(SHARED, "icons");
  const available = new Set(fs.readdirSync(dir).filter((f) => f.endsWith(".svg")).map((f) => f.slice(0, -4)));
  const names = new Set();
  const strict = /\b(?:icon|ic|iconButton)\(\s*["']([a-z0-9-]+)["']|\bicon:\s*["']([a-z0-9-]+)["']/g;
  const loose = /["']([a-z][a-z0-9-]*)["']/g;
  for (const f of walk(SRC)) {
    if (!f.endsWith(".ts") || f.startsWith(GEN)) continue;
    const s = fs.readFileSync(f, "utf8");
    for (const m of s.matchAll(strict)) {
      const n = m[1] || m[2];
      if (!available.has(n)) throw new Error(`缺图标 shared/icons/${n}.svg（${path.relative(ROOT, f)}）`);
      names.add(n);
    }
    for (const m of s.matchAll(loose)) if (available.has(m[1])) names.add(m[1]);
  }
  const out = {};
  for (const n of [...names].sort()) {
    const svg = fs.readFileSync(path.join(dir, n + ".svg"), "utf8");
    out[n] = svg.replace(/^[\s\S]*?<svg[^>]*>/, "").replace(/<\/svg>\s*$/, "").replace(/\s+/g, " ").trim();
  }
  fs.writeFileSync(path.join(GEN, "icons.ts"),
    `// 由 scripts/build.mjs 从 shared/icons 生成（只含用到的图标），不要手改\nexport const ICONS: Record<string, string> = ${JSON.stringify(out, null, 1)};\n`);
  return names.size;
}

// ---------------------------------------------------------------------------
// 3. 文案：shared/i18n 里 editor.* 与少量 common.* → dist/i18n/<locale>.json；
//    Windows 的说法（shared/i18n/windows：废纸篓 → 回收站等）→ dist/i18n/windows/<locale>.json，网页在 Windows 上叠加
const COMMON_KEYS = ["common.cancel", "common.close", "common.copy", "common.copied", "common.create", "common.delete", "common.ok", "common.save", "common.saved", "common.failed", "common.more", "common.name", "common.filter"];
function copyI18n() {
  copyI18nDir(path.join(SHARED, "i18n"), path.join(DIST, "i18n"));
  copyI18nDir(path.join(SHARED, "i18n", "windows"), path.join(DIST, "i18n", "windows"));
}

function copyI18nDir(dir, out) {
  if (!fs.existsSync(dir)) return;
  fs.mkdirSync(out, { recursive: true });
  for (const f of fs.readdirSync(dir)) {
    if (!f.endsWith(".json")) continue;
    const all = JSON.parse(fs.readFileSync(path.join(dir, f), "utf8"));
    const sub = {};
    for (const [k, v] of Object.entries(all)) {
      if (k.startsWith("editor.") || COMMON_KEYS.includes(k)) sub[k] = v;
    }
    fs.writeFileSync(path.join(out, f), JSON.stringify(sub));
  }
}

function* walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) yield* walk(p);
    else yield p;
  }
}

// ---------------------------------------------------------------------------
// 4. 打包
function writeHtml(meta) {
  writePage(meta, "src/main.ts", "index.html");
  writePage(meta, "src/snippet.ts", "snippet.html");
  writePage(meta, "src/terminal.ts", "terminal.html");
}

// 每个入口一张页面：入口 JS、它自己的 CSS（cssBundle）、首屏要预加载的共享块
function writePage(meta, entry, page) {
  const outs = Object.entries(meta.outputs);
  const js = outs.find(([, o]) => o.entryPoint === entry)?.[0];
  const css = meta.outputs[js]?.cssBundle;
  const rel = (f) => path.relative(DIST, path.resolve(ROOT, f)).split(path.sep).join("/");
  let html = fs.readFileSync(path.join(SRC, page), "utf8");
  html = html.replace("<!--CSS-->", css ? `<link rel="stylesheet" href="${rel(css)}">` : "")
    .replace("<!--JS-->", `<script type="module" src="${rel(js)}"></script>`);
  // 首屏要用的同步 chunk 预加载，避免瀑布
  const pre = (meta.outputs[js]?.imports || []).filter((i) => i.kind === "import-statement")
    .map((i) => `<link rel="modulepreload" href="${rel(i.path)}">`).join("");
  html = html.replace("<!--PRELOAD-->", pre);
  fs.writeFileSync(path.join(DIST, page), html);
}

function report(meta) {
  let first = 0, firstGz = 0, lazy = 0;
  const js = Object.entries(meta.outputs).find(([, o]) => o.entryPoint === "src/main.ts")?.[0];
  const firstSet = new Set([js]);
  for (const i of meta.outputs[js].imports) if (i.kind === "import-statement") firstSet.add(i.path);
  const lines = [];
  for (const [f, o] of Object.entries(meta.outputs)) {
    if (f.endsWith(".map")) continue;
    const buf = fs.readFileSync(path.resolve(ROOT, f));
    const gz = zlib.gzipSync(buf, { level: 9 }).length;
    const isFirst = firstSet.has(f) || f.endsWith(".css");
    if (isFirst) { first += buf.length; firstGz += gz; } else lazy += buf.length;
    lines.push(`  ${isFirst ? "首屏" : "懒加载"} ${path.basename(f)}  ${(buf.length / 1024).toFixed(1)} KB（gzip ${(gz / 1024).toFixed(1)} KB）`);
  }
  console.log(lines.sort().join("\n"));
  console.log(`首屏合计 ${(first / 1024).toFixed(1)} KB（gzip ${(firstGz / 1024).toFixed(1)} KB），懒加载 ${(lazy / 1024).toFixed(1)} KB`);
}

async function build() {
  genTokens();
  const n = genIcons();
  if (process.argv.includes("--gen-only")) return;
  fs.rmSync(DIST, { recursive: true, force: true });
  fs.mkdirSync(DIST, { recursive: true });
  copyI18n();
  const opts = {
    absWorkingDir: ROOT,
    entryPoints: ["src/main.ts", "src/snippet.ts", "src/terminal.ts"],
    bundle: true,
    format: "esm",
    splitting: true,
    minify,
    sourcemap: false,
    target: ["safari15", "chrome100"],
    outdir: "dist/assets",
    entryNames: "[name]-[hash]",
    chunkNames: "[name]-[hash]",
    assetNames: "[name]-[hash]",
    metafile: true,
    legalComments: "none",
    charset: "utf8",
    logLevel: "warning",
  };
  if (watch) {
    const ctx = await esbuild.context({ ...opts, plugins: [{ name: "html", setup(b) { b.onEnd((r) => { if (r.metafile) { writeHtml(r.metafile); copyI18n(); console.log("rebuilt", new Date().toLocaleTimeString()); } }); } }] });
    await ctx.watch();
    console.log("watching…");
    return;
  }
  const r = await esbuild.build(opts);
  writeHtml(r.metafile);
  const cache = path.join(ROOT, "node_modules/.cache");
  fs.mkdirSync(cache, { recursive: true });
  fs.writeFileSync(path.join(cache, "editor-meta.json"), JSON.stringify(r.metafile));
  console.log(`图标 ${n} 个`);
  report(r.metafile);
}

build().catch((e) => { console.error(e); process.exit(1); });
