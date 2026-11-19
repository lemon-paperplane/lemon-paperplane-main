#!/bin/bash
# 构建 Windows 版发布包（在 Mac 上执行）：
#   1. 交叉编译内核与内置插件（x64 + ARM64）
#   2. 构建编辑器网页（编辑窗口、终端用）
#   3. 传到 Windows 编译机，编译界面、打免安装 zip 和安装包（lemon-paperplane-main-windows/package.ps1）
#   4. 取回 dist/windows/
#
# 用法：WINDOWS_BUILD_HOST=user@host scripts/build-windows.sh [版本，默认 0.1.0]
#   WINDOWS_BUILD_DIR  编译机上的工作目录（相对用户主目录或绝对路径，默认 paperplane-build）
# 编译机需要：OpenSSH 服务、Visual Studio Build Tools（MSBuild）或挂载的 EWDK、Inno Setup 6；ssh 免密登录。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MAIN="$(cd "$HERE/.." && pwd)"
VERSION="${1:-0.1.0}"
HOST="${WINDOWS_BUILD_HOST:?设置 WINDOWS_BUILD_HOST=user@host}"
RDIR="${WINDOWS_BUILD_DIR:-paperplane-build}"
WORK="$MAIN/dist/.work-windows"
OUT="$MAIN/dist/windows"
rm -rf "$WORK" "$OUT"
mkdir -p "$WORK/pkg" "$OUT"

echo "==> 内核与内置插件"
for a in amd64 arm64; do
  "$HERE/build-windows-runtime.sh" "$WORK/pkg/runtime/$a" "$a" "$VERSION"
done

echo "==> 编辑器网页"
"$HERE/build-editor.sh" >/dev/null 2>&1 || echo "warning: 编辑器网页构建失败，使用已有的 dist"
[ -f "$MAIN/lemon-paperplane-main-editor/dist/index.html" ] || { echo "没有编辑器网页 lemon-paperplane-main-editor/dist"; exit 1; }
cp -R "$MAIN/lemon-paperplane-main-editor/dist" "$WORK/pkg/editor"

echo "==> 上传到 $HOST:$RDIR"
(cd "$MAIN" && COPYFILE_DISABLE=1 tar -c --exclude=bin --exclude=obj --exclude=dist --exclude=.vs --exclude=.DS_Store -f "$WORK/pkg/src.tar" \
  lemon-paperplane-main-windows shared lemon-paperplane-main-macos/PaperplaneKit/Tests/PaperplaneKitTests/Fixtures)
(cd "$WORK/pkg" && COPYFILE_DISABLE=1 tar -czf "$WORK/pkg.tgz" .)
cat > "$WORK/remote.ps1" <<PS
\$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8   # 输出经 ssh 回到 Mac，按 UTF-8
\$root = (Resolve-Path ".").Path
Remove-Item -Recurse -Force src, runtime, editor -ErrorAction SilentlyContinue
tar -xzf pkg.tgz
New-Item -ItemType Directory -Force src | Out-Null
tar -xf src.tar -C src
Remove-Item pkg.tgz, src.tar
Set-Location src\lemon-paperplane-main-windows
.\package.ps1 -Version "$VERSION" -RuntimeRoot "\$root\runtime" -EditorDir "\$root\editor"
exit \$LASTEXITCODE
PS
# PowerShell 5.1 按系统代码页读没有 BOM 的脚本
printf '\xef\xbb\xbf' | cat - "$WORK/remote.ps1" > "$WORK/remote-bom.ps1"
ssh -o BatchMode=yes "$HOST" "powershell -NoProfile -Command \"New-Item -ItemType Directory -Force '$RDIR' | Out-Null\""
scp -q -o BatchMode=yes "$WORK/pkg.tgz" "$HOST:$RDIR/pkg.tgz"
scp -q -o BatchMode=yes "$WORK/remote-bom.ps1" "$HOST:$RDIR/remote.ps1"

echo "==> 在 Windows 上编译与打包"
# 不依赖远端默认 shell 是 cmd 还是 PowerShell
ssh -o BatchMode=yes "$HOST" "powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Set-Location -LiteralPath '$RDIR'; & ./remote.ps1; exit \$LASTEXITCODE\""

echo "==> 取回产物"
for a in x64 arm64; do
  for f in "LemonPaperplane-$VERSION-windows-$a.zip" "LemonPaperplane-$VERSION-windows-$a-setup.exe"; do
    scp -q -o BatchMode=yes "$HOST:$RDIR/src/lemon-paperplane-main-windows/dist/$f" "$OUT/" 2>/dev/null || echo "warning: 没有 $f"
  done
done
rm -rf "$WORK"
ls -la "$OUT"
