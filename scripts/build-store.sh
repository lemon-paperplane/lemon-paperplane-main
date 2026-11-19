#!/bin/bash
# 为插件商店构建各平台插件包与索引。实现在 scripts/ppbuild（Go，Mac / Windows / Linux 共用）。
#
# 用法：scripts/build-store.sh <输出目录> <下载根地址> [插件目录...]
#   例：scripts/build-store.sh dist/store https://cdn.example.com/paperplane/plugins
#   下载根地址为 "." 时生成相对路径（随 App 附带的离线商店用）；环境变量 STORE_PLATFORMS 限定平台，如 "darwin/arm64 darwin/amd64"
#   不给插件目录时默认打包 scripts/store-only.txt 里列出的插件
# 产物：<输出目录>/<id>/<version>/<id>-<version>-<os>-<arch>.zip 与 <输出目录>/index.json
# 设置 → 插件商店索引地址 填 <下载根地址>/index.json 即可。
set -euo pipefail
MAIN="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?output dir}"
BASE="${2:?base url}"
shift 2
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
DIRS=()
for d in "$@"; do DIRS+=("$(cd "$d" && pwd)"); done
exec go -C "$MAIN/scripts/ppbuild" run . store -main "$MAIN" -out "$OUT" -base "$BASE" ${DIRS[@]+"${DIRS[@]}"}
