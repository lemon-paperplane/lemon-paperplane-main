#!/bin/bash
# 构建 Windows 版内核与内置插件（纯 Go 交叉编译，不需要 CGO）。实现在 scripts/ppbuild。
#
# 用法：scripts/build-windows-runtime.sh <输出目录> <amd64|arm64> [版本]
# 产物（就是安装目录里内核相关的部分）：
#   <输出>/paperplane.exe            内核 + 命令行
#   <输出>/paperplane-plugins.exe    内置插件多入口程序
#   <输出>/plugins/<id>/…            插件目录（bin\<名字>.bundled 指向 ..\..\..\paperplane-plugins.exe）
#   <输出>/store/…                   随 App 附带的离线商店（scripts/store-only.txt 里的插件）
set -euo pipefail
MAIN="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?output dir}"
ARCH="${2:?amd64 or arm64}"
VERSION="${3:-0.1.0}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
exec go -C "$MAIN/scripts/ppbuild" run . runtime -main "$MAIN" -out "$OUT" -os windows -arch "$ARCH" -version "$VERSION"
