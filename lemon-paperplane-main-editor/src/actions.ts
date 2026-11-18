// 跨模块的小动作：复制路径、在访达中显示、在文件树中定位。
import { app } from "./app";
import { post, hasHost } from "./bridge";
import { toast } from "./ui";
import { t } from "./i18n";
import { isWindows } from "./util";

export async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.style.position = "fixed";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.select();
    document.execCommand("copy");
    ta.remove();
  }
  toast("success", t("common.copied"));
}

export function absolutePath(rel: string): string {
  const root = app.project?.path ?? "";
  if (!root) return rel;
  const sep = isWindows || root.includes("\\") ? "\\" : "/";
  return root.replace(/[\\/]+$/, "") + (rel ? sep + rel.split("/").join(sep) : "");
}

/** 在访达/资源管理器中显示（路径相对工程根，宿主负责拼绝对路径） */
export function revealPath(rel: string) {
  if (!hasHost) {
    void copyText(absolutePath(rel));
    return;
  }
  post({ type: "reveal", path: rel });
}

let locateImpl: (path: string) => void = () => {};
export function setLocateImpl(f: (path: string) => void) {
  locateImpl = f;
}
export function locateInTree(path: string) {
  locateImpl(path);
}
