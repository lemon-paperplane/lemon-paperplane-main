#!/bin/bash
# 构建编辑器网页（lemon-paperplane-main-editor → dist/）。需要 Node.js 18+。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ED="$HERE/../lemon-paperplane-main-editor"
[ -f "$ED/package.json" ] || { echo "编辑器工程不存在：$ED"; exit 1; }
# Xcode 等图形界面环境里 PATH 很短：补上 nvm / Homebrew / build.sh 工具缓存里的 node
if ! command -v node >/dev/null 2>&1; then
  for d in "$HOME"/.nvm/versions/node/*/bin /opt/homebrew/bin /usr/local/bin \
    "${PAPERPLANE_BUILD_CACHE:-$HOME/Library/Caches/LemonPaperplane/build-tools}"/node-*/bin; do
    [ -x "$d/node" ] && PATH="$d:$PATH"
  done
fi
command -v node >/dev/null 2>&1 || { echo "error: 找不到 Node.js（编译编辑器网页需要，或先执行一次根目录的 build.sh）"; exit 1; }
cd "$ED"
# node_modules 是别的系统装的（比如从 Windows 拷过来）就重装：esbuild 带的是本机平台的程序
if [ ! -d "node_modules/@esbuild/$(node -p 'process.platform + "-" + process.arch')" ]; then
  rm -rf node_modules
  npm ci --registry="${NPM_REGISTRY:-https://registry.npmmirror.com}" --no-audit --no-fund
fi
npm run build --silent
echo "editor → $ED/dist"
