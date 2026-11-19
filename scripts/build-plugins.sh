#!/bin/bash
# 构建内置插件：把 ../lemon-paperplane-plugin-* 编进一个多入口二进制 paperplane-plugins（按 argv[0] 分发），
# 并整理出插件目录（plugin.json、icons、l10n、resources、web + bin/ 指向多入口二进制的链接）。
# 实现在 scripts/ppbuild（Go，Mac / Windows / Linux 共用）。
#
# 用法：scripts/build-plugins.sh <输出目录> [GOOS] [GOARCH...]
#   输出：<输出目录>/paperplane-plugins（多架构时用 lipo 合成通用二进制）
#        <输出目录>/plugins/<id>/...
# 环境变量 PLUGIN_BIN_LINK：插件 bin 下链接指向的相对路径（默认 ../../../paperplane-plugins，即与 plugins 同级）。
#         PAPERPLANE_PLUGINS_ROOT：插件仓库所在目录（默认主仓库的上一级）。
set -euo pipefail
MAIN="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?output dir}"
GOOS_="${2:-$(go env GOOS)}"
shift 2 2>/dev/null || shift $#
ARCHS="$*"
[ -n "$ARCHS" ] || ARCHS="$(go env GOARCH)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
exec go -C "$MAIN/scripts/ppbuild" run . plugins -main "$MAIN" -out "$OUT" -os "$GOOS_" -arch "${ARCHS// /,}" \
  -link "${PLUGIN_BIN_LINK:-../../../paperplane-plugins}"
