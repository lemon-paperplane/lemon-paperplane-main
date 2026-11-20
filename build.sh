#!/bin/bash
# 柠檬纸飞机 macOS 一键构建：在任何一台 Mac 上（包括全新的）执行，缺什么自动准备，最后产出拖拽安装的 DMG。
#
# 用法：./build.sh [版本] [--install] [--linux-headless]
#   产物：dist/macos/LemonPaperplane-<版本>-macos.dmg（通用二进制，Apple 芯片与 Intel 都能用，macOS 13+）
#   版本默认取 Xcode 工程的 MARKETING_VERSION
#   --install          构建后安装到 /Applications（会停掉正在运行的旧版本和它的后台服务）
#   --linux-headless   改为构建 Linux 无头版（单个可执行文件，10 个架构）→ dist/linux-headless/
#
# 自动准备的依赖（不需要 Homebrew，也不需要完整的 Xcode）：
#   Xcode 命令行工具   没有时调用系统安装（会要本机密码，或弹出安装窗口）
#   Go、Node.js        版本不够时下载到工具缓存目录（自动挑最快的下载源，含国内镜像）
#   dmgbuild           装进工具缓存目录里的 Python 虚拟环境
#   插件源码           旁边没有 lemon-paperplane-plugin-* 时从 GitHub 下载
#
# 环境变量：
#   SIGN_IDENTITY           签名证书。默认 ad-hoc（-），别的 Mac 首次打开要在「系统设置 → 隐私与安全性」里允许；
#                           分发给别人用 "Developer ID Application: …"
#   NOTARY_PROFILE          notarytool 的钥匙串配置名（xcrun notarytool store-credentials 建的），设置后自动公证并装订
#   PAPERPLANE_BUILD_CACHE  工具缓存目录（默认 ~/Library/Caches/LemonPaperplane/build-tools）
#   GOPROXY / NPM_REGISTRY / PIP_INDEX_URL  指定后不再自动挑下载源
set -euo pipefail

MAIN="$(cd "$(dirname "$0")" && pwd)"
MACOS="$MAIN/lemon-paperplane-main-macos"
PBXPROJ="$MACOS/LemonPaperplane.xcodeproj/project.pbxproj"
APPNAME="Lemon Paperplane"
CACHE="${PAPERPLANE_BUILD_CACHE:-$HOME/Library/Caches/LemonPaperplane/build-tools}"
WORK="$MAIN/dist/.work-macos"
DIST="$MAIN/dist/macos"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

say() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
die() { printf '\n\033[31m错误：%s\033[0m\n' "$*" >&2; exit 1; }

# Xcode 工程里的构建设置（Debug 与 Release 相同，取第一个）
pbx() { sed -n "s/^[[:space:]]*$1 = \"\{0,1\}\([^\";]*\)\"\{0,1\};.*/\1/p" "$PBXPROJ" | head -1; }

VERSION=""
INSTALL=0
HEADLESS=0
for a in "$@"; do
  case "$a" in
    --install) INSTALL=1 ;;
    --linux-headless) HEADLESS=1 ;;
    -h|--help) sed -n '2,21p' "$0"; exit 0 ;;
    -*) die "不认识的参数 $a" ;;
    *) VERSION="$a" ;;
  esac
done
[ "$(uname -s)" = Darwin ] || die "build.sh 只在 macOS 上运行（Windows 用 build.ps1）"
[ -n "$VERSION" ] || VERSION="$(pbx MARKETING_VERSION)"
[ -n "$VERSION" ] || VERSION="0.1.0"
HOST_ARCH=arm64
[ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = 1 ] || HOST_ARCH=amd64
mkdir -p "$CACHE"

# 下载（断点续传、失败重试），先写 .part 再改名
fetch() {
  local url="$1" out="$2"
  mkdir -p "$(dirname "$out")"
  local prog=(-sS)
  [ -t 2 ] && prog=(--progress-bar)
  # HTTP/1.1：有的代理会掐断 HTTP/2 长下载
  curl --http1.1 -fL "${prog[@]}" --connect-timeout 15 --retry 6 --retry-delay 3 --retry-all-errors -C - -o "$out.part" "$url" \
    || { rm -f "$out.part"; return 1; }
  mv -f "$out.part" "$out"
}

# 从候选地址里挑下载最快的：并行各下 512 KB，打印最快的那个（下标从 0 开始）
fastest() {
  local probe="$1"; shift
  local dir; dir="$(mktemp -d)"
  local i=0
  for base in "$@"; do
    ( curl --http1.1 -s -o /dev/null -r 0-524287 -m 8 -L -w '%{http_code} %{speed_download}' "${base}${probe}" > "$dir/$i" 2>/dev/null || true ) &
    i=$((i+1))
  done
  wait
  local best=-1 bestSpeed=0 j=0
  for ((j=0; j<i; j++)); do
    read -r code speed < "$dir/$j" || true
    case "$code" in 200|206) ;; *) continue ;; esac
    speed="${speed%.*}"
    if [ "${speed:-0}" -gt "$bestSpeed" ]; then best=$j; bestSpeed=$speed; fi
  done
  rm -rf "$dir"
  [ "$best" -ge 0 ] || best=0
  echo "$best"
}

# 只有系统自带命令的 PATH（PAPERPLANE_IGNORE_SYSTEM_TOOLS=1 时用，模拟全新机器，测试脚本用）
sys_path() { echo "/usr/bin:/bin:/usr/sbin:/sbin"; }

# ver_ge 1.23.4 1.23 → 真
ver_ge() { [ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -1)" = "$2" ]; }

# ---------------------------------------------------------------- Xcode 命令行工具
have_swift() { xcrun --find swiftc >/dev/null 2>&1 && xcrun swiftc --version >/dev/null 2>&1; }

ensure_clt() {
  say "检查 Xcode 命令行工具"
  if have_swift; then info "$(xcrun swiftc --version 2>&1 | head -1)"; return; fi
  # 选中的是完整 Xcode 但还没同意许可：改用命令行工具，没有的话请用户同意许可
  if xcrun swiftc --version 2>&1 | grep -qi license; then
    if [ -x /Library/Developer/CommandLineTools/usr/bin/swiftc ]; then
      export DEVELOPER_DIR=/Library/Developer/CommandLineTools
      have_swift && { info "Xcode 未同意许可，改用命令行工具"; return; }
    fi
    info "需要同意 Xcode 许可（输入本机密码）"
    sudo xcodebuild -license accept
    have_swift && return
  fi
  info "没有安装，开始安装（需要输入本机密码）"
  local flag=/tmp/.com.apple.dt.CommandLineTools.installondemand.in-progress
  touch "$flag"
  local label
  label="$(softwareupdate -l 2>/dev/null | sed -n 's/^[[:space:]]*\* Label: \(Command Line Tools.*\)$/\1/p' | sort -V | tail -1)"
  if [ -n "$label" ]; then
    info "$label"
    sudo softwareupdate -i "$label" || true
  fi
  rm -f "$flag"
  if ! have_swift; then
    xcode-select --install >/dev/null 2>&1 || true
    info "请在弹出的窗口里点「安装」，装好后会自动继续…"
    local n=0
    until have_swift; do
      sleep 10; n=$((n+1))
      [ $n -lt 360 ] || die "等待 Xcode 命令行工具安装超时"
    done
  fi
  have_swift || die "Xcode 命令行工具没有装好"
  info "$(xcrun swiftc --version 2>&1 | head -1)"
}

# ---------------------------------------------------------------- Go
ensure_go() {
  say "检查 Go"
  local need; need="$(sed -n 's/^go \([0-9.]*\).*/\1/p' "$MAIN/lemon-paperplane-main-core/go.mod" | head -1)"
  if [ -n "${PAPERPLANE_IGNORE_SYSTEM_TOOLS:-}" ]; then
    PATH="$(sys_path)"
  else
    for d in /usr/local/go/bin /opt/homebrew/bin /usr/local/bin "$HOME/go/bin" "$HOME/sdk/go/bin"; do
      [ -d "$d" ] && PATH="$PATH:$d"
    done
  fi
  local cached; cached="$(ls -d "$CACHE"/go-*/go/bin 2>/dev/null | sort -V | tail -1 || true)"
  [ -n "$cached" ] && PATH="$cached:$PATH"
  export PATH GOTOOLCHAIN=local
  if command -v go >/dev/null 2>&1; then
    local v; v="$(go env GOVERSION 2>/dev/null | sed 's/^go//')"
    if [ -n "$v" ] && ver_ge "$v" "$need"; then info "go ${v}（$(command -v go)）"; return; fi
    info "已有 go ${v:-?}，低于要求的 $need"
  fi
  local meta="$CACHE/go-versions.json"
  fetch "https://golang.google.cn/dl/?mode=json" "$meta" || fetch "https://go.dev/dl/?mode=json" "$meta" \
    || die "取不到 Go 的版本列表"
  local file sha ver
  read -r ver file sha < <(/usr/bin/python3 - "$meta" "$HOST_ARCH" <<'PY'
import json, sys
rel = json.load(open(sys.argv[1]))[0]
f = next(f for f in rel["files"] if f["os"] == "darwin" and f["arch"] == sys.argv[2] and f["kind"] == "archive")
print(rel["version"], f["filename"], f["sha256"])
PY
)
  local mirrors=("https://dl.google.com/go/" "https://mirrors.nju.edu.cn/golang/" "https://mirrors.ustc.edu.cn/golang/" "https://mirrors.aliyun.com/golang/")
  local best; best="$(fastest "$file" "${mirrors[@]}")"
  info "下载 ${ver}（${mirrors[$best]}）"
  local tgz="$CACHE/$file"
  [ -f "$tgz" ] && [ "$(shasum -a 256 "$tgz" | awk '{print $1}')" = "$sha" ] || {
    rm -f "$tgz"
    fetch "${mirrors[$best]}$file" "$tgz" || fetch "https://dl.google.com/go/$file" "$tgz" || die "下载 Go 失败"
  }
  [ "$(shasum -a 256 "$tgz" | awk '{print $1}')" = "$sha" ] || { rm -f "$tgz"; die "Go 安装包校验失败，请重试"; }
  rm -rf "$CACHE/$ver.tmp" && mkdir -p "$CACHE/$ver.tmp"
  tar -xzf "$tgz" -C "$CACHE/$ver.tmp"
  rm -rf "$CACHE/go-${ver#go}" && mv "$CACHE/$ver.tmp" "$CACHE/go-${ver#go}"
  rm -f "$tgz"
  export PATH="$CACHE/go-${ver#go}/go/bin:$PATH"
  info "go $(go env GOVERSION)（$(command -v go)）"
}

# 没配置过 Go 模块代理时，在官方代理和 goproxy.cn 之间挑快的
ensure_goproxy() {
  [ -z "${GOPROXY:-}" ] && [ "$(go env GOPROXY)" = "https://proxy.golang.org,direct" ] || return 0
  if [ "$(fastest "golang.org/x/sys/@v/v0.29.0.zip" "https://proxy.golang.org/" "https://goproxy.cn/")" = 1 ]; then
    export GOPROXY="https://goproxy.cn,direct" GOSUMDB="sum.golang.google.cn"
    info "Go 模块代理：goproxy.cn"
  fi
}

# ---------------------------------------------------------------- Node.js
ensure_node() {
  say "检查 Node.js"
  if [ -n "${PAPERPLANE_IGNORE_SYSTEM_TOOLS:-}" ]; then
    PATH="$(sys_path):$(dirname "$(command -v go)")"
  else
    for d in /opt/homebrew/bin /usr/local/bin; do [ -d "$d" ] && PATH="$PATH:$d"; done
    local nvm; nvm="$(ls -d "$HOME"/.nvm/versions/node/*/bin 2>/dev/null | sort -V | tail -1 || true)"
    [ -n "$nvm" ] && PATH="$PATH:$nvm"
  fi
  local cached; cached="$(ls -d "$CACHE"/node-*/bin 2>/dev/null | sort -V | tail -1 || true)"
  [ -n "$cached" ] && PATH="$cached:$PATH"
  export PATH
  if command -v node >/dev/null 2>&1; then
    local v; v="$(node --version 2>/dev/null | sed 's/^v//')"
    if [ -n "$v" ] && ver_ge "$v" 18; then info "node ${v}（$(command -v node)）"; return; fi
    info "已有 node ${v:-?}，低于要求的 18"
  fi
  local mirrors=("https://nodejs.org/dist/" "https://registry.npmmirror.com/-/binary/node/")
  local best; best="$(fastest "index.json" "${mirrors[@]}")"
  local base="${mirrors[$best]}" meta="$CACHE/node-index.json"
  fetch "${base}index.json" "$meta" || die "取不到 Node.js 的版本列表"
  local ver; ver="$(/usr/bin/python3 -c 'import json,sys;print(next(r["version"] for r in json.load(open(sys.argv[1])) if r.get("lts")))' "$meta")"
  local arch=arm64; [ "$HOST_ARCH" = amd64 ] && arch=x64
  local name="node-$ver-darwin-$arch" sums="$CACHE/node-$ver-SHASUMS256.txt"
  info "下载 ${ver}（${base}）"
  fetch "$base$ver/SHASUMS256.txt" "$sums" || die "下载 Node.js 校验文件失败"
  local sha; sha="$(awk -v f="$name.tar.gz" '$2==f{print $1}' "$sums")"
  fetch "$base$ver/$name.tar.gz" "$CACHE/$name.tar.gz" || die "下载 Node.js 失败"
  [ "$(shasum -a 256 "$CACHE/$name.tar.gz" | awk '{print $1}')" = "$sha" ] || { rm -f "$CACHE/$name.tar.gz"; die "Node.js 安装包校验失败，请重试"; }
  rm -rf "$CACHE/$name" && tar -xzf "$CACHE/$name.tar.gz" -C "$CACHE" && rm -f "$CACHE/$name.tar.gz" "$sums"
  rm -rf "$CACHE/node-${ver#v}" && mv "$CACHE/$name" "$CACHE/node-${ver#v}"
  export PATH="$CACHE/node-${ver#v}/bin:$PATH"
  info "node $(node --version)（$(command -v node)）"
}

ensure_npm_registry() {
  if [ -z "${NPM_REGISTRY:-}" ]; then
    local regs=("https://registry.npmjs.org/" "https://registry.npmmirror.com/")
    NPM_REGISTRY="${regs[$(fastest "esbuild/-/esbuild-0.28.2.tgz" "${regs[@]}")]}"
  fi
  export NPM_REGISTRY
  info "npm 源：$NPM_REGISTRY"
}

# ---------------------------------------------------------------- dmgbuild
ensure_dmgbuild() {
  say "检查 dmgbuild"
  local venv="$CACHE/dmgbuild-venv"
  if [ -x "$venv/bin/dmgbuild" ] && "$venv/bin/python" -c 'import dmgbuild' 2>/dev/null; then info "已就绪"; return; fi
  rm -rf "$venv"
  /usr/bin/python3 -m venv "$venv" || die "创建 Python 虚拟环境失败"
  local idx="${PIP_INDEX_URL:-}"
  if [ -z "$idx" ]; then
    local idxs=("https://pypi.org/simple/" "https://mirrors.aliyun.com/pypi/simple/" "https://repo.huaweicloud.com/repository/pypi/simple/" "https://pypi.tuna.tsinghua.edu.cn/simple/")
    idx="${idxs[$(fastest "dmgbuild/" "${idxs[@]}")]}"
  fi
  info "pip 源：$idx"
  "$venv/bin/python" -m pip install -q --disable-pip-version-check --index-url "$idx" dmgbuild \
    || die "安装 dmgbuild 失败"
  info "$("$venv/bin/python" -m pip show dmgbuild 2>/dev/null | sed -n 's/^Version: /dmgbuild /p')"
}

# ---------------------------------------------------------------- 编辑器网页
build_editor() {
  say "构建编辑器网页"
  local ed="$MAIN/lemon-paperplane-main-editor"
  # node_modules 是别的系统装的（比如从 Windows 拷过来）就重装：esbuild 带的是本机平台的程序
  local plat; plat="$(node -p 'process.platform + "-" + process.arch')"
  if [ ! -d "$ed/node_modules/@esbuild/$plat" ]; then
    rm -rf "$ed/node_modules"
    (cd "$ed" && npm ci --registry="$NPM_REGISTRY" --no-audit --no-fund) || die "安装编辑器依赖失败"
  fi
  (cd "$ed" && npm run build --silent) || die "编辑器网页构建失败"
  [ -f "$ed/dist/index.html" ] || die "没有生成 lemon-paperplane-main-editor/dist/index.html"
}

# ---------------------------------------------------------------- 界面（SwiftPM，不需要完整 Xcode）
build_app() {
  say "编译界面（arm64 + x86_64）"
  local spm="$WORK/spm"
  mkdir -p "$spm/Sources"
  ln -s "$MACOS/LemonPaperplane" "$spm/Sources/LemonPaperplane"
  local target; target="$(pbx MACOSX_DEPLOYMENT_TARGET)"
  local major="${target%%.*}"
  cat > "$spm/Package.swift" <<EOF
// swift-tools-version:5.9
// 由 build.sh 生成：用 SwiftPM 编译 Xcode 工程里的界面源码（不需要完整 Xcode）。
import PackageDescription
let package = Package(
    name: "LemonPaperplaneApp",
    platforms: [.macOS(.v$major)],
    dependencies: [.package(path: "$MACOS/PaperplaneKit")],
    targets: [
        .executableTarget(
            name: "LemonPaperplane",
            dependencies: [.product(name: "PaperplaneKit", package: "PaperplaneKit")],
            path: "Sources/LemonPaperplane",
            exclude: ["Resources", "Features/VCS/README.md"]
        ),
    ]
)
EOF
  local bins=()
  for arch in arm64 x86_64; do
    # 每种架构单独一个构建目录（新的 SwiftPM 构建后端不按架构区分产物路径）
    local scratch="$WORK/spm-build-$arch"
    xcrun swift build -c release --package-path "$spm" --scratch-path "$scratch" --arch "$arch" \
      --product LemonPaperplane 2>&1 | grep -Ev '^\[[0-9]+/[0-9]+\]|^Building for|^Compiling|^Write |^Planning|^Fetching|^Computing|^Creating working copy|^Working copy' || true
    local bin; bin="$(xcrun swift build -c release --package-path "$spm" --scratch-path "$scratch" --arch "$arch" --show-bin-path)/LemonPaperplane"
    [ -x "$bin" ] || die "界面编译失败（${arch}）"
    lipo -info "$bin" | grep -q "$arch" || die "界面编译产物不是 $arch"
    cp "$bin" "$WORK/LemonPaperplane-$arch"
    bins+=("$WORK/LemonPaperplane-$arch")
  done

  say "组装 $APPNAME.app"
  APP="$WORK/app/$APPNAME.app"
  local c="$APP/Contents"
  mkdir -p "$c/MacOS" "$c/Resources"
  lipo -create -output "$c/MacOS/$APPNAME" "${bins[@]}"
  printf 'APPL????' > "$c/PkgInfo"

  # Info.plist：工程里的 LemonPaperplane-Info.plist + Xcode 自动生成的键（取值来自工程构建设置）
  local plist="$c/Info.plist" pb=/usr/libexec/PlistBuddy
  cp "$MACOS/LemonPaperplane-Info.plist" "$plist"
  plset() {
    $pb -c "Delete :$1" "$plist" >/dev/null 2>&1 || true
    if [ "$2" = array ]; then $pb -c "Add :$1 array" "$plist"; else $pb -c "Add :$1 $2 $3" "$plist"; fi
  }
  plset CFBundleDevelopmentRegion string en
  plset CFBundleExecutable string "$APPNAME"
  plset CFBundleIconFile string AppIcon
  plset CFBundleIdentifier string "$(pbx PRODUCT_BUNDLE_IDENTIFIER)"
  plset CFBundleInfoDictionaryVersion string 6.0
  plset CFBundleName string "$(pbx PRODUCT_NAME)"
  plset CFBundleDisplayName string "$(pbx INFOPLIST_KEY_CFBundleDisplayName)"
  plset CFBundlePackageType string APPL
  plset CFBundleShortVersionString string "$VERSION"
  plset CFBundleVersion string "$(pbx CURRENT_PROJECT_VERSION)"
  plset CFBundleSupportedPlatforms array
  $pb -c "Add :CFBundleSupportedPlatforms:0 string MacOSX" "$plist"
  plset LSApplicationCategoryType string "$(pbx INFOPLIST_KEY_LSApplicationCategoryType)"
  plset LSMinimumSystemVersion string "$target"
  plset NSHumanReadableCopyright string "$(pbx INFOPLIST_KEY_NSHumanReadableCopyright)"
  plutil -lint "$plist" >/dev/null

  # 图标与本地化
  local iconset="$WORK/AppIcon.iconset"
  mkdir -p "$iconset"
  cp "$MACOS/LemonPaperplane/Resources/Assets.xcassets/AppIcon.appiconset/"icon_*.png "$iconset/"
  iconutil -c icns -o "$c/Resources/AppIcon.icns" "$iconset"
  for l in "$MACOS/LemonPaperplane/Resources/"*.lproj; do cp -R "$l" "$c/Resources/"; done

  # 三端共用资源（和 Xcode 构建阶段一样：图标、文案、品牌图形、设计变量）
  local shared="$MAIN/shared" dest="$c/Resources/shared"
  mkdir -p "$dest/icons" "$dest/i18n" "$dest/branding"
  cp "$shared"/icons/*.svg "$dest/icons/"
  cp "$shared"/i18n/*.json "$dest/i18n/"
  cp "$shared"/branding/*.svg "$dest/branding/"
  cp "$shared/design-tokens.json" "$dest/"

  # 内核、内置插件、离线商店、编辑器网页（和 Xcode 构建阶段同一个脚本）
  TARGET_BUILD_DIR="$WORK/app" EXECUTABLE_FOLDER_PATH="$APPNAME.app/Contents/MacOS" \
  UNLOCALIZED_RESOURCES_FOLDER_PATH="$APPNAME.app/Contents/Resources" DERIVED_FILE_DIR="$WORK/derived" \
  ARCHS="arm64 x86_64" MARKETING_VERSION="$VERSION" CODE_SIGNING_ALLOWED=NO PAPERPLANE_SKIP_EDITOR_BUILD=1 \
    "$MAIN/scripts/xcode-embed-core.sh" | sed 's/^/    /'

  say "校验"
  for f in paperplane paperplane-plugins "$APPNAME"; do
    lipo -info "$c/MacOS/$f" | grep -q "x86_64 arm64" || die "$f 不是通用二进制"
  done
  local n; n="$(ls -1 "$c/Resources/plugins" | wc -l | tr -d ' ')"
  [ "$n" -gt 0 ] || die "包里没有插件"
  [ -f "$c/Resources/editor/index.html" ] || die "包里没有编辑器网页"
  info "通用二进制；内置插件 $n 个；编辑器网页已带上"

  say "签名（${SIGN_IDENTITY}）"
  local ts=(--timestamp=none)
  [ "$SIGN_IDENTITY" = "-" ] || ts=(--timestamp)
  codesign --force --options runtime "${ts[@]}" -s "$SIGN_IDENTITY" "$c/MacOS/paperplane"
  codesign --force --options runtime "${ts[@]}" -s "$SIGN_IDENTITY" "$c/MacOS/paperplane-plugins"
  codesign --force --options runtime "${ts[@]}" -s "$SIGN_IDENTITY" "$APP"
  codesign --verify --strict "$APP" || die "签名校验失败"
  info "签名校验通过"
}

# ---------------------------------------------------------------- DMG
build_dmg() {
  say "生成 DMG"
  DMG="$DIST/LemonPaperplane-$VERSION-macos.dmg"
  mkdir -p "$DIST"
  rm -f "$DMG"
  local settings="$WORK/dmg-settings.py"
  # 窗口与图标位置要和背景图一致（lemon-paperplane-main-macos/Packaging/make-dmg-background.swift）
  cat > "$settings" <<'PY'
import os.path
app = defines["app"]
appname = os.path.basename(app)
format = "UDZO"
files = [app]
symlinks = {"Applications": "/Applications"}
icon = defines["icon"]
background = defines["background"]
window_rect = ((200, 160), (600, 380))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 112
text_size = 13
icon_locations = {appname: (160, 175), "Applications": (440, 175)}
PY
  "$CACHE/dmgbuild-venv/bin/dmgbuild" -s "$settings" \
    -D app="$APP" -D icon="$APP/Contents/Resources/AppIcon.icns" \
    -D background="$MACOS/Packaging/dmg-background.tiff" \
    "$APPNAME" "$DMG" >/dev/null || die "生成 DMG 失败"
  if [ "$SIGN_IDENTITY" != "-" ]; then
    codesign --force --timestamp -s "$SIGN_IDENTITY" "$DMG"
  fi
  if [ -n "${NOTARY_PROFILE:-}" ]; then
    say "公证（${NOTARY_PROFILE}）"
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait || die "公证失败"
    xcrun stapler staple "$DMG" || die "装订公证票据失败"
  fi

  # 挂载检查：里面有 App（签名完好）和「应用程序」替身
  local mnt="$WORK/mnt"
  mkdir -p "$mnt"
  hdiutil attach -nobrowse -noautoopen -readonly -mountpoint "$mnt" "$DMG" >/dev/null || die "DMG 挂载失败"
  local ok=1
  [ -d "$mnt/$APPNAME.app" ] && codesign --verify --strict "$mnt/$APPNAME.app" 2>/dev/null || ok=0
  [ -L "$mnt/Applications" ] || ok=0
  [ -f "$mnt/.DS_Store" ] || ok=0
  "$LSREGISTER" -u "$mnt/$APPNAME.app" >/dev/null 2>&1 || true
  hdiutil detach -quiet "$mnt" || hdiutil detach -force -quiet "$mnt" || true
  [ "$ok" = 1 ] || die "DMG 内容不完整"
  info "$(du -h "$DMG" | awk '{print $1}')  $DMG"
}

# ---------------------------------------------------------------- 安装到 /Applications
install_app() {
  say "安装到 /Applications"
  # 先停掉正在运行的旧版本（界面与后台服务；服务里的工程会被停止）
  pkill -TERM -f "/$APPNAME.app/Contents/MacOS/$APPNAME" >/dev/null 2>&1 || true
  # 不用 paperplane call：没有服务时它会拉起一个新的
  local rt="$HOME/Library/Application Support/LemonPaperplane/runtime.json"
  if [ -f "$rt" ]; then
    local port token pid
    port="$(plutil -extract port raw -o - "$rt" 2>/dev/null || true)"
    token="$(plutil -extract token raw -o - "$rt" 2>/dev/null || true)"
    pid="$(plutil -extract pid raw -o - "$rt" 2>/dev/null || true)"
    if [ -n "$port" ] && [ -n "$token" ]; then
      curl -s -m 15 -X POST -H "Authorization: Bearer $token" "http://127.0.0.1:$port/api/v1/call/system.shutdown" \
        -d '{"stopRuns":true}' >/dev/null 2>&1 || true
      for _ in $(seq 1 100); do kill -0 "${pid:-0}" 2>/dev/null || break; sleep 0.1; done
    fi
  fi
  rm -rf "/Applications/$APPNAME.app"
  ditto "$APP" "/Applications/$APPNAME.app"
  "$LSREGISTER" -f "/Applications/$APPNAME.app"
  info "已安装：/Applications/$APPNAME.app"
}

cleanup() {
  local rc=$?
  [ -n "${KEEP_WORK:-}" ] || rm -rf "$WORK"
  # 系统会登记构建过程中出现的 .app（中间目录、DMG 挂载点），应用库里只留 /Applications 那一份
  "$MAIN/scripts/clean-stale-apps.sh" >/dev/null 2>&1 || true
  exit $rc
}

# ---------------------------------------------------------------- 主流程
if [ "$HEADLESS" = 1 ]; then
  say "柠檬纸飞机 $VERSION · Linux 无头版构建（工具缓存：${CACHE}）"
  ensure_go
  ensure_goproxy
  say "准备插件源码"
  go -C "$MAIN/scripts/ppbuild" run . fetch-plugins -main "$MAIN" | sed 's/^/    /'
  say "编译（内核 + 内置插件，每个架构一个文件）"
  rm -rf "$MAIN/dist/linux-headless"
  go -C "$MAIN/scripts/ppbuild" run . headless -main "$MAIN" -out "$MAIN/dist/linux-headless" -version "$VERSION"
  say "完成"
  info "$MAIN/dist/linux-headless/"
  exit 0
fi

say "柠檬纸飞机 $VERSION · macOS 构建（工具缓存：${CACHE}）"
ensure_clt
ensure_go
ensure_goproxy
ensure_node
ensure_npm_registry
ensure_dmgbuild

say "准备插件源码"
go -C "$MAIN/scripts/ppbuild" run . fetch-plugins -main "$MAIN" | sed 's/^/    /'

rm -rf "$WORK"
mkdir -p "$WORK"
trap cleanup EXIT
build_editor
build_app
build_dmg
[ "$INSTALL" = 1 ] && install_app

say "完成"
info "$DMG"
[ "$SIGN_IDENTITY" = "-" ] && info "未使用开发者证书签名：别的 Mac 首次打开时需要在「系统设置 → 隐私与安全性」里点「仍要打开」"
exit 0
