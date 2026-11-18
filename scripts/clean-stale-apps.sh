#!/bin/bash
# 清掉系统里多余的 Lemon Paperplane.app，启动台/应用库只留 /Applications 那一份。
#
# 每次 xcodebuild（Xcode 里 ⌘B、命令行编译检查）都会在 DerivedData 或临时目录产出一份 .app，
# 系统会登记并被 Spotlight 收录，应用库里就会多出一个「柠檬纸飞机」。
# 规则：构建产物（DerivedData、临时目录、dist 中间目录）注销登记并删除；
#      其他位置的副本只注销登记并列出来，不删（可能是用户自己放的）。
#
# 用法：scripts/clean-stale-apps.sh        （scripts/build-macos.sh 结束时会自动调用）
set -uo pipefail

APPNAME="Lemon Paperplane"
BUNDLE_ID="cn.lemonit.paperplane"
KEEP="/Applications/$APPNAME.app"
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

{
  "$LSREGISTER" -dump 2>/dev/null | awk '/^path:/{ $1=""; sub(/^ +/,""); sub(/ \(0x[0-9a-f]+\)$/,""); print }'
  mdfind "kMDItemCFBundleIdentifier == '$BUNDLE_ID'" 2>/dev/null
} | grep -E "/$APPNAME\.app$" | sort -u | while IFS= read -r p; do
  [ "$p" = "$KEEP" ] && continue
  "$LSREGISTER" -u "$p" >/dev/null 2>&1 || true
  case "$p" in
    */DerivedData/*|/private/tmp/*|/tmp/*|/private/var/folders/*|/var/folders/*|*/dist/.work-macos/*)
      if [ -d "$p" ]; then
        rm -rf "${p:?}"
        echo "已删除多余副本：$p"
      else
        echo "已注销登记（文件已不在）：$p"
      fi
      ;;
    *)
      echo "已注销登记，未删除（不是构建产物，请自行确认）：$p"
      ;;
  esac
done
exit 0
