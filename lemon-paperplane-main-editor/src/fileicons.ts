// 文件类型图标（shared/icons 里的通用图标，按扩展名粗分）。
import { icon } from "./ui";
import { basename, extname } from "./util";

const CODE = new Set([".java", ".kt", ".kts", ".groovy", ".gradle", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".nvue", ".uvue", ".py", ".go", ".rs", ".c", ".h", ".cc", ".cpp", ".hpp", ".cs", ".swift", ".php", ".rb", ".lua", ".dart", ".scala", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".xml", ".svg", ".sql", ".proto"]);
const DATA = new Set([".json", ".json5", ".jsonc", ".yaml", ".yml", ".toml"]);
const CONFIG = new Set([".properties", ".ini", ".conf", ".cfg", ".env", ".editorconfig", ".gitignore", ".gitattributes", ".npmrc", ".nvmrc"]);
const SHELL = new Set([".sh", ".bash", ".zsh", ".fish", ".bat", ".cmd", ".ps1"]);
const IMAGE = new Set([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".icns", ".tiff"]);
const ARCHIVE = new Set([".zip", ".jar", ".war", ".tar", ".gz", ".tgz", ".7z", ".rar", ".aar"]);

export function fileIconName(path: string, kind?: string): string {
  const name = basename(path).toLowerCase();
  const ext = extname(path);
  if (kind === "binary" || kind === "tooLarge") return IMAGE.has(ext) ? "file-image" : ARCHIVE.has(ext) ? "file-archive" : "binary";
  if (IMAGE.has(ext)) return "file-image";
  if (ARCHIVE.has(ext)) return "file-archive";
  if (CODE.has(ext)) return "file-code-2";
  if (DATA.has(ext)) return "file-braces";
  if (SHELL.has(ext) || name === "makefile" || name === "dockerfile") return "file-terminal";
  if (CONFIG.has(ext) || name.startsWith(".env") || name.startsWith(".")) return "file-cog";
  if (ext === ".diff" || ext === ".patch") return "file-diff";
  if (ext === ".md" || ext === ".txt" || ext === ".log" || !ext) return "file-text";
  return "file";
}

export function fileIcon(path: string, kind?: string): SVGSVGElement {
  return icon(fileIconName(path, kind), 16, "pp-file-icon");
}
