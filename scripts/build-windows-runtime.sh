#!/bin/bash
# 构建 Windows 版内核与内置插件（在 Mac 上交叉编译，纯 Go、不需要 CGO）。
#
# 用法：scripts/build-windows-runtime.sh <输出目录> <amd64|arm64> [版本]
# 产物（就是安装目录里内核相关的部分）：
#   <输出>/paperplane.exe            内核 + 命令行
#   <输出>/paperplane-plugins.exe    内置插件多入口程序
#   <输出>/plugins/<id>/…            插件目录（bin\<名字>.bundled 指向 ..\..\..\paperplane-plugins.exe）
#   <输出>/store/…                   随 App 附带的离线商店（scripts/store-only.txt 里的插件）
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MAIN="$(cd "$HERE/.." && pwd)"
OUT="${1:?output dir}"
ARCH="${2:?amd64 or arm64}"
VERSION="${3:-0.1.0}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

(cd "$MAIN/lemon-paperplane-main-core" && CGO_ENABLED=0 GOOS=windows GOARCH="$ARCH" \
  go build -trimpath -ldflags "-s -w -X main.Version=$VERSION" -o "$OUT/paperplane.exe" ./cmd/paperplane)

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PLUGIN_BIN_LINK="../../../paperplane-plugins" "$HERE/build-plugins.sh" "$WORK/plugins-out" windows "$ARCH" >/dev/null
cp -f "$WORK/plugins-out/paperplane-plugins.exe" "$OUT/paperplane-plugins.exe"
rm -rf "$OUT/plugins"
cp -R "$WORK/plugins-out/plugins" "$OUT/plugins"

if grep -qv '^#' "$HERE/store-only.txt" 2>/dev/null; then
  rm -rf "$OUT/store"
  STORE_PLATFORMS="windows/$ARCH" "$HERE/build-store.sh" "$OUT/store" "." >/dev/null
fi
N=$(ls -1 "$OUT/plugins" | wc -l | tr -d ' ')
echo "windows/$ARCH: paperplane.exe + $N 个内置插件 → $OUT"
