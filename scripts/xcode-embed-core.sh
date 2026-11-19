#!/bin/bash
# 由 Xcode 构建阶段调用：从源码编译 Go 内核与全部内置插件，嵌入正在构建的 App。
# 这样无论是 Xcode 里 ⌘B、命令行 xcodebuild，还是 scripts/build-macos.sh，产出的 App 都是完整可用的
# （之前只拷了内核、没带插件，从 Xcode 构建目录打开的 App 什么工程都识别不了）。
set -euo pipefail

MAIN="$(cd "$(dirname "$0")/.." && pwd)"
APP_MACOS="${TARGET_BUILD_DIR}/${EXECUTABLE_FOLDER_PATH}"
APP_RES="${TARGET_BUILD_DIR}/${UNLOCALIZED_RESOURCES_FOLDER_PATH}"
WORK="${DERIVED_FILE_DIR:-${TMPDIR:-/tmp}}/paperplane-go"
mkdir -p "$WORK" "$APP_MACOS" "$APP_RES"

# Xcode 的构建环境 PATH 很短，补上常见的 Go 安装位置（含 build.sh 下载到工具缓存里的）
for d in /opt/homebrew/bin /usr/local/go/bin /usr/local/bin "$HOME/go/bin" "$HOME/sdk/go/bin" \
  $(ls -d "${PAPERPLANE_BUILD_CACHE:-$HOME/Library/Caches/LemonPaperplane/build-tools}"/go-*/go/bin 2>/dev/null | sort -V -r); do
  [ -d "$d" ] && PATH="$PATH:$d"
done
export PATH
if ! command -v go >/dev/null 2>&1; then
  echo "error: 找不到 Go 工具链（需要 Go 1.23+ 来编译内核与插件）"
  exit 1
fi

GOARCHS=()
for a in ${ARCHS:-$(uname -m)}; do
  case "$a" in
    arm64) GOARCHS+=(arm64) ;;
    x86_64) GOARCHS+=(amd64) ;;
  esac
done
[ ${#GOARCHS[@]} -eq 0 ] && GOARCHS=(arm64)
VERSION="${MARKETING_VERSION:-0.1.0}"

# 1. 内核
BINS=()
for a in "${GOARCHS[@]}"; do
  (cd "$MAIN/lemon-paperplane-main-core" && CGO_ENABLED=0 GOOS=darwin GOARCH="$a" \
    go build -trimpath -ldflags "-s -w -X main.Version=$VERSION" -o "$WORK/paperplane-$a" ./cmd/paperplane)
  BINS+=("$WORK/paperplane-$a")
done
lipo -create -output "$APP_MACOS/paperplane" "${BINS[@]}"

# 2. 内置插件（多入口二进制 + 插件目录，bin/ 下是指向 Contents/MacOS/paperplane-plugins 的链接）
PLUGIN_BIN_LINK="../../../../MacOS/paperplane-plugins" "$MAIN/scripts/build-plugins.sh" "$WORK/plugins-out" darwin "${GOARCHS[@]}" >/dev/null
cp -f "$WORK/plugins-out/paperplane-plugins" "$APP_MACOS/paperplane-plugins"
rm -rf "$APP_RES/plugins"
cp -R "$WORK/plugins-out/plugins" "$APP_RES/plugins"

# 3. 随 App 附带的离线商店（scripts/store-only.txt 里的插件，用户在插件页安装）
PLATS=""
for a in "${GOARCHS[@]}"; do PLATS="$PLATS darwin/$a"; done
if grep -qv '^#' "$MAIN/scripts/store-only.txt" 2>/dev/null; then
  rm -rf "$WORK/store"
  STORE_PLATFORMS="$PLATS" "$MAIN/scripts/build-store.sh" "$WORK/store" "." >/dev/null
  rm -rf "$APP_RES/store"
  cp -R "$WORK/store" "$APP_RES/store"
fi

# 4. 编辑器网页（打开编辑窗口时才加载）。有 Node 就重新构建，没有则用已构建的 dist
if [ -z "${PAPERPLANE_SKIP_EDITOR_BUILD:-}" ]; then
  "$MAIN/scripts/build-editor.sh" >/dev/null 2>&1 || echo "warning: 编辑器网页构建失败，使用已有的 dist"
fi
if [ -f "$MAIN/lemon-paperplane-main-editor/dist/index.html" ]; then
  rm -rf "$APP_RES/editor"
  cp -R "$MAIN/lemon-paperplane-main-editor/dist" "$APP_RES/editor"
else
  echo "warning: 没有编辑器网页（lemon-paperplane-main-editor/dist），编辑窗口不可用"
fi

COUNT=$(ls -1 "$APP_RES/plugins" | wc -l | tr -d ' ')
if [ "$COUNT" -eq 0 ]; then
  echo "error: 没有构建出任何插件（检查 ../lemon-paperplane-plugin-* 是否存在）"
  exit 1
fi

# 5. 签名（允许签名时 ad-hoc；正式分发由打包脚本用正式证书重签）
if [ "${CODE_SIGNING_ALLOWED:-NO}" = "YES" ]; then
  codesign --force --sign - "$APP_MACOS/paperplane" "$APP_MACOS/paperplane-plugins" >/dev/null 2>&1 || true
fi
echo "embedded paperplane core + $COUNT plugins (${GOARCHS[*]})"
