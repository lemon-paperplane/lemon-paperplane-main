#!/bin/bash
# 已并入仓库根目录的 build.sh（自动准备依赖、产出 DMG）。保留这个入口，参数原样转过去。
#   scripts/build-macos.sh [版本] [--install]
exec "$(cd "$(dirname "$0")/.." && pwd)/build.sh" "$@"
