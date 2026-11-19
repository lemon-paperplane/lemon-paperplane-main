package engine

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// manualToolchain 是用户手动登记的工具链路径。
type manualToolchain struct {
	Kind string `json:"kind"`
	Path string `json:"path"`
}

func (e *Engine) manualFile() string { return filepath.Join(e.Layout.Toolchains, "manual.json") }

func (e *Engine) manualList() []manualToolchain {
	var out []manualToolchain
	_ = sdk.ReadJSON(e.manualFile(), &out)
	return out
}

func (e *Engine) saveManual(list []manualToolchain) {
	b, _ := json.MarshalIndent(list, "", "  ")
	_ = os.WriteFile(e.manualFile(), b, 0o644)
}

// ToolchainKinds 列出全部工具链类型。
func (e *Engine) ToolchainKinds() []model.ToolchainKind {
	var out []model.ToolchainKind
	seen := map[string]bool{}
	for _, p := range e.Plugins.Active() {
		for _, k := range p.Manifest.Contributes.Toolchains {
			if seen[k.Kind] {
				continue
			}
			seen[k.Kind] = true
			out = append(out, model.ToolchainKind{Kind: k.Kind, Name: p.T(k.Name), Icon: p.Icon(k.Icon), Installable: k.Installable, Plugin: p.ID()})
		}
	}
	return out
}

func (e *Engine) toolchainPlugin(kind string) *plugins.Plugin {
	for _, p := range e.Plugins.Active() {
		for _, k := range p.Manifest.Contributes.Toolchains {
			if k.Kind == kind {
				return p
			}
		}
	}
	return nil
}

// ToolchainsOf 返回某类工具链（缓存 5 分钟；refresh 强制重新发现）。
func (e *Engine) ToolchainsOf(ctx context.Context, kind string, refresh bool) []sdk.Toolchain {
	e.tcMu.Lock()
	if c, ok := e.tcCache[kind]; ok && !refresh && time.Since(e.tcAt[kind]) < 5*time.Minute {
		e.tcMu.Unlock()
		return c
	}
	e.tcMu.Unlock()
	p := e.toolchainPlugin(kind)
	if p == nil {
		return nil
	}
	var all []sdk.Toolchain
	var r sdk.DiscoverToolchainsResult
	if err := e.Plugins.Call(ctx, p, sdk.MethodDiscoverToolchains, &sdk.DiscoverToolchainsParams{Kind: kind}, &r); err != nil {
		e.logf("discover %s: %v", kind, err)
	}
	all = append(all, r.Toolchains...)
	// Paperplane 管理的与手动登记的
	var extra []string
	managedRoot := filepath.Join(e.Layout.Toolchains, kind)
	for _, d := range subdirsOf(managedRoot) {
		extra = append(extra, d)
	}
	for _, mt := range e.manualList() {
		if mt.Kind == kind {
			extra = append(extra, mt.Path)
		}
	}
	if len(extra) > 0 {
		var r2 sdk.DiscoverToolchainsResult
		if err := e.Plugins.Call(ctx, p, sdk.MethodDiscoverToolchains, &sdk.DiscoverToolchainsParams{Kind: kind, Paths: extra}, &r2); err == nil {
			all = append(all, r2.Toolchains...)
		}
	}
	seen := map[string]bool{}
	var out []sdk.Toolchain
	for _, t := range all {
		t.Kind = kind
		key := filepath.Clean(t.Path)
		if real, err := filepath.EvalSymlinks(key); err == nil {
			key = real
		}
		if seen[key] {
			continue
		}
		seen[key] = true
		t.ID = kind + ":" + filepath.Clean(t.Path)
		if strings.HasPrefix(filepath.Clean(t.Path), managedRoot+string(filepath.Separator)) {
			t.Managed = true
			t.Source = "managed"
		}
		out = append(out, t)
	}
	sort.SliceStable(out, func(i, j int) bool { return sdk.CompareVersions(out[i].Version, out[j].Version) > 0 })
	e.tcMu.Lock()
	e.tcCache[kind] = out
	e.tcAt[kind] = time.Now()
	e.tcMu.Unlock()
	return out
}

func subdirsOf(d string) []string {
	entries, err := os.ReadDir(d)
	if err != nil {
		return nil
	}
	var out []string
	for _, en := range entries {
		if en.IsDir() && !strings.HasPrefix(en.Name(), ".") {
			out = append(out, filepath.Join(d, en.Name()))
		}
	}
	return out
}

// AllToolchains 返回全部类型的工具链。
func (e *Engine) AllToolchains(ctx context.Context, kind string) []sdk.Toolchain {
	if kind != "" {
		return e.ToolchainsOf(ctx, kind, false)
	}
	var out []sdk.Toolchain
	for _, k := range e.ToolchainKinds() {
		out = append(out, e.ToolchainsOf(ctx, k.Kind, false)...)
	}
	return out
}

func (e *Engine) toolchainByID(id string) (sdk.Toolchain, bool) {
	kind, _, _ := strings.Cut(id, ":")
	for _, t := range e.ToolchainsOf(context.Background(), kind, false) {
		if t.ID == id {
			return t, true
		}
	}
	return sdk.Toolchain{}, false
}

// InvalidateToolchains 清缓存。
func (e *Engine) InvalidateToolchains() {
	e.tcMu.Lock()
	e.tcCache = map[string][]sdk.Toolchain{}
	e.tcMu.Unlock()
	e.Bus.Publish(model.EvToolchains, nil)
}

// AvailableToolchains 列出可下载版本。
func (e *Engine) AvailableToolchains(ctx context.Context, kind string) ([]sdk.AvailableVersion, error) {
	p := e.toolchainPlugin(kind)
	if p == nil {
		return nil, ErrNotFound
	}
	var r sdk.AvailableToolchainsResult
	if err := e.Plugins.Call(ctx, p, sdk.MethodAvailableToolchain, &sdk.AvailableToolchainsParams{Kind: kind}, &r); err != nil {
		return nil, err
	}
	return r.Versions, nil
}

var unsafeChars = regexp.MustCompile(`[^A-Za-z0-9._+-]`)

// InstallToolchain 创建安装任务。
func (e *Engine) InstallToolchain(kind, version string) (model.Task, error) {
	p := e.toolchainPlugin(kind)
	if p == nil {
		return model.Task{}, ErrNotFound
	}
	name := kind
	for _, k := range e.ToolchainKinds() {
		if k.Kind == kind {
			name = k.Name
		}
	}
	t := e.NewTask("toolchain", i18n.T("task.installToolchain", name, version), "", "", func(ctx context.Context, ts *taskState) (any, error) {
		return e.installToolchain(ctx, p, kind, version, ts.t.ID)
	})
	return t, nil
}

// installToolchain 同步执行安装，返回安装好的工具链。
func (e *Engine) installToolchain(ctx context.Context, p *plugins.Plugin, kind, version, taskID string) (*sdk.Toolchain, error) {
	// 工程没写版本时是 "latest"：按插件给的可下载列表取推荐版本（没有推荐就取最新），不让每个插件各自认 latest
	if version == "" || version == "latest" {
		if vs, err := e.AvailableToolchains(ctx, kind); err == nil && len(vs) > 0 {
			version = pickInstallVersion(vs)
		}
	}
	dest := filepath.Join(e.Layout.Toolchains, kind, unsafeChars.ReplaceAllString(version, "_")+"-"+time.Now().Format("150405"))
	var r sdk.InstallToolchainResult
	if err := e.Plugins.Call(ctx, p, sdk.MethodInstallToolchain, &sdk.InstallToolchainParams{Kind: kind, Version: version, Dest: dest, TaskID: taskID}, &r); err != nil {
		return nil, err
	}
	var tc *sdk.Toolchain
	if r.Plan != nil {
		_, err := e.download(ctx, &sdk.DownloadParams{URLs: r.Plan.URLs, SHA256: r.Plan.SHA256, Dest: dest, Extract: r.Plan.Archive, StripComponents: r.Plan.StripComponents, TaskID: taskID, Title: i18n.T("task.download", kind+" "+r.Plan.Version)})
		if err != nil {
			_ = os.RemoveAll(dest)
			return nil, err
		}
		// 目录名换成真实版本
		final := filepath.Join(e.Layout.Toolchains, kind, unsafeChars.ReplaceAllString(r.Plan.Version, "_"))
		if _, err := os.Stat(final); err == nil {
			final = dest
		} else if err := os.Rename(dest, final); err != nil {
			final = dest
		}
		var dr sdk.DiscoverToolchainsResult
		if err := e.Plugins.Call(ctx, p, sdk.MethodDiscoverToolchains, &sdk.DiscoverToolchainsParams{Kind: kind, Paths: []string{final}}, &dr); err != nil {
			return nil, err
		}
		if len(dr.Toolchains) == 0 {
			return nil, Errf("err.toolchainInvalid", final)
		}
		tc = &dr.Toolchains[0]
	} else if r.Toolchain != nil {
		tc = r.Toolchain
	} else {
		return nil, errors.New("plugin returned no plan")
	}
	e.InvalidateToolchains()
	tc.Kind = kind
	tc.ID = kind + ":" + filepath.Clean(tc.Path)
	if taskID != "" {
		e.consoleSystem(taskID, i18n.T("task.toolchainInstalled", tc.Label+" "+tc.Version, shortHome(tc.Path)))
	}
	// 让等待该工具链的工程重新选择
	go e.reselectAll()
	return tc, nil
}

// AddToolchain 手动登记一个路径。
func (e *Engine) AddToolchain(ctx context.Context, kind, path string) (sdk.Toolchain, error) {
	p := e.toolchainPlugin(kind)
	if p == nil {
		return sdk.Toolchain{}, ErrNotFound
	}
	var dr sdk.DiscoverToolchainsResult
	if err := e.Plugins.Call(ctx, p, sdk.MethodDiscoverToolchains, &sdk.DiscoverToolchainsParams{Kind: kind, Paths: []string{path}}, &dr); err != nil {
		return sdk.Toolchain{}, err
	}
	if len(dr.Toolchains) == 0 {
		return sdk.Toolchain{}, Errf("err.toolchainInvalid", path)
	}
	list := e.manualList()
	list = append(list, manualToolchain{Kind: kind, Path: path})
	e.saveManual(list)
	e.InvalidateToolchains()
	tc := dr.Toolchains[0]
	tc.Kind = kind
	tc.ID = kind + ":" + filepath.Clean(tc.Path)
	go e.reselectAll()
	return tc, nil
}

// RemoveToolchain 删除 Paperplane 管理的工具链，或取消手动登记。
func (e *Engine) RemoveToolchain(id string) error {
	t, ok := e.toolchainByID(id)
	if !ok {
		return ErrNotFound
	}
	if t.Managed {
		root := filepath.Join(e.Layout.Toolchains, t.Kind)
		// 找到 managed 根下的第一层目录整体删除
		rel, err := filepath.Rel(root, t.Path)
		if err != nil || strings.HasPrefix(rel, "..") {
			return Errf("err.toolchainNotManaged")
		}
		first := strings.Split(filepath.ToSlash(rel), "/")[0]
		if err := os.RemoveAll(filepath.Join(root, first)); err != nil {
			return err
		}
	} else {
		list := e.manualList()
		var next []manualToolchain
		found := false
		for _, m := range list {
			if m.Kind == t.Kind && (filepath.Clean(m.Path) == filepath.Clean(t.Path) || strings.HasPrefix(t.Path, m.Path)) {
				found = true
				continue
			}
			next = append(next, m)
		}
		if !found {
			return Errf("err.toolchainNotManaged")
		}
		e.saveManual(next)
	}
	e.InvalidateToolchains()
	go e.reselectAll()
	return nil
}

// ---------------------------------------------------------------------------
// 选择

// selectToolchains 按需求与工程设置为模块选工具链。
func (e *Engine) selectToolchains(ctx context.Context, p *model.Project, m *model.Module) {
	byKind := map[string][]sdk.ToolchainRequirement{}
	var kinds []string
	for _, r := range m.Requirements {
		if _, ok := byKind[r.Kind]; !ok {
			kinds = append(kinds, r.Kind)
		}
		byKind[r.Kind] = append(byKind[r.Kind], r)
	}
	for k := range p.Settings.Toolchains {
		if _, ok := byKind[k]; !ok && e.toolchainPlugin(k) != nil {
			kinds = append(kinds, k)
			byKind[k] = nil
		}
	}
	next := map[string]model.ToolchainSelection{}
	for _, kind := range kinds {
		reqs := byKind[kind]
		sel := model.ToolchainSelection{Kind: kind}
		var cons []string
		var sources []string
		optional := len(reqs) > 0
		for _, r := range reqs {
			if r.Constraint != "" {
				cons = append(cons, r.Constraint)
			}
			if sel.Preferred == "" && r.Preferred != "" {
				sel.Preferred = r.Preferred
			}
			if r.Source != "" {
				sources = append(sources, r.Source)
			}
			if !r.Optional {
				optional = false
			}
		}
		sel.Source = strings.Join(dedupe(sources), ", ")
		ov, hasOv := p.Settings.Toolchains[kind]
		tcs := e.ToolchainsOf(ctx, kind, false)
		if hasOv && ov.ID != "" {
			sel.Override = true
			for _, t := range tcs {
				if t.ID == ov.ID {
					sel.ToolchainID, sel.Version = t.ID, t.Version
				}
			}
			if sel.ToolchainID == "" {
				sel.Missing = true
			}
			next[kind] = sel
			continue
		}
		if hasOv && ov.Constraint != "" {
			sel.Override = true
			cons = []string{ov.Constraint}
			sel.Preferred = ""
		}
		sel.Constraint = strings.Join(cons, " && ")
		if t := pickToolchain(tcs, cons, sel.Preferred); t != nil {
			sel.ToolchainID, sel.Version = t.ID, t.Version
		} else if !optional {
			sel.Missing = true
		}
		next[kind] = sel
	}
	m.Toolchains = next
}

// pickToolchain：满足全部约束 → 优先 preferred → 本机架构 → 非预发布 → 版本最高。
func pickToolchain(tcs []sdk.Toolchain, cons []string, preferred string) *sdk.Toolchain {
	var best *sdk.Toolchain
	score := func(t *sdk.Toolchain) [4]int {
		v, _ := sdk.ParseVersion(t.Version)
		s := [4]int{}
		if preferred != "" && sdk.MatchVersion(preferred, t.Version) {
			s[0] = 1
		}
		if t.Arch == "" || t.Arch == runtime.GOARCH || (t.Arch == "aarch64" && runtime.GOARCH == "arm64") || (t.Arch == "x86_64" && runtime.GOARCH == "amd64") {
			s[1] = 1
		}
		if v.Pre == "" {
			s[2] = 1
		}
		return s
	}
	for i := range tcs {
		t := &tcs[i]
		ok := true
		for _, c := range cons {
			if !sdk.MatchVersion(c, t.Version) {
				ok = false
				break
			}
		}
		if !ok {
			continue
		}
		if best == nil {
			best = t
			continue
		}
		a, b := score(t), score(best)
		if a != b {
			for k := 0; k < 3; k++ {
				if a[k] != b[k] {
					if a[k] > b[k] {
						best = t
					}
					break
				}
			}
			continue
		}
		if sdk.CompareVersions(t.Version, best.Version) > 0 {
			best = t
		}
	}
	return best
}

// pickInstallVersion：推荐版本 → 最新的长期支持版 → 列表第一个（插件按新到旧给出）。
func pickInstallVersion(vs []sdk.AvailableVersion) string {
	for _, v := range vs {
		if v.Recommended {
			return v.Version
		}
	}
	for _, v := range vs {
		if v.LTS {
			return v.Version
		}
	}
	return vs[0].Version
}

// constraintForInstall 从选择推导一个可安装的版本：优先 preferred，其次约束本身。
func constraintForInstall(sel model.ToolchainSelection) string {
	if sel.Preferred != "" {
		return sel.Preferred
	}
	if c, _, _ := strings.Cut(sel.Constraint, " && "); c != "" {
		return c
	}
	return "latest"
}

// ensureToolchains 确保模块所需工具链都已就绪；缺失且可安装时自动下载（输出进 consoleID）。
func (e *Engine) ensureToolchains(ctx context.Context, p *model.Project, m *model.Module, consoleID string) (map[string]sdk.Toolchain, error) {
	auto := e.Store.Settings().AutoInstallToolchains
	for kind, sel := range m.Toolchains {
		if !sel.Missing {
			continue
		}
		plug := e.toolchainPlugin(kind)
		if plug == nil {
			continue
		}
		installable := false
		for _, k := range plug.Manifest.Contributes.Toolchains {
			if k.Kind == kind && k.Installable {
				installable = true
			}
		}
		if !auto || !installable {
			return nil, Errf("err.toolchainMissing", kind, sel.Constraint)
		}
		want := constraintForInstall(sel)
		e.consoleSystem(consoleID, i18n.T("run.installingToolchain", kind, want))
		tc, err := e.installToolchain(ctx, plug, kind, want, consoleID)
		if err != nil {
			return nil, err
		}
		sel.ToolchainID, sel.Version, sel.Missing = tc.ID, tc.Version, false
		m.Toolchains[kind] = sel
		mid := m.ID
		_, _ = e.Store.UpdateProject(p.ID, func(pp *model.Project) error {
			if mm := findModule(pp, mid); mm != nil {
				if mm.Toolchains == nil {
					mm.Toolchains = map[string]model.ToolchainSelection{}
				}
				mm.Toolchains[kind] = sel
			}
			return nil
		})
	}
	return e.moduleToolchains(m), nil
}

// reselectAll 工具链变化后为全部工程重新选择。
func (e *Engine) reselectAll() {
	ctx := context.Background()
	for _, p := range e.Store.Projects("") {
		updated, changed, err := e.reselectProject(ctx, p.ID, nil)
		if err == nil && changed {
			e.publishProject(updated.ID)
		}
	}
}

// reselectProject 重新选工具链并写回工程（mutate 先改工程设置，可为 nil）。
// 选工具链要调插件，插件进程启动时会读设置；放在 Store.UpdateProject 的回调里（持有写锁）会自己等自己，
// 所以在锁外算好、锁内只写结果。
func (e *Engine) reselectProject(ctx context.Context, projectID string, mutate func(p *model.Project)) (*model.Project, bool, error) {
	cur, ok := e.Store.Project(projectID)
	if !ok {
		return nil, false, ErrNotFound
	}
	if mutate != nil {
		mutate(cur)
	}
	sel := map[string]map[string]model.ToolchainSelection{}
	for _, m := range cur.Modules {
		e.selectToolchains(ctx, cur, m)
		sel[m.ID] = m.Toolchains
	}
	changed := false
	updated, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		if mutate != nil {
			mutate(p)
		}
		for _, m := range p.Modules {
			t, ok := sel[m.ID]
			if !ok {
				continue
			}
			before, _ := json.Marshal(m.Toolchains)
			after, _ := json.Marshal(t)
			if string(before) != string(after) {
				m.Toolchains = t
				changed = true
			}
		}
		return nil
	})
	return updated, changed, err
}

func dedupe(xs []string) []string {
	seen := map[string]bool{}
	var out []string
	for _, x := range xs {
		if !seen[x] {
			seen[x] = true
			out = append(out, x)
		}
	}
	return out
}
