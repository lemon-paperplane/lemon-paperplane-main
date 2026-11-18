#!/bin/bash
# 为插件商店构建各平台插件包与索引。
#
# 用法：scripts/build-store.sh <输出目录> <下载根地址> [插件目录...]
#   例：scripts/build-store.sh dist/store https://cdn.example.com/paperplane/plugins
#   下载根地址为 "." 时生成相对路径（随 App 附带的离线商店用）；环境变量 STORE_PLATFORMS 限定平台，如 "darwin/arm64 darwin/amd64"
#   不给插件目录时默认打包 scripts/store-only.txt 里列出的插件
# 产物：<输出目录>/<id>/<version>/<id>-<version>-<os>-<arch>.zip 与 <输出目录>/index.json
# 设置 → 插件商店索引地址 填 <下载根地址>/index.json 即可。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="${1:?output dir}"
BASE="${2:?base url}"
shift 2
DIRS=()
[ $# -gt 0 ] && DIRS=("$@")
if [ ${#DIRS[@]} -eq 0 ]; then
  for id in $(grep -v '^#' "$HERE/store-only.txt"); do
    [ -d "$ROOT/lemon-paperplane-plugin-$id" ] && DIRS+=("$ROOT/lemon-paperplane-plugin-$id")
  done
fi
if [ ${#DIRS[@]} -eq 0 ]; then
  # 没有要打包的插件：生成空索引（macOS 自带 bash 3.2 下空数组配合 set -u 会报错，这里提前返回）
  mkdir -p "$OUT"
  echo '{"plugins": []}' > "$OUT/index.json"
  echo "index: $OUT/index.json 0 plugins"
  exit 0
fi
PLATS="${STORE_PLATFORMS:-darwin/arm64 darwin/amd64 windows/amd64 windows/arm64 linux/amd64 linux/arm64}"
url() { if [ "$BASE" = "." ]; then echo "$1"; else echo "$BASE/$1"; fi; }

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
ENTRIES="$OUT/.entries"
: > "$ENTRIES"

for dir in "${DIRS[@]}"; do
  [ -f "$dir/plugin.json" ] || continue
  read -r id version exe < <(python3 -c "import json,sys,os;m=json.load(open(sys.argv[1]));print(m['id'],m['version'],os.path.basename(m.get('executable','')))" "$dir/plugin.json")
  dest="$OUT/$id/$version"
  mkdir -p "$dest"
  if [ -z "$exe" ]; then
    # 纯声明插件：没有程序，一个包通吃所有平台（索引里平台记为 any）
    stage="$(mktemp -d)"
    cp "$dir/plugin.json" "$stage/"
    for sub in icons l10n resources web editor keymaps themes; do [ -d "$dir/$sub" ] && cp -R "$dir/$sub" "$stage/"; done
    zip="$dest/$id-$version.zip"
    rm -f "$zip"
    (cd "$stage" && zip -qr "$zip" .)
    rm -rf "$stage"
    sha="$(shasum -a 256 "$zip" | awk '{print $1}')"
    size="$(stat -f%z "$zip" 2>/dev/null || stat -c%s "$zip")"
    echo "$dir	$id	$version	any	$(url "$id/$version/$(basename "$zip")")	$sha	$size" >> "$ENTRIES"
    [ -f "$dir/icons/$id.svg" ] && cp "$dir/icons/$id.svg" "$OUT/$id/icon.svg" || true
    echo "packed $id $version (any)"
    continue
  fi
  cmd="$(ls -d "$dir"/cmd/*/ | head -1)"
  for p in $PLATS; do
    os="${p%/*}"; arch="${p#*/}"
    stage="$(mktemp -d)"
    cp "$dir/plugin.json" "$stage/"
    for sub in icons l10n resources web editor keymaps themes; do [ -d "$dir/$sub" ] && cp -R "$dir/$sub" "$stage/"; done
    ext=""; [ "$os" = "windows" ] && ext=".exe"
    mkdir -p "$stage/bin/$os-$arch"
    (cd "$dir" && CGO_ENABLED=0 GOOS=$os GOARCH=$arch go build -trimpath -ldflags "-s -w" -o "$stage/bin/$os-$arch/$exe$ext" "./${cmd#$dir/}")
    zip="$dest/$id-$version-$os-$arch.zip"
    rm -f "$zip"
    (cd "$stage" && zip -qr "$zip" .)
    rm -rf "$stage"
    sha="$(shasum -a 256 "$zip" | awk '{print $1}')"
    size="$(stat -f%z "$zip" 2>/dev/null || stat -c%s "$zip")"
    echo "$dir	$id	$version	$os-$arch	$(url "$id/$version/$(basename "$zip")")	$sha	$size" >> "$ENTRIES"
  done
  [ -f "$dir/icons/$id.svg" ] && cp "$dir/icons/$id.svg" "$OUT/$id/icon.svg" || true
  echo "packed $id $version"
done

python3 - "$ENTRIES" "$OUT/index.json" "$BASE" <<'PY'
import json, sys, os
entries, out, base = sys.argv[1], sys.argv[2], sys.argv[3]
plugins = {}
for line in open(entries):
    d, pid, ver, plat, url, sha, size = line.rstrip("\n").split("\t")
    if pid not in plugins:
        m = json.load(open(os.path.join(d, "plugin.json")))
        def loc(s):
            if s.startswith("%") and s.endswith("%"):
                try:
                    return json.load(open(os.path.join(d, "l10n", "en.json"))).get(s[1:-1], s)
                except Exception:
                    return s
            return s
        plugins[pid] = {"id": pid, "name": loc(m.get("name", pid)), "version": ver, "vendor": m.get("vendor", ""),
                        "description": loc(m.get("description", "")), "engines": m.get("engines", {}),
                        "icon": (f"{pid}/icon.svg" if base == "." else f"{base}/{pid}/icon.svg"), "platforms": {}}
    plugins[pid]["platforms"][plat] = {"url": url, "sha256": sha, "size": int(size)}
json.dump({"plugins": sorted(plugins.values(), key=lambda p: p["id"])}, open(out, "w"), indent=2, ensure_ascii=False)
print("index:", out, len(plugins), "plugins")
PY
rm -f "$ENTRIES"
