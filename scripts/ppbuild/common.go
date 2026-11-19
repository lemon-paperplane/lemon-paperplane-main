package main

import (
	"bufio"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
)

const pluginPrefix = "lemon-paperplane-plugin-"

// 插件目录里随插件分发的子目录
var pluginSubdirs = []string{"icons", "l10n", "resources", "web", "editor", "keymaps", "themes"}

// paths：主仓库与插件仓库所在目录。
type paths struct {
	main string // lemon-paperplane-main
	root string // 放 lemon-paperplane-plugin-* 的目录
}

func (p *paths) register(fs *flag.FlagSet) {
	fs.StringVar(&p.main, "main", "", "lemon-paperplane-main 目录")
	fs.StringVar(&p.root, "root", "", "插件仓库所在目录（默认 main 的上一级，或环境变量 PAPERPLANE_PLUGINS_ROOT）")
}

func (p *paths) resolve() error {
	if p.main == "" {
		wd, _ := os.Getwd()
		for d := wd; ; d = filepath.Dir(d) {
			if isDir(filepath.Join(d, "lemon-paperplane-main-core")) {
				p.main = d
				break
			}
			if filepath.Dir(d) == d {
				return errors.New("找不到 lemon-paperplane-main 目录，用 -main 指定")
			}
		}
	}
	m, err := filepath.Abs(p.main)
	if err != nil {
		return err
	}
	p.main = m
	if p.root == "" {
		p.root = os.Getenv("PAPERPLANE_PLUGINS_ROOT")
	}
	if p.root == "" {
		p.root = filepath.Dir(p.main)
	}
	p.root, err = filepath.Abs(p.root)
	return err
}

func (p *paths) core() string { return filepath.Join(p.main, "lemon-paperplane-main-core") }
func (p *paths) sdk() string  { return filepath.Join(p.main, "lemon-paperplane-main-plugin-sdk") }

// pluginDirs：root 下所有带 plugin.json 的插件仓库，按名字排序。
func (p *paths) pluginDirs() []string {
	ms, _ := filepath.Glob(filepath.Join(p.root, pluginPrefix+"*"))
	var out []string
	for _, d := range ms {
		if isFile(filepath.Join(d, "plugin.json")) {
			out = append(out, d)
		}
	}
	sort.Strings(out)
	return out
}

// storeOnly：scripts/store-only.txt 里列出的只进商店、不内置的插件 id。
func (p *paths) storeOnly() []string {
	f, err := os.Open(filepath.Join(p.main, "scripts", "store-only.txt"))
	if err != nil {
		return nil
	}
	defer f.Close()
	var ids []string
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		l := strings.TrimSpace(sc.Text())
		if l == "" || strings.HasPrefix(l, "#") {
			continue
		}
		ids = append(ids, strings.Fields(l)...)
	}
	return ids
}

type manifest struct {
	ID          string          `json:"id"`
	Name        string          `json:"name"`
	Version     string          `json:"version"`
	Vendor      string          `json:"vendor"`
	Description string          `json:"description"`
	Executable  string          `json:"executable"`
	OS          []string        `json:"os"`
	Engines     json.RawMessage `json:"engines"`
}

func readManifest(dir string) (*manifest, error) {
	b, err := os.ReadFile(filepath.Join(dir, "plugin.json"))
	if err != nil {
		return nil, err
	}
	var m manifest
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, fmt.Errorf("%s/plugin.json：%w", dir, err)
	}
	if m.ID == "" {
		return nil, fmt.Errorf("%s/plugin.json 没有 id", dir)
	}
	return &m, nil
}

// supports：插件没声明 os，或声明里含这个系统。
func (m *manifest) supports(goos string) bool {
	if len(m.OS) == 0 {
		return true
	}
	for _, o := range m.OS {
		if o == goos {
			return true
		}
	}
	return false
}

// exeName：插件程序的文件名（不含目录），纯声明插件为空。
func (m *manifest) exeName() string {
	if m.Executable == "" {
		return ""
	}
	return filepath.Base(filepath.FromSlash(m.Executable))
}

// copyPluginFiles：plugin.json 与随插件分发的子目录。
func copyPluginFiles(src, dst string) error {
	if err := os.MkdirAll(dst, 0o755); err != nil {
		return err
	}
	if err := copyFile(filepath.Join(src, "plugin.json"), filepath.Join(dst, "plugin.json")); err != nil {
		return err
	}
	for _, sub := range pluginSubdirs {
		if isDir(filepath.Join(src, sub)) {
			if err := copyTree(filepath.Join(src, sub), filepath.Join(dst, sub)); err != nil {
				return err
			}
		}
	}
	return nil
}

func copyTree(src, dst string) error {
	return filepath.Walk(src, func(p string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(src, p)
		t := filepath.Join(dst, rel)
		switch {
		case info.Mode()&os.ModeSymlink != 0:
			l, err := os.Readlink(p)
			if err != nil {
				return err
			}
			_ = os.Remove(t)
			return os.Symlink(l, t)
		case info.IsDir():
			return os.MkdirAll(t, 0o755)
		default:
			return copyFile(p, t)
		}
	})
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	info, err := in.Stat()
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	out, err := os.OpenFile(dst, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, info.Mode().Perm()|0o200)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	return out.Close()
}

func isDir(p string) bool {
	st, err := os.Stat(p)
	return err == nil && st.IsDir()
}

func isFile(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

func exeSuffix(goos string) string {
	if goos == "windows" {
		return ".exe"
	}
	return ""
}

// goBuild：纯 Go 交叉编译（不需要 CGO）。
func goBuild(dir, goos, goarch, out, ldflags, pkg string) error {
	if ldflags == "" {
		ldflags = "-s -w"
	}
	cmd := exec.Command("go", "build", "-trimpath", "-ldflags", ldflags, "-o", out, pkg)
	cmd.Dir = dir
	cmd.Env = append(os.Environ(), "CGO_ENABLED=0", "GOOS="+goos, "GOARCH="+goarch)
	cmd.Stdout = os.Stderr
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("编译 %s（%s/%s）失败：%w", filepath.Join(dir, pkg), goos, goarch, err)
	}
	return nil
}

func splitList(s string) []string {
	var out []string
	for _, f := range strings.FieldsFunc(s, func(r rune) bool { return r == ',' || r == ' ' }) {
		if f != "" {
			out = append(out, f)
		}
	}
	return out
}

func absAll(ps []string) ([]string, error) {
	out := make([]string, len(ps))
	for i, p := range ps {
		a, err := filepath.Abs(p)
		if err != nil {
			return nil, err
		}
		out[i] = a
	}
	return out, nil
}

// relSlash：给 go.mod 用的相对路径（正斜杠，以 ./ 或 ../ 开头）。
func relSlash(from, to string) (string, error) {
	r, err := filepath.Rel(from, to)
	if err != nil {
		return "", err
	}
	r = filepath.ToSlash(r)
	if !strings.HasPrefix(r, "../") && !strings.HasPrefix(r, "./") {
		r = "./" + r
	}
	return r, nil
}
