package engine

import (
	"bytes"
	"io/fs"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// 「能不能作为启动项运行」的统一判定（文件树右键、「添加脚本或程序」选择框都用它）：
//
//	script  脚本：shell 一类的扩展名（.sh .zsh .command .bat .ps1 …），或第一行是 #!
//	binary  程序：有执行权限且确实是可执行文件（Mach-O / ELF / PE），或 Windows 的 .exe/.com
//	source  其他语言源码（.py .js .rb … 没有 #!）：能跑，但多数是模块不是脚本，选择框默认不列
//	""      不能运行：Markdown、JSON、图片、文档等，哪怕带了执行权限（从 Windows 拷来的文件常见）
const (
	RunnableScript = "script"
	RunnableBinary = "binary"
	RunnableSource = "source"
)

var (
	scriptExts = map[string]bool{
		".sh": true, ".bash": true, ".zsh": true, ".command": true, ".fish": true, ".ksh": true,
		".ps1": true, ".bat": true, ".cmd": true, ".applescript": true, ".scpt": true,
	}
	sourceExts = map[string]bool{
		".py": true, ".pyw": true, ".js": true, ".mjs": true, ".cjs": true, ".ts": true, ".mts": true,
		".rb": true, ".pl": true, ".php": true, ".lua": true, ".r": true, ".swift": true, ".go": true,
		".groovy": true, ".kts": true, ".jar": true,
	}
	// 肯定不能运行的（文档、数据、配置、图片、样式、压缩包等），带执行权限也不算
	neverRunnableExts = map[string]bool{
		".md": true, ".markdown": true, ".txt": true, ".rst": true, ".adoc": true, ".pdf": true, ".doc": true, ".docx": true,
		".json": true, ".jsonc": true, ".yaml": true, ".yml": true, ".toml": true, ".ini": true, ".cfg": true, ".conf": true,
		".xml": true, ".plist": true, ".csv": true, ".tsv": true, ".log": true, ".lock": true, ".env": true, ".properties": true,
		".html": true, ".htm": true, ".css": true, ".scss": true, ".less": true, ".svg": true, ".png": true, ".jpg": true,
		".jpeg": true, ".gif": true, ".webp": true, ".ico": true, ".icns": true, ".bmp": true, ".mp3": true, ".mp4": true,
		".mov": true, ".wav": true, ".zip": true, ".gz": true, ".tgz": true, ".tar": true, ".7z": true, ".rar": true,
		".gitignore": true, ".gitattributes": true, ".editorconfig": true, ".d.ts": true, ".map": true, ".sql": true,
		".vue": true, ".tsx": true, ".jsx": true, ".java": true, ".kt": true, ".c": true, ".h": true, ".cpp": true, ".hpp": true,
		".m": true, ".mm": true, ".cs": true, ".dart": true, ".rs": true, ".gradle": true, ".pbxproj": true, ".storyboard": true,
		".xib": true, ".strings": true, ".woff": true, ".woff2": true, ".ttf": true, ".otf": true, ".so": true, ".dylib": true,
		".a": true, ".o": true, ".class": true, ".pyc": true,
	}
)

// runnableKind 判定一个普通文件能不能作为启动项运行（见上面的说明）。
func runnableKind(abs string, info fs.FileInfo) string {
	if info == nil || info.IsDir() {
		return ""
	}
	name := strings.ToLower(info.Name())
	ext := filepath.Ext(name)
	if strings.HasSuffix(name, ".d.ts") {
		ext = ".d.ts"
	}
	if neverRunnableExts[ext] {
		return ""
	}
	if runtime.GOOS == "windows" && (ext == ".exe" || ext == ".com") {
		return RunnableBinary
	}
	if scriptExts[ext] {
		return RunnableScript
	}
	exec := runtime.GOOS != "windows" && info.Mode().Perm()&0o111 != 0
	// 只有扩展名看不出来时才读文件头（#! 或可执行文件格式），避免逐个打开大量源码文件
	if exec || ext == "" || sourceExts[ext] {
		head := readHead(abs, 4)
		if bytes.HasPrefix(head, []byte("#!")) {
			return RunnableScript
		}
		if exec && isExecutableHeader(head) {
			return RunnableBinary
		}
	}
	if sourceExts[ext] {
		return RunnableSource
	}
	return ""
}

func readHead(abs string, n int) []byte {
	f, err := os.Open(abs)
	if err != nil {
		return nil
	}
	defer f.Close()
	b := make([]byte, n)
	m, _ := f.Read(b)
	return b[:m]
}

func isExecutableHeader(h []byte) bool {
	if len(h) < 4 {
		return false
	}
	for _, magic := range [][]byte{{0xcf, 0xfa, 0xed, 0xfe}, {0xce, 0xfa, 0xed, 0xfe}, {0xca, 0xfe, 0xba, 0xbe}, {0xfe, 0xed, 0xfa, 0xcf}, {0x7f, 'E', 'L', 'F'}} {
		if bytes.Equal(h[:4], magic) {
			return true
		}
	}
	return h[0] == 'M' && h[1] == 'Z'
}

// RunnableFile 是「添加脚本或程序」选择框的一项。
type RunnableFile struct {
	Path   string `json:"path"` // 相对工程根
	Kind   string `json:"kind"` // script | binary | source
	Exec   bool   `json:"exec,omitempty"`
	Size   int64  `json:"size"`
	Launch bool   `json:"launch,omitempty"` // 已经在启动里
}

// RunnableList 是 launch.candidates 的结果。
type RunnableList struct {
	Files     []RunnableFile `json:"files"`
	Truncated bool           `json:"truncated,omitempty"`
}

// LaunchCandidates 列出工程里能作为启动项运行的文件（脚本、程序；includeSources 时加上其他语言源码）。
// 跳过依赖、构建产物、隐藏目录（但 bin/ 要看，里面常放脚本和程序）。
func (e *Engine) LaunchCandidates(projectID string, includeSources bool, limit int) (*RunnableList, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil, ErrNotFound
	}
	if limit <= 0 || limit > 5000 {
		limit = 2000
	}
	inLaunch := map[string]bool{}
	for _, id := range launchIDs(p) {
		if _, c := findConfig(p, id); c != nil && c.Type == "script" {
			inLaunch[sdk.OptString(c.Options, "file", "")] = true
		}
	}
	root := p.Path
	out := &RunnableList{Files: []RunnableFile{}}
	scanned := 0
	_ = filepath.WalkDir(root, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			if d != nil && d.IsDir() {
				return fs.SkipDir
			}
			return nil
		}
		name := d.Name()
		if d.IsDir() {
			if path == root {
				return nil
			}
			if strings.HasPrefix(name, ".") || (name != "bin" && sdk.IsGeneratedDir(filepath.Dir(path), name)) {
				return fs.SkipDir
			}
			if rel := relOf(root, path); strings.Count(rel, "/") >= 7 {
				return fs.SkipDir
			}
			return nil
		}
		scanned++
		if scanned > 60000 {
			out.Truncated = true
			return fs.SkipAll
		}
		if !d.Type().IsRegular() && d.Type()&fs.ModeSymlink == 0 {
			return nil
		}
		info, err := os.Stat(path) // 跟随符号链接
		if err != nil || info.IsDir() {
			return nil
		}
		kind := runnableKind(path, info)
		if kind == "" || (kind == RunnableSource && !includeSources) {
			return nil
		}
		rel := relOf(root, path)
		out.Files = append(out.Files, RunnableFile{Path: rel, Kind: kind, Exec: info.Mode().Perm()&0o111 != 0, Size: info.Size(), Launch: inLaunch[rel]})
		if len(out.Files) >= limit {
			out.Truncated = true
			return fs.SkipAll
		}
		return nil
	})
	sort.Slice(out.Files, func(i, j int) bool {
		di, dj := filepath.Dir(out.Files[i].Path), filepath.Dir(out.Files[j].Path)
		if di != dj {
			if di == "." || dj == "." {
				return di == "." // 根目录的排最前
			}
			return di < dj
		}
		return out.Files[i].Path < out.Files[j].Path
	})
	return out, nil
}
