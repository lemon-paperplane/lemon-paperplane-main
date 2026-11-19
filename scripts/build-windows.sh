#!/bin/bash
# 在 Mac 上出 Windows 发布包：把源码（主仓库 + 旁边的插件仓库）传到一台 Windows 机器，
# 在那边执行仓库根目录的 build.cmd（自动准备依赖、编译、打包），再取回 dist/windows/。
#
# 用法：WINDOWS_BUILD_HOST=user@host scripts/build-windows.sh [版本]
#   WINDOWS_BUILD_DIR  Windows 上的工作目录（相对用户主目录，默认 paperplane-build）
# Windows 机器只需要：OpenSSH 服务 + 免密登录。其余依赖 build.ps1 自己准备，缓存在那台机器上，下次不再下载。
set -euo pipefail
MAIN="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="$(cd "$MAIN/.." && pwd)"
VERSION="${1:-}"
HOST="${WINDOWS_BUILD_HOST:?设置 WINDOWS_BUILD_HOST=user@host}"
RDIR="${WINDOWS_BUILD_DIR:-paperplane-build}"
OUT="$MAIN/dist/windows"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "==> 打包源码"
(cd "$ROOT" && COPYFILE_DISABLE=1 tar -czf "$TMP/src.tgz" --exclude=.git --exclude=node_modules --exclude=dist \
  --exclude=bin --exclude=obj --exclude=bundled-plugins --exclude=.build --exclude=DerivedData --exclude=xcuserdata \
  --exclude=.DS_Store --exclude=.vs "$(basename "$MAIN")" lemon-paperplane-plugin-*)

echo "==> 上传到 $HOST:$RDIR"
ssh -o BatchMode=yes "$HOST" "powershell -NoProfile -Command \"Remove-Item -Recurse -Force '$RDIR' -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force '$RDIR' | Out-Null\""
scp -q -o BatchMode=yes "$TMP/src.tgz" "$HOST:$RDIR/src.tgz"
ssh -o BatchMode=yes "$HOST" "cd /d $RDIR && tar -xzf src.tgz && del src.tgz"

echo "==> 在 Windows 上构建"
ARGS=""
[ -n "$VERSION" ] && ARGS="-Version $VERSION"
ssh -o BatchMode=yes "$HOST" "cd /d $RDIR\\$(basename "$MAIN") && build.cmd $ARGS"

echo "==> 取回产物"
rm -rf "$OUT" && mkdir -p "$OUT"
scp -q -o BatchMode=yes "$HOST:$RDIR/$(basename "$MAIN")/dist/windows/*" "$OUT/"
ls -la "$OUT"
