package sdk

import (
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"io/fs"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
)

// DefaultSkipDirs 是扫描工程时默认跳过的目录名。
var DefaultSkipDirs = map[string]bool{
	".git": true, ".svn": true, ".hg": true, ".idea": true, ".vscode": true, ".fleet": true,
	"node_modules": true, "bower_components": true, ".pnpm-store": true, ".yarn": true,
	"build": true, "dist": true, "out": true, "target": true, "bin": true, "obj": true,
	".gradle": true, ".mvn": true, ".kotlin": true,
	".venv": true, "venv": true, "env": true, "__pycache__": true, ".pytest_cache": true, ".mypy_cache": true, ".tox": true, "site-packages": true,
	"Pods": true, "DerivedData": true, ".build": true, ".swiftpm": true,
	".next": true, ".nuxt": true, ".output": true, ".svelte-kit": true, ".turbo": true, ".cache": true, ".parcel-cache": true,
	"unpackage": true, "coverage": true, "vendor": true, "third_party": true, "third-party": true,
}

// projectGeneratedDirs：某类工程根目录下由工具自己生成的目录（缓存、临时文件、构建产物），
// 只有父目录确实是那类工程时才跳过（这些名字在别处可能是正常目录，如 Library）。
var projectGeneratedDirs = []struct {
	markers []string // 父目录里同时存在这些（以 / 结尾的是目录）
	dirs    map[string]bool
}{
	// Unity：Library/PackageCache 里每个包都带 package.json（是 Unity 包清单不是 npm），Library 往往有几个 GB
	{[]string{"Assets/", "ProjectSettings/"}, map[string]bool{
		"Library": true, "Temp": true, "Logs": true, "UserSettings": true, "Obj": true,
		"Build": true, "Builds": true, "MemoryCaptures": true, "Recordings": true,
	}},
}

// IsGeneratedDir 判断 parent 下名为 name 的目录是否应在扫描时跳过：通用跳过列表，
// 或父目录是某类工程（如 Unity）时它生成的目录。
func IsGeneratedDir(parent, name string) bool {
	if DefaultSkipDirs[name] {
		return true
	}
	for _, g := range projectGeneratedDirs {
		if !g.dirs[name] {
			continue
		}
		ok := true
		for _, m := range g.markers {
			fi, err := os.Stat(filepath.Join(parent, strings.TrimSuffix(m, "/")))
			if err != nil || fi.IsDir() != strings.HasSuffix(m, "/") {
				ok = false
				break
			}
		}
		if ok {
			return true
		}
	}
	return false
}

// FindOptions 控制 FindFiles。
type FindOptions struct {
	MaxDepth int             // 相对 root 的最大深度，0 表示只看 root
	Names    []string        // 文件名 glob（filepath.Match 语法），空表示全部文件
	SkipDirs map[string]bool // 为空用 DefaultSkipDirs
	Limit    int             // 最多返回多少个，0 表示 5000
	Dirs     bool            // 匹配目录而不是文件
}

// FindFiles 在 root 下按名字查找文件，返回绝对路径（按路径排序）。
func FindFiles(root string, opt FindOptions) []string {
	skip := opt.SkipDirs
	if skip == nil {
		skip = DefaultSkipDirs
	}
	limit := opt.Limit
	if limit == 0 {
		limit = 5000
	}
	var out []string
	rootDepth := strings.Count(filepath.Clean(root), string(filepath.Separator))
	_ = filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			if d != nil && d.IsDir() {
				return fs.SkipDir
			}
			return nil
		}
		depth := strings.Count(filepath.Clean(path), string(filepath.Separator)) - rootDepth
		if d.IsDir() {
			if path != root && (skip[d.Name()] || (strings.HasPrefix(d.Name(), ".") && d.Name() != ".mvn") ||
				(opt.SkipDirs == nil && IsGeneratedDir(filepath.Dir(path), d.Name()))) {
				return fs.SkipDir
			}
			if opt.Dirs && path != root && matchAny(d.Name(), opt.Names) {
				out = append(out, path)
			}
			if depth >= opt.MaxDepth && path != root {
				return fs.SkipDir
			}
			if depth > opt.MaxDepth {
				return fs.SkipDir
			}
			return nil
		}
		if !opt.Dirs && depth <= opt.MaxDepth+1 && (len(opt.Names) == 0 || matchAny(d.Name(), opt.Names)) {
			out = append(out, path)
		}
		if len(out) >= limit {
			return fs.SkipAll
		}
		return nil
	})
	sort.Strings(out)
	return out
}

func matchAny(name string, patterns []string) bool {
	for _, p := range patterns {
		if ok, _ := filepath.Match(p, name); ok {
			return true
		}
	}
	return false
}

// MatchFileName 判断文件名是否匹配任一 glob。
func MatchFileName(name string, patterns []string) bool { return matchAny(name, patterns) }

// FileExists 判断是普通文件。
func FileExists(path string) bool {
	st, err := os.Stat(path)
	return err == nil && !st.IsDir()
}

// DirExists 判断是目录。
func DirExists(path string) bool {
	st, err := os.Stat(path)
	return err == nil && st.IsDir()
}

// ReadText 读取文本文件，超过 max 字节截断（max<=0 表示 4 MB）。
func ReadText(path string, max int) string {
	if max <= 0 {
		max = 4 << 20
	}
	f, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer f.Close()
	buf := make([]byte, max)
	n, _ := f.Read(buf)
	return string(buf[:n])
}

// ReadJSON 读取 JSON 文件到 v。
func ReadJSON(path string, v any) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}

// HasFile 判断文件名列表（DetectParams.Files）里是否有匹配项。
func HasFile(files []string, patterns ...string) bool {
	for _, f := range files {
		if matchAny(f, patterns) {
			return true
		}
	}
	return false
}

// StableID 由若干字符串生成稳定的短 id（用于运行配置 id，保证重新导入时能合并）。
func StableID(parts ...string) string {
	h := sha1.Sum([]byte(strings.Join(parts, "\x00")))
	return hex.EncodeToString(h[:])[:12]
}

// ExeName 在 Windows 上补 .exe。
func ExeName(name string) string {
	if runtime.GOOS == "windows" && !strings.HasSuffix(strings.ToLower(name), ".exe") {
		return name + ".exe"
	}
	return name
}

// ToolchainEnv 合并若干工具链的环境变量与 PATH 前缀。
func ToolchainEnv(tcs ...Toolchain) (map[string]string, []string) {
	env := map[string]string{}
	var path []string
	for _, tc := range tcs {
		for k, v := range tc.Env {
			env[k] = v
		}
		path = append(path, tc.PathPrepend...)
	}
	return env, path
}

// MergeEnv 依次覆盖合并，返回新 map。
func MergeEnv(layers ...map[string]string) map[string]string {
	out := map[string]string{}
	for _, l := range layers {
		for k, v := range l {
			out[k] = v
		}
	}
	return out
}

// HomeDir 返回用户主目录。
func HomeDir() string {
	h, _ := os.UserHomeDir()
	return h
}

// Expand 展开开头的 ~。
func Expand(p string) string {
	if strings.HasPrefix(p, "~/") || p == "~" {
		return filepath.Join(HomeDir(), strings.TrimPrefix(p, "~"))
	}
	return p
}

// OptString 从运行配置 Options 取字符串。
func OptString(opts map[string]any, key, def string) string {
	if v, ok := opts[key]; ok {
		switch t := v.(type) {
		case string:
			if t != "" {
				return t
			}
		case nil:
		default:
			b, _ := json.Marshal(t)
			return string(b)
		}
	}
	return def
}

// OptBool 从运行配置 Options 取布尔。
func OptBool(opts map[string]any, key string, def bool) bool {
	if v, ok := opts[key].(bool); ok {
		return v
	}
	return def
}

// OptStrings 从运行配置 Options 取字符串数组（也接受逗号/空白分隔的字符串）。
func OptStrings(opts map[string]any, key string) []string {
	switch t := opts[key].(type) {
	case []any:
		var out []string
		for _, x := range t {
			if s, ok := x.(string); ok && s != "" {
				out = append(out, s)
			}
		}
		return out
	case []string:
		return t
	case string:
		return strings.FieldsFunc(t, func(r rune) bool { return r == ',' })
	}
	return nil
}

// SplitArgs 按 shell 规则（简化：支持单双引号与反斜杠）切分参数字符串。
func SplitArgs(s string) []string {
	var out []string
	var cur strings.Builder
	inS, inD, has := false, false, false
	for i := 0; i < len(s); i++ {
		c := s[i]
		switch {
		case c == '\\' && !inS && i+1 < len(s):
			i++
			cur.WriteByte(s[i])
			has = true
		case c == '\'' && !inD:
			inS = !inS
			has = true
		case c == '"' && !inS:
			inD = !inD
			has = true
		case (c == ' ' || c == '\t' || c == '\n') && !inS && !inD:
			if has {
				out = append(out, cur.String())
				cur.Reset()
				has = false
			}
		default:
			cur.WriteByte(c)
			has = true
		}
	}
	if has {
		out = append(out, cur.String())
	}
	return out
}
