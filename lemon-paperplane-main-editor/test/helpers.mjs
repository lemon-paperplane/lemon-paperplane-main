// 测试公用：路径、命令表解析、插件清单。
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
export const PLUGINS_ROOT = path.resolve(ROOT, "../..");

/** COMMANDS.md 表格第一列的命令 id */
export function documentedCommands() {
  const md = fs.readFileSync(path.join(ROOT, "COMMANDS.md"), "utf8");
  return new Set([...md.matchAll(/^\| `([A-Za-z]+)` \|/gm)].map((m) => m[1]));
}

/** 源码里的命令：commands.ts 的命令表 + main.ts 的 registerCommands */
export function sourceCommands() {
  const ids = new Set();
  const take = (file, from, to) => {
    const s = fs.readFileSync(path.join(ROOT, "src", file), "utf8");
    const body = s.slice(s.indexOf(from), s.indexOf(to));
    for (const m of body.matchAll(/^ {2}(\w+):/gm)) ids.add(m[1]);
  };
  take("commands.ts", "export const commands: Record<string, Command> = {", "/** 界面级命令由 app 注册");
  take("main.ts", "registerCommands({", "// 焦点不在编辑区");
  return ids;
}

export function pluginDirs() {
  return fs.readdirSync(PLUGINS_ROOT).filter((n) => n.startsWith("lemon-paperplane-plugin-"))
    .map((n) => path.join(PLUGINS_ROOT, n)).filter((d) => fs.existsSync(path.join(d, "plugin.json")));
}

export function manifest(dir) {
  return JSON.parse(fs.readFileSync(path.join(dir, "plugin.json"), "utf8"));
}
