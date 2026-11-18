package engine

import (
	"archive/zip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk/archive"
)

func runtimeGOOS() string   { return runtime.GOOS }
func runtimeGOARCH() string { return runtime.GOARCH }

// PluginInfos 列出插件。
func (e *Engine) PluginInfos() []model.PluginInfo {
	var out []model.PluginInfo
	for _, p := range e.Plugins.All() {
		out = append(out, e.pluginInfo(p))
	}
	return out
}

func (e *Engine) pluginInfo(p *plugins.Plugin) model.PluginInfo {
	m := p.Manifest
	info := model.PluginInfo{
		ID: m.ID, Name: p.T(m.Name), Version: m.Version, Vendor: m.Vendor, Description: p.T(m.Description), Icon: p.Icon(m.Icon),
		Category: m.Category, Homepage: m.Homepage, Source: p.Source, Dir: p.Dir, Enabled: p.Enabled, Running: p.Running(), Error: p.Error,
		Dependencies: m.Dependencies, BundledVersion: p.BundledVersion,
	}
	c := m.Contributes
	for _, f := range c.Facets {
		info.Contributes.Facets = append(info.Contributes.Facets, p.T(f.Name))
	}
	for _, t := range c.Toolchains {
		info.Contributes.Toolchains = append(info.Contributes.Toolchains, p.T(t.Name))
	}
	for _, t := range c.RunConfigurationTypes {
		info.Contributes.RunConfigTypes = append(info.Contributes.RunConfigTypes, p.T(t.Name))
	}
	info.Contributes.Diagnostics = len(c.Diagnostics)
	for _, v := range c.Views {
		info.Contributes.Views = append(info.Contributes.Views, p.T(v.Title))
	}
	for _, a := range c.Actions {
		info.Contributes.Actions = append(info.Contributes.Actions, p.T(a.Title))
	}
	for _, t := range c.Tools {
		info.Contributes.Tools = append(info.Contributes.Tools, m.ID+"."+t.Name)
	}
	if c.VCS != nil {
		info.Contributes.VCS = c.VCS.Name
	}
	return info
}

// SetPluginEnabled 启用/禁用插件。
func (e *Engine) SetPluginEnabled(id string, enabled bool) error {
	if e.Plugins.Get(id) == nil {
		return ErrNotFound
	}
	e.Store.MutateSettings(func(s *model.Settings) {
		var next []string
		for _, x := range s.DisabledPlugins {
			if x != id {
				next = append(next, x)
			}
		}
		if !enabled {
			next = append(next, id)
		}
		s.DisabledPlugins = next
	})
	if !enabled {
		e.Plugins.StopPlugin(id)
	}
	e.Plugins.Reload(i18n.Locale())
	e.InvalidateToolchains()
	e.Bus.Publish(model.EvPlugins, nil)
	return nil
}

// SetPluginSettings 保存插件设置并重启插件进程。
func (e *Engine) SetPluginSettings(id string, values map[string]any) error {
	if e.Plugins.Get(id) == nil {
		return ErrNotFound
	}
	e.Store.MutateSettings(func(s *model.Settings) {
		if s.PluginSettings == nil {
			s.PluginSettings = map[string]map[string]any{}
		}
		s.PluginSettings[id] = values
	})
	e.Plugins.StopPlugin(id)
	e.Bus.Publish(model.EvPlugins, nil)
	return nil
}

// InstallPlugin 从 zip 或目录安装到用户插件目录。
func (e *Engine) InstallPlugin(path string) (model.PluginInfo, error) {
	path = sdk.Expand(path)
	tmp, err := os.MkdirTemp(e.Layout.Cache, "plugin-")
	if err != nil {
		return model.PluginInfo{}, err
	}
	defer os.RemoveAll(tmp)
	src := path
	if st, err := os.Stat(path); err != nil {
		return model.PluginInfo{}, err
	} else if !st.IsDir() {
		if err := archive.Extract(path, tmp, "zip", 0); err != nil {
			return model.PluginInfo{}, err
		}
		src = tmp
		// zip 里可能多包了一层目录
		if !sdk.FileExists(filepath.Join(src, "plugin.json")) {
			if subs := subdirsOf(src); len(subs) == 1 && sdk.FileExists(filepath.Join(subs[0], "plugin.json")) {
				src = subs[0]
			}
		}
	}
	man, err := sdk.LoadManifest(src)
	if err != nil {
		return model.PluginInfo{}, Errf("err.badPlugin", err.Error())
	}
	dest := filepath.Join(e.Layout.Plugins, man.ID)
	e.Plugins.StopPlugin(man.ID)
	_ = os.RemoveAll(dest)
	if err := copyDir(src, dest); err != nil {
		return model.PluginInfo{}, err
	}
	// 可执行权限
	_ = filepath.Walk(filepath.Join(dest, "bin"), func(p string, info os.FileInfo, err error) error {
		if err == nil && !info.IsDir() {
			_ = os.Chmod(p, 0o755)
		}
		return nil
	})
	e.Plugins.Reload(i18n.Locale())
	e.InvalidateToolchains()
	e.Bus.Publish(model.EvPlugins, nil)
	p := e.Plugins.Get(man.ID)
	if p == nil {
		return model.PluginInfo{}, ErrNotFound
	}
	return e.pluginInfo(p), nil
}

// UninstallPlugin 卸载用户安装的插件（内置插件只能禁用）。
func (e *Engine) UninstallPlugin(id string) error {
	p := e.Plugins.Get(id)
	if p == nil {
		return ErrNotFound
	}
	if p.Source != "user" {
		return Errf("err.cannotUninstallBundled")
	}
	e.Plugins.StopPlugin(id)
	if err := os.RemoveAll(p.Dir); err != nil {
		return err
	}
	e.Plugins.Reload(i18n.Locale())
	e.InvalidateToolchains()
	e.Bus.Publish(model.EvPlugins, nil)
	return nil
}

func copyDir(src, dst string) error {
	return filepath.Walk(src, func(p string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(src, p)
		target := filepath.Join(dst, rel)
		if info.IsDir() {
			return os.MkdirAll(target, 0o755)
		}
		if info.Mode()&os.ModeSymlink != 0 {
			l, err := os.Readlink(p)
			if err != nil {
				return err
			}
			return os.Symlink(l, target)
		}
		in, err := os.Open(p)
		if err != nil {
			return err
		}
		defer in.Close()
		out, err := os.OpenFile(target, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, info.Mode().Perm())
		if err != nil {
			return err
		}
		_, err = io.Copy(out, in)
		out.Close()
		return err
	})
}

// ---------------------------------------------------------------------------
// 商店

// StoreList 读取商店索引。
func (e *Engine) StoreList(ctx context.Context) ([]model.StoreEntry, error) {
	url := e.Store.Settings().StoreIndexURL
	if url == "" {
		url = e.cfg.BundledStore
	}
	if url == "" {
		return []model.StoreEntry{}, nil
	}
	data, err := readURL(ctx, url)
	if err != nil {
		return nil, err
	}
	var idx struct {
		Plugins []model.StoreEntry `json:"plugins"`
	}
	if err := json.Unmarshal(data, &idx); err != nil {
		return nil, err
	}
	// 索引里的相对地址相对索引本身解析（随 App 附带的离线商店用相对路径）
	for i := range idx.Plugins {
		for k, pf := range idx.Plugins[i].Platforms {
			pf.URL = resolveRef(url, pf.URL)
			idx.Plugins[i].Platforms[k] = pf
		}
		if idx.Plugins[i].Icon != "" {
			idx.Plugins[i].Icon = resolveRef(url, idx.Plugins[i].Icon)
		}
	}
	plat := runtime.GOOS + "-" + runtime.GOARCH
	for i := range idx.Plugins {
		en := &idx.Plugins[i]
		if p := e.Plugins.Get(en.ID); p != nil {
			en.Installed = p.Manifest.Version
		}
		_, hasPlat := storePlatform(en.Platforms, plat)
		en.Compatible = hasPlat && (en.Engines.Paperplane == "" || sdk.MatchVersion(en.Engines.Paperplane, e.cfg.Version))
	}
	return idx.Plugins, nil
}

// storePlatform 取本平台的包；纯声明插件（只有静态文件）用 "any" 一个包通吃所有平台。
func storePlatform[V any](m map[string]V, plat string) (V, bool) {
	if v, ok := m[plat]; ok {
		return v, true
	}
	v, ok := m["any"]
	return v, ok
}

// StoreInstall 从商店下载并安装（任务）。
func (e *Engine) StoreInstall(id string) (model.Task, error) {
	return e.NewTask("plugin", i18n.T("task.installPlugin", id), "", "", func(ctx context.Context, ts *taskState) (any, error) {
		list, err := e.StoreList(ctx)
		if err != nil {
			return nil, err
		}
		plat := runtime.GOOS + "-" + runtime.GOARCH
		for _, en := range list {
			if en.ID != id {
				continue
			}
			pf, ok := storePlatform(en.Platforms, plat)
			if !ok || !en.Compatible {
				return nil, Errf("err.pluginIncompatible", id)
			}
			file := filepath.Join(e.Layout.Cache, "plugin-"+id+"-"+en.Version+".zip")
			if isLocalRef(pf.URL) {
				if err := copyVerified(localPath(pf.URL), file, pf.SHA256); err != nil {
					return nil, err
				}
			} else if _, err := e.download(ctx, &sdk.DownloadParams{URLs: []string{pf.URL}, SHA256: pf.SHA256, Dest: file, TaskID: ts.t.ID}); err != nil {
				return nil, err
			}
			defer os.Remove(file)
			info, err := e.InstallPlugin(file)
			return info, err
		}
		return nil, ErrNotFound
	}), nil
}

// PackPlugin 把插件目录打成商店 zip（当前平台）。
func PackPlugin(dir, out string) (string, error) {
	man, err := sdk.LoadManifest(dir)
	if err != nil {
		return "", err
	}
	if out == "" {
		out = filepath.Join(dir, "dist", fmt.Sprintf("%s-%s-%s-%s.zip", man.ID, man.Version, runtime.GOOS, runtime.GOARCH))
	}
	_ = os.MkdirAll(filepath.Dir(out), 0o755)
	f, err := os.Create(out)
	if err != nil {
		return "", err
	}
	defer f.Close()
	zw := zip.NewWriter(f)
	add := func(src, name string, mode os.FileMode) error {
		in, err := os.Open(src)
		if err != nil {
			return err
		}
		defer in.Close()
		h := &zip.FileHeader{Name: filepath.ToSlash(name), Method: zip.Deflate}
		h.SetMode(mode)
		w, err := zw.CreateHeader(h)
		if err != nil {
			return err
		}
		_, err = io.Copy(w, in)
		return err
	}
	if err := add(filepath.Join(dir, "plugin.json"), "plugin.json", 0o644); err != nil {
		return "", err
	}
	for _, sub := range []string{"icons", "l10n", "resources", "web", "editor", "keymaps", "themes"} {
		_ = filepath.Walk(filepath.Join(dir, sub), func(p string, info os.FileInfo, err error) error {
			if err != nil || info.IsDir() {
				return nil
			}
			rel, _ := filepath.Rel(dir, p)
			return add(p, rel, 0o644)
		})
	}
	if !man.Declarative() {
		exe := filepath.Join(dir, filepath.FromSlash(man.Executable))
		if runtime.GOOS == "windows" {
			exe += ".exe"
		}
		name := filepath.Join("bin", runtime.GOOS+"-"+runtime.GOARCH, filepath.Base(exe))
		if err := add(exe, name, 0o755); err != nil {
			return "", fmt.Errorf("executable: %w", err)
		}
	}
	if err := zw.Close(); err != nil {
		return "", err
	}
	return out, nil
}

var _ = strings.TrimSpace

// ReloadPlugins 重新扫描插件目录、关闭全部插件进程、清空规则缓存（开发时改了 manifest 或重新编译插件后用）。
func (e *Engine) ReloadPlugins() {
	for _, p := range e.Plugins.All() {
		e.Plugins.StopPlugin(p.ID())
	}
	e.Plugins.Reload(i18n.Locale())
	e.rulesMu.Lock()
	e.rules = map[string]*compiledRules{}
	e.rulesMu.Unlock()
	e.InvalidateToolchains()
	e.Bus.Publish(model.EvPlugins, nil)
}

func isLocalRef(u string) bool { return strings.HasPrefix(u, "file://") || filepath.IsAbs(u) }

func localPath(u string) string { return strings.TrimPrefix(u, "file://") }

// resolveRef 把相对引用解析为相对 base（文件路径或 URL）的地址。
func resolveRef(base, ref string) string {
	if ref == "" || strings.Contains(ref, "://") || filepath.IsAbs(ref) {
		return ref
	}
	if isLocalRef(base) {
		return filepath.Join(filepath.Dir(localPath(base)), filepath.FromSlash(ref))
	}
	if i := strings.LastIndex(base, "/"); i >= 0 {
		return base[:i+1] + ref
	}
	return ref
}

// readURL 读取 http(s) 地址或本地文件。
func readURL(ctx context.Context, u string) ([]byte, error) {
	if isLocalRef(u) {
		return os.ReadFile(localPath(u))
	}
	cctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	req, _ := http.NewRequestWithContext(cctx, http.MethodGet, u, nil)
	resp, err := (&http.Client{Transport: &http.Transport{Proxy: http.ProxyFromEnvironment}}).Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return nil, fmt.Errorf("store index HTTP %d", resp.StatusCode)
	}
	return io.ReadAll(io.LimitReader(resp.Body, 16<<20))
}

// copyVerified 复制本地文件并校验 sha256。
func copyVerified(src, dst, sha string) error {
	data, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	if sha != "" {
		sum := sha256.Sum256(data)
		if !strings.EqualFold(hex.EncodeToString(sum[:]), sha) {
			return fmt.Errorf("sha256 mismatch")
		}
	}
	return os.WriteFile(dst, data, 0o644)
}
