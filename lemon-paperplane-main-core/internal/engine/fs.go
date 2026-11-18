package engine

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"errors"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"time"
	"unicode/utf8"

	"golang.org/x/text/encoding"
	"golang.org/x/text/encoding/charmap"
	"golang.org/x/text/encoding/simplifiedchinese"
	"golang.org/x/text/encoding/traditionalchinese"
	"golang.org/x/text/encoding/unicode"
	"golang.org/x/text/transform"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// 文件读写的大小限制（docs/editor.md 第 2 节）。
const (
	fsReadOnlyBytes = 5 << 20  // 超过只读打开
	fsMaxBytes      = 50 << 20 // 超过不打开
	fsGrepMaxFile   = 2 << 20
)

// FSEntry 是目录里的一项。
type FSEntry struct {
	Name    string `json:"name"`
	Path    string `json:"path"` // 相对工程根，"/" 分隔
	Dir     bool   `json:"dir"`
	Size    int64  `json:"size"`
	MTime   string `json:"mtime"`
	Symlink bool   `json:"symlink,omitempty"`
	Hidden  bool   `json:"hidden,omitempty"`
	Ignored bool   `json:"ignored,omitempty"`
	Exec    bool   `json:"exec,omitempty"` // 普通文件且有执行权限（Windows 上恒为 false）
	// Runnable 能否作为启动项运行：script | binary | source | ""（见 runnable.go）
	Runnable string `json:"runnable,omitempty"`
}

// FSList 是 fs.list 的返回。
type FSList struct {
	Entries   []FSEntry `json:"entries"`
	Truncated bool      `json:"truncated"`
}

// FSFile 是 fs.read 的返回。
type FSFile struct {
	Path       string `json:"path"`
	Content    string `json:"content"`
	Encoding   string `json:"encoding"`
	BOM        bool   `json:"bom,omitempty"`
	LineEnding string `json:"lineEnding"` // lf | crlf | mixed
	// PreferredLineEnding 是 mixed 时占多数的换行符（lf | crlf），保存时用它
	PreferredLineEnding string `json:"preferredLineEnding,omitempty"`
	Size                int64  `json:"size"`
	MTime               string `json:"mtime"`
	Binary              bool   `json:"binary,omitempty"`
	TooLarge            bool   `json:"tooLarge,omitempty"`
	ReadOnly            bool   `json:"readOnly,omitempty"`
	// Image 为 true 时是可预览的图片；不超过 fsImageBytes 时 DataURL 带内容（data:image/...;base64,）
	Image   bool   `json:"image,omitempty"`
	DataURL string `json:"dataUrl,omitempty"`
}

// fsImageBytes 是 fs.read 随结果返回图片内容的上限。
const fsImageBytes = 8 << 20

var imageTypes = map[string]string{
	".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".gif": "image/gif",
	".webp": "image/webp", ".bmp": "image/bmp", ".ico": "image/x-icon", ".avif": "image/avif",
}

// FSStat 是 fs.stat 的一项。
type FSStat struct {
	Path   string `json:"path"`
	Exists bool   `json:"exists"`
	Dir    bool   `json:"dir,omitempty"`
	Size   int64  `json:"size,omitempty"`
	MTime  string `json:"mtime,omitempty"`
}

// FSMatch 是 fs.grep 的一项。
type FSMatch struct {
	Path   string `json:"path"`
	Line   int    `json:"line"`
	Column int    `json:"column"`
	Text   string `json:"text"`
}

// FSSearchHit 是 fs.search 的一项。
type FSSearchHit struct {
	Path  string `json:"path"`
	Name  string `json:"name"`
	Score int    `json:"score"`
}

func mtimeString(t time.Time) string { return t.UTC().Format(time.RFC3339Nano) }

// projectPath 把工程内相对路径解析为绝对路径，拒绝越出工程根（含符号链接）。
func (e *Engine) projectPath(projectID, rel string) (root, abs string, err error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return "", "", ErrNotFound
	}
	root = filepath.Clean(p.Path)
	rel = strings.TrimPrefix(filepath.ToSlash(rel), "/")
	abs = filepath.Clean(filepath.Join(root, filepath.FromSlash(rel)))
	if abs != root && !strings.HasPrefix(abs, root+string(filepath.Separator)) {
		return "", "", Errf("err.pathOutside", rel)
	}
	// 已存在的部分解析符号链接后仍须在工程内（工程根本身是链接时以解析后的根为准）
	realRoot, _ := filepath.EvalSymlinks(root)
	if realRoot == "" {
		realRoot = root
	}
	check := abs
	for {
		if real, err := filepath.EvalSymlinks(check); err == nil {
			if real != realRoot && !strings.HasPrefix(real, realRoot+string(filepath.Separator)) {
				return "", "", Errf("err.pathOutside", rel)
			}
			break
		}
		parent := filepath.Dir(check)
		if parent == check {
			break
		}
		check = parent
	}
	return root, abs, nil
}

func relOf(root, abs string) string {
	r, _ := filepath.Rel(root, abs)
	if r == "." {
		return ""
	}
	return filepath.ToSlash(r)
}

// FSListDir 列出目录（目录在前，按名称不区分大小写排序）。
func (e *Engine) FSListDir(projectID, rel string) (*FSList, error) {
	root, abs, err := e.projectPath(projectID, rel)
	if err != nil {
		return nil, err
	}
	entries, err := os.ReadDir(abs)
	if err != nil {
		return nil, err
	}
	out := &FSList{Entries: []FSEntry{}}
	const limit = 5000
	for _, en := range entries {
		name := en.Name()
		if name == ".git" || name == ".DS_Store" {
			continue
		}
		if len(out.Entries) >= limit {
			out.Truncated = true
			break
		}
		full := filepath.Join(abs, name)
		info, err := os.Stat(full) // 跟随符号链接
		isLink := en.Type()&os.ModeSymlink != 0
		if err != nil {
			if isLink {
				out.Entries = append(out.Entries, FSEntry{Name: name, Path: relOf(root, full), Symlink: true})
			}
			continue
		}
		fe := FSEntry{Name: name, Path: relOf(root, full), Dir: info.IsDir(), Size: info.Size(), MTime: mtimeString(info.ModTime()), Symlink: isLink, Hidden: strings.HasPrefix(name, ".")}
		if !fe.Dir && runtime.GOOS != "windows" && info.Mode().Perm()&0o111 != 0 {
			fe.Exec = true
		}
		if !fe.Dir {
			fe.Runnable = runnableKind(full, info)
		}
		if fe.Dir && sdk.IsGeneratedDir(abs, name) {
			fe.Ignored = true
		}
		out.Entries = append(out.Entries, fe)
	}
	sort.SliceStable(out.Entries, func(i, j int) bool {
		a, b := out.Entries[i], out.Entries[j]
		if a.Dir != b.Dir {
			return a.Dir
		}
		return strings.ToLower(a.Name) < strings.ToLower(b.Name)
	})
	return out, nil
}

// FSRead 读取文本文件并识别编码与换行符。
// FSRead 读取工程文件。encoding 非空时按指定编码解码（「用某编码重新打开」），否则自动识别。
func (e *Engine) FSRead(projectID, rel, forceEnc string) (*FSFile, error) {
	root, abs, err := e.projectPath(projectID, rel)
	if err != nil {
		return nil, err
	}
	info, err := os.Stat(abs)
	if err != nil {
		return nil, err
	}
	if info.IsDir() {
		return nil, Errf("err.isDir", rel)
	}
	f := &FSFile{Path: relOf(root, abs), Size: info.Size(), MTime: mtimeString(info.ModTime()), LineEnding: "lf", Encoding: "utf-8"}
	if mime := imageTypes[strings.ToLower(filepath.Ext(abs))]; mime != "" {
		f.Image, f.Binary = true, true
		if info.Size() <= fsImageBytes {
			data, err := os.ReadFile(abs)
			if err != nil {
				return nil, err
			}
			f.DataURL = "data:" + mime + ";base64," + base64.StdEncoding.EncodeToString(data)
		}
		return f, nil
	}
	if info.Size() > fsMaxBytes {
		f.TooLarge = true
		return f, nil
	}
	data, err := os.ReadFile(abs)
	if err != nil {
		return nil, err
	}
	if info.Size() > fsReadOnlyBytes {
		f.ReadOnly = true
	}
	if fi, err := os.Stat(abs); err == nil && fi.Mode().Perm()&0o222 == 0 {
		f.ReadOnly = true
	}
	var (
		text, enc   string
		bom, binary bool
	)
	if forceEnc != "" {
		text, enc, bom, err = decodeAs(data, forceEnc)
		if err != nil {
			return nil, err
		}
	} else {
		text, enc, bom, binary = decodeText(data)
	}
	if binary {
		f.Binary = true
		return f, nil
	}
	f.Encoding, f.BOM = enc, bom
	crlf := strings.Count(text, "\r\n")
	lf := strings.Count(text, "\n") - crlf
	switch {
	case crlf > 0 && lf == 0:
		f.LineEnding = "crlf"
	case crlf > 0 && lf > 0:
		f.LineEnding = "mixed"
		f.PreferredLineEnding = "lf"
		if crlf > lf {
			f.PreferredLineEnding = "crlf"
		}
	}
	// 编辑器内部统一用 \n，写回时按 lineEnding 还原
	f.Content = strings.ReplaceAll(text, "\r\n", "\n")
	return f, nil
}

var encodings = map[string]encoding.Encoding{
	"gb18030":  simplifiedchinese.GB18030,
	"gbk":      simplifiedchinese.GBK,
	"big5":     traditionalchinese.Big5,
	"latin1":   charmap.ISO8859_1,
	"utf-16le": unicode.UTF16(unicode.LittleEndian, unicode.IgnoreBOM),
	"utf-16be": unicode.UTF16(unicode.BigEndian, unicode.IgnoreBOM),
}

// decodeAs 按指定编码解码；带对应 BOM 时去掉 BOM。
func decodeAs(data []byte, name string) (text, enc string, bom bool, err error) {
	name = strings.ToLower(strings.TrimSpace(name))
	switch name {
	case "utf-8", "utf8":
		if bytes.HasPrefix(data, []byte{0xEF, 0xBB, 0xBF}) {
			return string(data[3:]), "utf-8", true, nil
		}
		return strings.ToValidUTF8(string(data), "\uFFFD"), "utf-8", false, nil
	case "utf-16le":
		bom = bytes.HasPrefix(data, []byte{0xFF, 0xFE})
	case "utf-16be":
		bom = bytes.HasPrefix(data, []byte{0xFE, 0xFF})
	}
	dec, ok := encodings[name]
	if !ok {
		return "", "", false, Errf("err.badEncoding", name)
	}
	if bom {
		data = data[2:]
	}
	s, _, err := transform.Bytes(dec.NewDecoder(), data)
	if err != nil {
		return "", "", false, err
	}
	return string(s), name, bom, nil
}

// decodeText 识别编码：BOM → UTF-8 → GB18030 → Big5 → Latin-1；含 NUL 且不是 UTF-16 视为二进制。
func decodeText(data []byte) (text, enc string, bom, binary bool) {
	switch {
	case bytes.HasPrefix(data, []byte{0xEF, 0xBB, 0xBF}):
		return string(data[3:]), "utf-8", true, false
	case bytes.HasPrefix(data, []byte{0xFF, 0xFE}):
		s, _, err := transform.Bytes(encodings["utf-16le"].NewDecoder(), data[2:])
		if err == nil {
			return string(s), "utf-16le", true, false
		}
	case bytes.HasPrefix(data, []byte{0xFE, 0xFF}):
		s, _, err := transform.Bytes(encodings["utf-16be"].NewDecoder(), data[2:])
		if err == nil {
			return string(s), "utf-16be", true, false
		}
	}
	probe := data
	if len(probe) > 8000 {
		probe = probe[:8000]
	}
	if bytes.IndexByte(probe, 0) >= 0 {
		return "", "", false, true
	}
	if utf8.Valid(data) {
		return string(data), "utf-8", false, false
	}
	for _, name := range []string{"gb18030", "big5"} {
		if s, _, err := transform.Bytes(encodings[name].NewDecoder(), data); err == nil && utf8.Valid(s) && !bytes.ContainsRune(s, utf8.RuneError) {
			return string(s), name, false, false
		}
	}
	s, _, _ := transform.Bytes(encodings["latin1"].NewDecoder(), data)
	return string(s), "latin1", false, false
}

// FSWriteParams 是 fs.write 的输入。
type FSWriteParams struct {
	ProjectID     string `json:"projectId" required:"true"`
	Path          string `json:"path" required:"true"`
	Content       string `json:"content"`
	Encoding      string `json:"encoding,omitempty" desc:"utf-8 (default), gb18030, gbk, big5, latin1, utf-16le, utf-16be"`
	BOM           bool   `json:"bom,omitempty"`
	LineEnding    string `json:"lineEnding,omitempty" desc:"lf (default) or crlf"`
	ExpectedMTime string `json:"expectedMtime,omitempty" desc:"mtime returned by fs.read; the write fails with reason modifiedExternally when the file changed since"`
	Create        bool   `json:"create,omitempty" desc:"Allow creating a new file."`
}

// FSWrite 原子写入文件（保留权限），可检测外部修改。
func (e *Engine) FSWrite(p *FSWriteParams) (*FSStat, error) {
	root, abs, err := e.projectPath(p.ProjectID, p.Path)
	if err != nil {
		return nil, err
	}
	perm := os.FileMode(0o644)
	info, err := os.Stat(abs)
	switch {
	case err == nil:
		if info.IsDir() {
			return nil, Errf("err.isDir", p.Path)
		}
		perm = info.Mode().Perm()
		if p.ExpectedMTime != "" && mtimeString(info.ModTime()) != p.ExpectedMTime {
			return nil, &sdk.Error{Code: sdk.ErrCodeUserError, Message: i18n.T("err.modifiedExternally", p.Path), Data: map[string]any{"reason": "modifiedExternally", "mtime": mtimeString(info.ModTime())}}
		}
	case errors.Is(err, os.ErrNotExist):
		if !p.Create && p.ExpectedMTime != "" {
			return nil, &sdk.Error{Code: sdk.ErrCodeUserError, Message: i18n.T("err.deletedExternally", p.Path), Data: map[string]any{"reason": "deletedExternally"}}
		}
		if err := os.MkdirAll(filepath.Dir(abs), 0o755); err != nil {
			return nil, err
		}
	default:
		return nil, err
	}
	text := strings.ReplaceAll(p.Content, "\r\n", "\n")
	if p.LineEnding == "crlf" {
		text = strings.ReplaceAll(text, "\n", "\r\n")
	}
	var data []byte
	enc := strings.ToLower(p.Encoding)
	switch enc {
	case "", "utf-8", "utf8":
		data = []byte(text)
		if p.BOM {
			data = append([]byte{0xEF, 0xBB, 0xBF}, data...)
		}
	default:
		codec := encodings[enc]
		if codec == nil {
			return nil, Errf("err.unknownEncoding", p.Encoding)
		}
		data, _, err = transform.Bytes(codec.NewEncoder(), []byte(text))
		if err != nil {
			return nil, Errf("err.encodeFailed", p.Encoding)
		}
		if p.BOM && enc == "utf-16le" {
			data = append([]byte{0xFF, 0xFE}, data...)
		} else if p.BOM && enc == "utf-16be" {
			data = append([]byte{0xFE, 0xFF}, data...)
		}
	}
	tmp, err := os.CreateTemp(filepath.Dir(abs), "."+filepath.Base(abs)+".paperplane-*")
	if err != nil {
		return nil, err
	}
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		os.Remove(tmp.Name())
		return nil, err
	}
	tmp.Close()
	_ = os.Chmod(tmp.Name(), perm)
	if err := os.Rename(tmp.Name(), abs); err != nil {
		os.Remove(tmp.Name())
		return nil, err
	}
	ni, err := os.Stat(abs)
	if err != nil {
		return nil, err
	}
	e.afterFileChange(p.ProjectID)
	return &FSStat{Path: relOf(root, abs), Exists: true, Size: ni.Size(), MTime: mtimeString(ni.ModTime())}, nil
}

// afterFileChange：文件改动可能影响 Git 摘要与工程识别。
func (e *Engine) afterFileChange(projectID string) {
	go e.refreshVCSBrief(context.Background(), projectID)
}

// FSStatPaths 批量查询文件状态。
func (e *Engine) FSStatPaths(projectID string, paths []string) ([]FSStat, error) {
	out := []FSStat{}
	for _, rel := range paths {
		root, abs, err := e.projectPath(projectID, rel)
		if err != nil {
			return nil, err
		}
		st := FSStat{Path: relOf(root, abs)}
		if info, err := os.Stat(abs); err == nil {
			st.Exists, st.Dir, st.Size, st.MTime = true, info.IsDir(), info.Size(), mtimeString(info.ModTime())
		}
		out = append(out, st)
	}
	return out, nil
}

// FSCreate 新建文件或目录。
func (e *Engine) FSCreate(projectID, rel string, dir bool) (string, error) {
	root, abs, err := e.projectPath(projectID, rel)
	if err != nil {
		return "", err
	}
	if _, err := os.Lstat(abs); err == nil {
		return "", Errf("err.exists", rel)
	}
	if dir {
		err = os.MkdirAll(abs, 0o755)
	} else {
		if err = os.MkdirAll(filepath.Dir(abs), 0o755); err == nil {
			var f *os.File
			if f, err = os.OpenFile(abs, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644); err == nil {
				f.Close()
			}
		}
	}
	if err != nil {
		return "", err
	}
	e.afterFileChange(projectID)
	return relOf(root, abs), nil
}

// FSRename 重命名/移动（工程内）。
func (e *Engine) FSRename(projectID, from, to string) (string, error) {
	_, src, err := e.projectPath(projectID, from)
	if err != nil {
		return "", err
	}
	root, dst, err := e.projectPath(projectID, to)
	if err != nil {
		return "", err
	}
	if _, err := os.Lstat(dst); err == nil && !strings.EqualFold(src, dst) {
		return "", Errf("err.exists", to)
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return "", err
	}
	if err := os.Rename(src, dst); err != nil {
		return "", err
	}
	e.afterFileChange(projectID)
	return relOf(root, dst), nil
}

// FSDelete 把文件移到废纸篓（不直接删除）。
func (e *Engine) FSDelete(projectID string, paths []string) ([]string, error) {
	var trashed []string
	for _, rel := range paths {
		root, abs, err := e.projectPath(projectID, rel)
		if err != nil {
			return trashed, err
		}
		if abs == root {
			return trashed, Errf("err.deleteRoot")
		}
		if err := moveToTrash(abs); err != nil {
			return trashed, err
		}
		trashed = append(trashed, relOf(root, abs))
	}
	e.afterFileChange(projectID)
	return trashed, nil
}

// moveToTrash 按平台移到废纸篓/回收站。
func moveToTrash(abs string) error {
	home, _ := os.UserHomeDir()
	switch runtime.GOOS {
	case "darwin":
		trash := filepath.Join(home, ".Trash")
		return os.Rename(abs, uniqueName(trash, filepath.Base(abs)))
	case "windows":
		ps := `Add-Type -AssemblyName Microsoft.VisualBasic; $p=$args[0]; if (Test-Path -LiteralPath $p -PathType Container) { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($p,'OnlyErrorDialogs','SendToRecycleBin') } else { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($p,'OnlyErrorDialogs','SendToRecycleBin') }`
		return exec.Command("powershell", "-NoProfile", "-Command", ps, abs).Run()
	default:
		// freedesktop 回收站规范
		base := os.Getenv("XDG_DATA_HOME")
		if base == "" {
			base = filepath.Join(home, ".local", "share")
		}
		files := filepath.Join(base, "Trash", "files")
		info := filepath.Join(base, "Trash", "info")
		_ = os.MkdirAll(files, 0o700)
		_ = os.MkdirAll(info, 0o700)
		dst := uniqueName(files, filepath.Base(abs))
		meta := "[Trash Info]\nPath=" + abs + "\nDeletionDate=" + time.Now().Format("2006-01-02T15:04:05") + "\n"
		if err := os.WriteFile(filepath.Join(info, filepath.Base(dst)+".trashinfo"), []byte(meta), 0o600); err != nil {
			return err
		}
		return os.Rename(abs, dst)
	}
}

func uniqueName(dir, name string) string {
	p := filepath.Join(dir, name)
	if _, err := os.Lstat(p); err != nil {
		return p
	}
	ext := filepath.Ext(name)
	stem := strings.TrimSuffix(name, ext)
	return filepath.Join(dir, stem+" "+time.Now().Format("15.04.05.000")+ext)
}

// walkProject 遍历工程文件（跳过 ignored 目录与 .git），fn 返回 false 时停止。
func walkProject(root string, fn func(rel string, d fs.DirEntry) bool) {
	_ = filepath.WalkDir(root, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			if d != nil && d.IsDir() {
				return fs.SkipDir
			}
			return nil
		}
		if d.IsDir() {
			if p != root && (sdk.IsGeneratedDir(filepath.Dir(p), d.Name()) || d.Name() == ".git") {
				return fs.SkipDir
			}
			return nil
		}
		if !fn(relOf(root, p), d) {
			return fs.SkipAll
		}
		return nil
	})
}

// FSSearch 按文件名模糊匹配（快速打开）。
func (e *Engine) FSSearch(projectID, query string, limit int) ([]FSSearchHit, error) {
	root, _, err := e.projectPath(projectID, "")
	if err != nil {
		return nil, err
	}
	if limit <= 0 {
		limit = 50
	}
	q := strings.ToLower(strings.TrimSpace(query))
	var hits []FSSearchHit
	scanned := 0
	walkProject(root, func(rel string, d fs.DirEntry) bool {
		scanned++
		if s := fuzzyScore(q, strings.ToLower(rel), strings.ToLower(d.Name())); s > 0 || q == "" {
			hits = append(hits, FSSearchHit{Path: rel, Name: d.Name(), Score: s})
		}
		return scanned < 200000
	})
	sort.SliceStable(hits, func(i, j int) bool {
		if hits[i].Score != hits[j].Score {
			return hits[i].Score > hits[j].Score
		}
		return len(hits[i].Path) < len(hits[j].Path)
	})
	if len(hits) > limit {
		hits = hits[:limit]
	}
	if hits == nil {
		hits = []FSSearchHit{}
	}
	return hits, nil
}

// fuzzyScore：查询字符按顺序出现在路径里即匹配；文件名命中、连续命中、词首命中加分。
func fuzzyScore(q, path, name string) int {
	if q == "" {
		return 1
	}
	if strings.Contains(name, q) {
		s := 1000 - len(name)
		if strings.HasPrefix(name, q) {
			s += 500
		}
		return s
	}
	score, pi, run := 0, 0, 0
	for _, c := range q {
		idx := strings.IndexRune(path[pi:], c)
		if idx < 0 {
			return 0
		}
		if idx == 0 {
			run++
			score += 5 * run
		} else {
			run = 0
		}
		pos := pi + idx
		if pos == 0 || strings.ContainsRune("/._-", rune(path[pos-1])) {
			score += 10
		}
		pi = pos + utf8.RuneLen(c)
	}
	return score + 1
}

// FSGrepParams 是 fs.grep 的输入。
type FSGrepParams struct {
	ProjectID     string `json:"projectId" required:"true"`
	Query         string `json:"query" required:"true"`
	Regex         bool   `json:"regex,omitempty"`
	CaseSensitive bool   `json:"caseSensitive,omitempty"`
	WholeWord     bool   `json:"wholeWord,omitempty"`
	Include       string `json:"include,omitempty" desc:"Comma-separated globs on file names, e.g. *.java,*.kt"`
	Limit         int    `json:"limit,omitempty"`
}

// FSGrepResult 是 fs.grep 的返回。
type FSGrepResult struct {
	Matches   []FSMatch `json:"matches"`
	Truncated bool      `json:"truncated"`
}

// FSGrep 在工程文件中查找文本。
func (e *Engine) FSGrep(ctx context.Context, p *FSGrepParams) (*FSGrepResult, error) {
	root, _, err := e.projectPath(p.ProjectID, "")
	if err != nil {
		return nil, err
	}
	pat := p.Query
	if !p.Regex {
		pat = regexp.QuoteMeta(pat)
	}
	if p.WholeWord {
		pat = `\b` + pat + `\b`
	}
	if !p.CaseSensitive {
		pat = "(?i)" + pat
	}
	re, err := regexp.Compile(pat)
	if err != nil {
		return nil, Errf("err.badRegex", err.Error())
	}
	var includes []string
	for _, g := range strings.Split(p.Include, ",") {
		if g = strings.TrimSpace(g); g != "" {
			includes = append(includes, g)
		}
	}
	limit := p.Limit
	if limit <= 0 {
		limit = 500
	}
	res := &FSGrepResult{Matches: []FSMatch{}}
	walkProject(root, func(rel string, d fs.DirEntry) bool {
		if ctx.Err() != nil {
			return false
		}
		if len(includes) > 0 && !sdk.MatchFileName(d.Name(), includes) {
			return true
		}
		info, err := d.Info()
		if err != nil || info.Size() > fsGrepMaxFile || info.Size() == 0 {
			return true
		}
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(rel)))
		if err != nil {
			return true
		}
		text, _, _, binary := decodeText(data)
		if binary {
			return true
		}
		sc := bufio.NewScanner(strings.NewReader(text))
		sc.Buffer(make([]byte, 64*1024), 4<<20)
		line := 0
		for sc.Scan() {
			line++
			s := sc.Text()
			if loc := re.FindStringIndex(s); loc != nil {
				t := s
				if len(t) > 400 {
					t = t[:400]
				}
				res.Matches = append(res.Matches, FSMatch{Path: rel, Line: line, Column: utf8.RuneCountInString(s[:loc[0]]) + 1, Text: t})
				if len(res.Matches) >= limit {
					res.Truncated = true
					return false
				}
			}
		}
		return true
	})
	return res, nil
}
