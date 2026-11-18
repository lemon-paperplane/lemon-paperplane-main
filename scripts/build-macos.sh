#!/bin/bash
# 构建 macOS 安装包：Swift 界面 + Go 内核 + 全部内置插件（Xcode 构建阶段 scripts/xcode-embed-core.sh 负责编译嵌入），
# 产出唯一一份 Lemon Paperplane.app。
#
# 用法：scripts/build-macos.sh [版本号] [--install]
#   产物：dist/macos/LemonPaperplane-<版本>-macos.zip
#   --install：安装到 /Applications（覆盖旧版本），并清掉系统里登记的其他副本，启动台只留一个入口
# 签名：默认 ad-hoc（本机可用）。正式分发设置 SIGN_IDENTITY="Developer ID Application: ..." 并自行公证。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
MAIN="$(cd "$HERE/.." && pwd)"
VERSION="0.1.0"
INSTALL=0
for a in "$@"; do
  case "$a" in
    --install) INSTALL=1 ;;
    *) VERSION="$a" ;;
  esac
done
DIST="$MAIN/dist/macos"
WORK="$MAIN/dist/.work-macos"
APPNAME="Lemon Paperplane"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

rm -rf "$WORK" "$DIST"
mkdir -p "$WORK" "$DIST"

echo "==> 构建（界面 + 内核 + 内置插件，通用二进制）"
xcodebuild -project "$MAIN/lemon-paperplane-main-macos/LemonPaperplane.xcodeproj" -scheme LemonPaperplane \
  -configuration Release -destination 'generic/platform=macOS' -derivedDataPath "$WORK/dd" \
  ARCHS="arm64 x86_64" ONLY_ACTIVE_ARCH=NO CODE_SIGNING_ALLOWED=NO \
  MARKETING_VERSION="$VERSION" build | grep -E "error|warning: |embedded|BUILD" || true
APP_SRC="$WORK/dd/Build/Products/Release/$APPNAME.app"
[ -d "$APP_SRC" ] || { echo "没有找到构建出的 .app"; exit 1; }

echo "==> 校验"
APP="$WORK/$APPNAME.app"
cp -R "$APP_SRC" "$APP"
for f in paperplane paperplane-plugins "$APPNAME"; do
  lipo -info "$APP/Contents/MacOS/$f" | grep -q "x86_64 arm64" || { echo "$f 不是通用二进制"; exit 1; }
done
N=$(ls -1 "$APP/Contents/Resources/plugins" | wc -l | tr -d ' ')
[ "$N" -gt 0 ] || { echo "包里没有插件"; exit 1; }
echo "内置插件 $N 个"

echo "==> 签名（$SIGN_IDENTITY）"
codesign --force --options runtime --timestamp=none -s "$SIGN_IDENTITY" "$APP/Contents/MacOS/paperplane"
codesign --force --options runtime --timestamp=none -s "$SIGN_IDENTITY" "$APP/Contents/MacOS/paperplane-plugins"
codesign --force --options runtime --timestamp=none -s "$SIGN_IDENTITY" "$APP"
codesign --verify --strict "$APP" && echo "签名校验通过"

(cd "$WORK" && ditto -c -k --keepParent "$APPNAME.app" "$DIST/LemonPaperplane-$VERSION-macos.zip")
du -sh "$APP" "$DIST/LemonPaperplane-$VERSION-macos.zip"

if [ "$INSTALL" = 1 ]; then
  echo "==> 安装到 /Applications"
  # 先停掉正在运行的旧版本（界面与后台服务；服务里的工程会被停止）
  pkill -TERM -f "/$APPNAME.app/Contents/MacOS/$APPNAME" >/dev/null 2>&1 || true
  # 停掉正在运行的后台服务（不论来自哪一份安装）；不用 paperplane call，它在没有服务时会拉起一个新的
  RT="$HOME/Library/Application Support/LemonPaperplane/runtime.json"
  if [ -f "$RT" ]; then
    read -r PORT TOKEN PID < <(python3 -c "import json,sys;r=json.load(open(sys.argv[1]));print(r['port'],r['token'],r['pid'])" "$RT")
    curl -s -m 15 -X POST -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:$PORT/api/v1/call/system.shutdown" -d '{"stopRuns":true}' >/dev/null 2>&1 || true
    for i in $(seq 1 100); do kill -0 "$PID" 2>/dev/null || break; sleep 0.1; done
  fi
  rm -rf "/Applications/$APPNAME.app"
  ditto "$APP" "/Applications/$APPNAME.app"
  "$LSREGISTER" -f "/Applications/$APPNAME.app"
fi

echo "==> 清理中间产物与系统登记"
rm -rf "$WORK"
# 系统会登记/收录每一份构建出的 .app（本次的中间目录、Xcode 的 DerivedData、编译检查的临时目录），
# 应用库因此会出现多个「柠檬纸飞机」；只保留 /Applications 那一份
"$HERE/clean-stale-apps.sh"
echo "完成：$DIST/LemonPaperplane-$VERSION-macos.zip"
[ "$INSTALL" = 1 ] && echo "已安装：/Applications/$APPNAME.app"
exit 0
