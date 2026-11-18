#!/bin/bash
# 构建编辑器网页（lemon-paperplane-main-editor → dist/）。需要 Node.js 18+。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ED="$HERE/../lemon-paperplane-main-editor"
[ -f "$ED/package.json" ] || { echo "编辑器工程不存在：$ED"; exit 1; }
# Xcode 等图形界面环境里 PATH 很短：补上 nvm / Homebrew 的 node
if ! command -v node >/dev/null 2>&1; then
  for d in "$HOME"/.nvm/versions/node/*/bin /opt/homebrew/bin /usr/local/bin; do [ -x "$d/node" ] && PATH="$d:$PATH"; done
fi
command -v node >/dev/null 2>&1 || { echo "error: 找不到 Node.js（编译编辑器网页需要）"; exit 1; }
cd "$ED"
[ -d node_modules ] || npm ci --registry="${NPM_REGISTRY:-https://registry.npmmirror.com}" --no-audit --no-fund
npm run build --silent
echo "editor → $ED/dist"
