package main

import (
	"archive/zip"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const defaultStorePlatforms = "darwin/arm64 darwin/amd64 windows/amd64 windows/arm64 linux/amd64 linux/arm64"

type storeOptions struct {
	paths
	out       string
	base      string // 下载根地址；"." 生成相对路径（随 App 附带的离线商店）
	platforms []string
	dirs      []string
	quiet     bool
}

// cmdStore：为插件商店打各平台安装包与索引。
//
//	<out>/<id>/<version>/<id>-<version>-<os>-<arch>.zip（纯声明插件：<id>-<version>.zip，平台记为 any）
//	<out>/<id>/icon.svg
//	<out>/index.json
//
// 不给插件目录时打包 scripts/store-only.txt 里列出的插件。
func cmdStore(args []string) error {
	var o storeOptions
	var plats string
	fs := flag.NewFlagSet("store", flag.ExitOnError)
	o.register(fs)
	fs.StringVar(&o.out, "out", "", "输出目录")
	fs.StringVar(&o.base, "base", "", "下载根地址（\".\" 为相对路径）")
	fs.StringVar(&plats, "platforms", "", "平台，如 \"darwin/arm64 windows/amd64\"（默认环境变量 STORE_PLATFORMS 或全部平台）")
	_ = fs.Parse(args)
	if o.out == "" || o.base == "" {
		return errors.New("store 需要 -out 与 -base")
	}
	if plats == "" {
		plats = os.Getenv("STORE_PLATFORMS")
	}
	if plats == "" {
		plats = defaultStorePlatforms
	}
	o.platforms = splitList(plats)
	if err := o.resolve(); err != nil {
		return err
	}
	dirs, err := absAll(fs.Args())
	if err != nil {
		return err
	}
	o.dirs = dirs
	return buildStore(o)
}

type storePlatform struct {
	URL    string `json:"url"`
	SHA256 string `json:"sha256"`
	Size   int64  `json:"size"`
}

type storeEntry struct {
	ID          string                   `json:"id"`
	Name        string                   `json:"name"`
	Version     string                   `json:"version"`
	Vendor      string                   `json:"vendor"`
	Description string                   `json:"description"`
	Engines     json.RawMessage          `json:"engines"`
	Icon        string                   `json:"icon"`
	Platforms   map[string]storePlatform `json:"platforms"`
}

func buildStore(o storeOptions) error {
	out, err := filepath.Abs(o.out)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(out, 0o755); err != nil {
		return err
	}
	dirs := o.dirs
	if len(dirs) == 0 {
		for _, id := range o.storeOnly() {
			d := filepath.Join(o.root, pluginPrefix+id)
			if isDir(d) {
				dirs = append(dirs, d)
			}
		}
	}
	url := func(rel string) string {
		if o.base == "." {
			return rel
		}
		return strings.TrimRight(o.base, "/") + "/" + rel
	}
	var index []*storeEntry
	for _, dir := range dirs {
		if !isFile(filepath.Join(dir, "plugin.json")) {
			continue
		}
		m, err := readManifest(dir)
		if err != nil {
			return err
		}
		if m.Version == "" {
			return fmt.Errorf("%s/plugin.json 没有 version", dir)
		}
		dest := filepath.Join(out, m.ID, m.Version)
		if err := os.MkdirAll(dest, 0o755); err != nil {
			return err
		}
		engines := m.Engines
		if len(engines) == 0 {
			engines = json.RawMessage("{}")
		}
		e := &storeEntry{
			ID: m.ID, Name: localize(dir, m.Name, m.ID), Version: m.Version, Vendor: m.Vendor,
			Description: localize(dir, m.Description, ""), Engines: engines,
			Icon: url(m.ID + "/icon.svg"), Platforms: map[string]storePlatform{},
		}
		pack := func(plat, zipName string, stage func(string) error) error {
			tmp, err := os.MkdirTemp("", "ppbuild-store-")
			if err != nil {
				return err
			}
			defer os.RemoveAll(tmp)
			if err := copyPluginFiles(dir, tmp); err != nil {
				return err
			}
			if stage != nil {
				if err := stage(tmp); err != nil {
					return err
				}
			}
			zp := filepath.Join(dest, zipName)
			if err := zipDir(tmp, zp); err != nil {
				return err
			}
			sum, size, err := fileSHA256(zp)
			if err != nil {
				return err
			}
			e.Platforms[plat] = storePlatform{URL: url(m.ID + "/" + m.Version + "/" + zipName), SHA256: sum, Size: size}
			return nil
		}
		exe := m.exeName()
		if exe == "" {
			// 纯声明插件：没有程序，一个包通吃所有平台
			if err := pack("any", fmt.Sprintf("%s-%s.zip", m.ID, m.Version), nil); err != nil {
				return err
			}
		} else {
			cmds, _ := filepath.Glob(filepath.Join(dir, "cmd", "*"))
			sort.Strings(cmds)
			var cmdDir string
			for _, c := range cmds {
				if isDir(c) {
					cmdDir = c
					break
				}
			}
			if cmdDir == "" {
				return fmt.Errorf("%s 下没有 cmd/<程序>", dir)
			}
			pkg := "./" + filepath.ToSlash(mustRel(dir, cmdDir))
			for _, p := range o.platforms {
				goos, goarch, ok := strings.Cut(p, "/")
				if !ok {
					return fmt.Errorf("平台写法应为 os/arch：%s", p)
				}
				if !m.supports(goos) {
					continue
				}
				err := pack(goos+"-"+goarch, fmt.Sprintf("%s-%s-%s-%s.zip", m.ID, m.Version, goos, goarch), func(stage string) error {
					bin := filepath.Join(stage, "bin", goos+"-"+goarch, exe+exeSuffix(goos))
					return goBuild(dir, goos, goarch, bin, "", pkg)
				})
				if err != nil {
					return err
				}
			}
		}
		if icon := filepath.Join(dir, "icons", m.ID+".svg"); isFile(icon) {
			if err := copyFile(icon, filepath.Join(out, m.ID, "icon.svg")); err != nil {
				return err
			}
		}
		if len(e.Platforms) > 0 {
			index = append(index, e)
		}
		if !o.quiet {
			fmt.Printf("packed %s %s\n", m.ID, m.Version)
		}
	}
	sort.Slice(index, func(i, j int) bool { return index[i].ID < index[j].ID })
	if index == nil {
		index = []*storeEntry{}
	}
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	enc.SetIndent("", "  ")
	if err := enc.Encode(map[string]any{"plugins": index}); err != nil {
		return err
	}
	ip := filepath.Join(out, "index.json")
	if err := os.WriteFile(ip, buf.Bytes(), 0o644); err != nil {
		return err
	}
	if !o.quiet {
		fmt.Printf("index: %s %d plugins\n", ip, len(index))
	}
	return nil
}

// localize：%key% 形式的名称取 l10n/en.json 里的英文（商店索引用英文作默认）。
func localize(dir, s, fallback string) string {
	if s == "" {
		return fallback
	}
	if len(s) > 2 && strings.HasPrefix(s, "%") && strings.HasSuffix(s, "%") {
		b, err := os.ReadFile(filepath.Join(dir, "l10n", "en.json"))
		if err == nil {
			var m map[string]any
			if json.Unmarshal(b, &m) == nil {
				if v, ok := m[s[1:len(s)-1]].(string); ok {
					return v
				}
			}
		}
	}
	return s
}

// zipDir：把目录内容打进 zip（条目用正斜杠，保留可执行权限）。
func zipDir(dir, zp string) error {
	_ = os.Remove(zp)
	f, err := os.Create(zp)
	if err != nil {
		return err
	}
	zw := zip.NewWriter(f)
	err = filepath.Walk(dir, func(p string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(dir, p)
		if rel == "." {
			return nil
		}
		h, err := zip.FileInfoHeader(info)
		if err != nil {
			return err
		}
		h.Name = filepath.ToSlash(rel)
		if info.IsDir() {
			h.Name += "/"
			_, err = zw.CreateHeader(h)
			return err
		}
		h.Method = zip.Deflate
		if strings.HasPrefix(h.Name, "bin/") {
			h.SetMode(0o755)
		}
		w, err := zw.CreateHeader(h)
		if err != nil {
			return err
		}
		in, err := os.Open(p)
		if err != nil {
			return err
		}
		defer in.Close()
		_, err = io.Copy(w, in)
		return err
	})
	if cerr := zw.Close(); err == nil {
		err = cerr
	}
	if cerr := f.Close(); err == nil {
		err = cerr
	}
	return err
}

func fileSHA256(p string) (string, int64, error) {
	f, err := os.Open(p)
	if err != nil {
		return "", 0, err
	}
	defer f.Close()
	h := sha256.New()
	n, err := io.Copy(h, f)
	if err != nil {
		return "", 0, err
	}
	return hex.EncodeToString(h.Sum(nil)), n, nil
}

func mustRel(base, p string) string {
	r, err := filepath.Rel(base, p)
	if err != nil {
		return p
	}
	return r
}
