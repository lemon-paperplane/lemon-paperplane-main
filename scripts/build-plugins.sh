#!/bin/bash
# 构建内置插件：把 ../lemon-paperplane-plugin-* 编进一个多入口二进制 paperplane-plugins（按 argv[0] 分发），
# 并整理出插件目录（plugin.json、icons、l10n、resources、web + bin/ 指向多入口二进制的链接）。
#
# 用法：scripts/build-plugins.sh <输出目录> [GOOS] [GOARCH...]
#   输出：<输出目录>/paperplane-plugins（多架构时用 lipo 合成通用二进制）
#        <输出目录>/plugins/<id>/...
# 环境变量 PLUGIN_BIN_LINK：插件 bin 下链接指向的相对路径（默认 ../../../paperplane-plugins，即与 plugins 同级）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
MAIN="$(cd "$HERE/.." && pwd)"
ROOT="$(cd "$MAIN/.." && pwd)"
OUT="${1:?output dir}"
GOOS_="${2:-$(go env GOOS)}"
shift 2 2>/dev/null || shift $#
ARCHS=("$@")
[ ${#ARCHS[@]} -eq 0 ] && ARCHS=("$(go env GOARCH)")
LINK="${PLUGIN_BIN_LINK:-../../../paperplane-plugins}"

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
GEN="$MAIN/lemon-paperplane-main-core/bundled-plugins"
mkdir -p "$GEN"

# 1. 生成多入口模块
{
  echo "module github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/bundled-plugins"
  echo
  echo "go 1.23"
  echo
  echo "require github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk v0.0.0"
  echo "replace github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk => ../../lemon-paperplane-main-plugin-sdk"
} > "$GEN/go.mod"

# 只进商店、不内置的插件
STORE_ONLY="$(grep -v '^#' "$HERE/store-only.txt" 2>/dev/null | tr -s ' \n' ' ')"

IMPORTS=""
TABLE=""
IDS=()
i=0
for dir in "$ROOT"/lemon-paperplane-plugin-*; do
  [ -f "$dir/plugin.json" ] || continue
  read -r id exe < <(python3 -c "import json,sys;m=json.load(open(sys.argv[1]));print(m['id'],m.get('executable',''))" "$dir/plugin.json")
  case " $STORE_ONLY " in *" $id "*) continue ;; esac
  # 插件声明了 os 且不含目标系统（如 iOS 插件只在 macOS 上）：不打包
  python3 -c "import json,sys;m=json.load(open(sys.argv[1]));o=m.get('os') or [];sys.exit(0 if not o or sys.argv[2] in o else 1)" "$dir/plugin.json" "$GOOS_" || continue
  # 纯声明插件（没有 executable）：只整理文件，不进多入口二进制
  if [ -z "$exe" ]; then
    IDS+=("$id:$dir")
    continue
  fi
  [ -d "$dir/plugin" ] || continue
  mod="$(awk '/^module /{print $2; exit}' "$dir/go.mod")"
  name="$(basename "$dir")"
  echo "require $mod v0.0.0" >> "$GEN/go.mod"
  echo "replace $mod => ../../../$name" >> "$GEN/go.mod"
  alias="p$i"
  IMPORTS="$IMPORTS	$alias \"$mod/plugin\"
"
  TABLE="$TABLE	\"$id\": func() any { return $alias.New() },
"
  IDS+=("$id:$dir")
  i=$((i+1))
done

cat > "$GEN/main.go" <<EOF
// 由 scripts/build-plugins.sh 生成，不要手改。
// 内置插件多入口二进制：通过名为 paperplane-plugin-<id> 的链接启动时运行对应插件（每个插件仍是独立进程）。
package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
$IMPORTS)

var plugins = map[string]func() any{
$TABLE}

func main() {
	name := strings.TrimSuffix(filepath.Base(os.Args[0]), ".exe")
	id := strings.TrimPrefix(name, "paperplane-plugin-")
	if id == name && len(os.Args) > 1 {
		id = os.Args[1]
	}
	f := plugins[id]
	if f == nil {
		fmt.Fprintln(os.Stderr, "unknown plugin:", id)
		os.Exit(2)
	}
	sdk.Serve(f())
}
EOF
gofmt -w "$GEN/main.go"
(cd "$GEN" && GOFLAGS=-mod=mod go mod tidy >/dev/null 2>&1 || true)

# 2. 编译
BINS=()
for arch in "${ARCHS[@]}"; do
  out="$OUT/.paperplane-plugins-$GOOS_-$arch"
  ext=""; [ "$GOOS_" = "windows" ] && ext=".exe"
  (cd "$GEN" && CGO_ENABLED=0 GOOS="$GOOS_" GOARCH="$arch" go build -trimpath -ldflags "-s -w" -o "$out$ext" .)
  BINS+=("$out$ext")
done
if [ ${#BINS[@]} -gt 1 ]; then
  lipo -create -output "$OUT/paperplane-plugins" "${BINS[@]}"
  rm -f "${BINS[@]}"
else
  ext=""; [ "$GOOS_" = "windows" ] && ext=".exe"
  mv "${BINS[0]}" "$OUT/paperplane-plugins$ext"
fi

# 3. 整理插件目录
rm -rf "$OUT/plugins"
mkdir -p "$OUT/plugins"
for entry in "${IDS[@]}"; do
  id="${entry%%:*}"; dir="${entry#*:}"
  dst="$OUT/plugins/$id"
  mkdir -p "$dst/bin"
  cp "$dir/plugin.json" "$dst/"
  for sub in icons l10n resources web editor keymaps themes; do
    [ -d "$dir/$sub" ] && cp -R "$dir/$sub" "$dst/"
  done
  exe="$(python3 -c "import json,sys,os;print(os.path.basename(json.load(open(sys.argv[1])).get('executable','')))" "$dir/plugin.json")"
  if [ -z "$exe" ]; then
    rmdir "$dst/bin"
  elif [ "$GOOS_" = "windows" ]; then
    # Windows 没有符号链接：写一个指向多入口程序的 .bundled 文件，内核用插件 id 作参数启动它
    printf '%s' "$LINK.exe" > "$dst/bin/$exe.bundled"
  else
    ln -s "$LINK" "$dst/bin/$exe"
  fi
done
echo "bundled ${#IDS[@]} plugins → $OUT"
