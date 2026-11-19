package engine

import (
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
)

// remoteAPIVersion 与 remote.APIVersion 一致（engine 不依赖 remote 包）。
const remoteAPIVersion = 1

// SetRemoteLinks 设置连接远程服务器时运行地址的打开方式；两种都要（缺省）时删掉记录。
func (e *Engine) SetRemoteLinks(projectID, configID string, l model.RemoteLinks) error {
	if !l.Forward && !l.Direct {
		return Errf("err.remoteLinksNone")
	}
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		if _, c := findConfig(p, configID); c == nil {
			return ErrNotFound
		}
		if l.Forward && l.Direct {
			delete(p.RemoteLinks, configID)
			return nil
		}
		if p.RemoteLinks == nil {
			p.RemoteLinks = map[string]model.RemoteLinks{}
		}
		p.RemoteLinks[configID] = l
		return nil
	})
	if err == nil {
		e.publishProject(projectID)
	}
	return err
}

// FSBrowseEntry 是 fs.browse 的一项。
type FSBrowseEntry struct {
	Name    string    `json:"name"`
	Path    string    `json:"path"`
	Dir     bool      `json:"dir"`
	Hidden  bool      `json:"hidden"`
	Size    int64     `json:"size"`
	ModTime time.Time `json:"modTime"`
}

// FSBrowse 是 fs.browse 的结果。
type FSBrowse struct {
	Path    string          `json:"path"`
	Parent  string          `json:"parent"` // 已在根目录时为空
	Home    string          `json:"home"`
	Sep     string          `json:"sep"`
	Roots   []string        `json:"roots"` // Windows 盘符；其他系统为 ["/"]
	Entries []FSBrowseEntry `json:"entries"`
}

// FSBrowse 列出任意文件夹（远程选择文件夹用）。path 为空时是主目录。
func (e *Engine) FSBrowse(path string) (*FSBrowse, error) {
	home, _ := os.UserHomeDir()
	p := strings.TrimSpace(path)
	if p == "" {
		p = home
	}
	if strings.HasPrefix(p, "~") {
		p = filepath.Join(home, strings.TrimPrefix(p, "~"))
	}
	if runtime.GOOS == "windows" && len(p) == 2 && p[1] == ':' {
		p += `\`
	}
	p = filepath.Clean(p)
	if !filepath.IsAbs(p) {
		return nil, Errf("fs.notAbs", path)
	}
	st, err := os.Stat(p)
	if err != nil {
		return nil, Errf("fs.cannotOpen", p)
	}
	if !st.IsDir() {
		p = filepath.Dir(p)
	}
	ents, err := os.ReadDir(p)
	if err != nil {
		return nil, Errf("fs.noPermission", p)
	}
	out := &FSBrowse{Path: p, Home: home, Sep: string(filepath.Separator), Roots: fsRoots(), Entries: []FSBrowseEntry{}}
	if parent := filepath.Dir(p); parent != p {
		out.Parent = parent
	}
	for _, d := range ents {
		info, err := d.Info()
		if err != nil {
			continue
		}
		isDir := info.IsDir()
		if info.Mode()&os.ModeSymlink != 0 {
			if t, err := os.Stat(filepath.Join(p, d.Name())); err == nil {
				isDir = t.IsDir()
			}
		}
		out.Entries = append(out.Entries, FSBrowseEntry{Name: d.Name(), Path: filepath.Join(p, d.Name()), Dir: isDir,
			Hidden: strings.HasPrefix(d.Name(), "."), Size: info.Size(), ModTime: info.ModTime()})
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

func fsRoots() []string {
	if runtime.GOOS != "windows" {
		return []string{"/"}
	}
	var out []string
	for c := 'A'; c <= 'Z'; c++ {
		r := string(c) + `:\`
		if _, err := os.Stat(r); err == nil {
			out = append(out, r)
		}
	}
	return out
}

// FSMkdir 在绝对路径处新建文件夹（上一级必须存在）。
func (e *Engine) FSMkdir(path string) (string, error) {
	p := filepath.Clean(strings.TrimSpace(path))
	if !filepath.IsAbs(p) {
		return "", Errf("fs.notAbs", path)
	}
	if st, err := os.Stat(filepath.Dir(p)); err != nil || !st.IsDir() {
		return "", Errf("fs.parentMissing", filepath.Dir(p))
	}
	if err := os.Mkdir(p, 0o755); err != nil {
		if os.IsExist(err) {
			return "", Errf("fs.exists", filepath.Base(p))
		}
		return "", &UserError{Msg: err.Error()}
	}
	return p, nil
}
