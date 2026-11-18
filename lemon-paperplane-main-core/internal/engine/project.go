package engine

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

const maxScanDepth = 4

type detected struct {
	relDir string
	name   string
	facets []sdk.Facet
}

// detect 遍历工程目录，让插件识别模块。
func (e *Engine) detect(ctx context.Context, root string) []detected {
	active := e.Plugins.Active()
	var out []detected
	claimedAll := map[string]bool{}
	claimed := map[string]bool{}
	var walk func(dir string, depth int)
	walk = func(dir string, depth int) {
		if ctx.Err() != nil {
			return
		}
		rel, _ := filepath.Rel(root, dir)
		rel = filepath.ToSlash(rel)
		if rel == "." {
			rel = ""
		}
		entries, err := os.ReadDir(dir)
		if err != nil {
			return
		}
		var names, subdirs []string
		for _, en := range entries {
			if en.IsDir() {
				names = append(names, en.Name()+"/")
				if !sdk.IsGeneratedDir(dir, en.Name()) && !strings.HasPrefix(en.Name(), ".") {
					subdirs = append(subdirs, en.Name())
				}
			} else {
				names = append(names, en.Name())
			}
		}
		var facets []sdk.Facet
		name := ""
		if !claimed[rel] {
			for _, p := range active {
				if !activates(p.Manifest, names, facets) {
					continue
				}
				var r sdk.DetectResult
				cctx, cancel := context.WithTimeout(ctx, 20*time.Second)
				err := e.Plugins.Call(cctx, p, sdk.MethodDetect, &sdk.DetectParams{Root: root, Dir: dir, RelDir: rel, Files: names, Facets: facets}, &r)
				cancel()
				if err != nil {
					if !sdk.IsCode(err, sdk.ErrCodeNotSupported) {
						e.logf("detect %s @%s: %v", p.ID(), rel, err)
					}
					continue
				}
				for _, f := range r.Facets {
					f.Plugin = p.ID()
					facets = append(facets, f)
				}
				if name == "" && r.Name != "" {
					name = r.Name
				}
				for _, c := range r.Claims {
					if c == "**" {
						claimedAll[rel] = true
					} else {
						claimed[strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, c)), "./")] = true
					}
				}
			}
		}
		if len(facets) > 0 {
			if name == "" {
				name = filepath.Base(dir)
			}
			out = append(out, detected{relDir: rel, name: name, facets: facets})
		}
		if claimedAll[rel] || depth >= maxScanDepth {
			return
		}
		for _, s := range subdirs {
			walk(filepath.Join(dir, s), depth+1)
		}
	}
	walk(root, 0)
	return out
}

func activates(m *sdk.Manifest, names []string, facets []sdk.Facet) bool {
	for _, n := range names {
		if sdk.MatchFileName(n, m.Activation.Files) {
			return true
		}
	}
	for _, f := range facets {
		for _, af := range m.Activation.Facets {
			if af == f.ID {
				return true
			}
		}
	}
	return false
}

// ---------------------------------------------------------------------------
// 导入

// Import 导入一个目录为工程（识别 + 分析，返回时已可展示）；需要同步的自动开同步任务。
func (e *Engine) Import(ctx context.Context, path, workspaceID, name string) (*model.Project, error) {
	abs, err := filepath.Abs(sdk.Expand(path))
	if err != nil {
		return nil, err
	}
	st, err := os.Stat(abs)
	if err != nil || !st.IsDir() {
		return nil, Errf("err.notDir", path)
	}
	if p, ok := e.Store.FindProjectByPath(abs); ok {
		return p, nil
	}
	if workspaceID == "" {
		workspaceID = e.Store.DefaultWorkspaceID()
	}
	if name == "" {
		name = filepath.Base(abs)
	}
	p := &model.Project{ID: store.NewID(), Name: name, Path: abs, WorkspaceID: workspaceID, ImportedAt: time.Now()}
	e.mu.Lock()
	e.busy[p.ID] = i18n.T("task.import", name)
	e.mu.Unlock()
	defer func() {
		e.mu.Lock()
		delete(e.busy, p.ID)
		e.mu.Unlock()
	}()
	e.refreshModules(ctx, p)
	if err := e.Store.PutProject(p); err != nil {
		return nil, err
	}
	e.Bus.Publish(model.EvWorkspaces, e.Workspaces())
	e.publishProject(p.ID)
	go e.afterImport(p.ID)
	return p, nil
}

// afterImport 刷新 Git 状态，并对需要同步的模块自动同步。
func (e *Engine) afterImport(projectID string) {
	e.refreshVCSBrief(context.Background(), projectID)
	p, ok := e.Store.Project(projectID)
	if !ok || !e.Store.Settings().AutoSyncOnImport {
		return
	}
	for _, m := range p.Modules {
		if m.NeedsSync && !m.Synced {
			_, _ = e.Sync(projectID, m.ID)
			return
		}
	}
}

// refreshModules 识别 + 分析 + 选工具链 + 合并运行配置 + VCS 根。会修改 p。
// detectRulesVersion 是模块识别规则的版本。改了识别规则（跳过哪些目录、插件怎么认领）就加一，
// 已导入的工程会在后台按新规则重新识别。
//
//	2：跳过 Unity 工程生成的目录（Library 等）；Unity 包清单不算 Node 工程；Gradle 子工程归上层构建
//	3：新增 iOS（Xcode 工程）与安卓插件的识别
const detectRulesVersion = 3

func (e *Engine) refreshModules(ctx context.Context, p *model.Project) {
	p.DetectVersion = detectRulesVersion
	dets := e.detect(ctx, p.Path)
	old := map[string]*model.Module{}
	for _, m := range p.Modules {
		old[m.RelDir] = m
	}
	ide := readIdeConfigs(p.Path)
	var mods []*model.Module
	for _, d := range dets {
		m := &model.Module{ID: sdk.StableID(p.ID, d.relDir), Name: d.name, RelDir: d.relDir, Facets: d.facets, Models: map[string]json.RawMessage{}, Analysis: map[string]*model.ModuleAnalysis{}}
		if om := old[d.relDir]; om != nil {
			m.ID = om.ID
			m.Models = om.Models
			if m.Models == nil {
				m.Models = map[string]json.RawMessage{}
			}
			m.Toolchains = om.Toolchains
			m.RunConfigs = om.RunConfigs
			m.Synced = om.Synced
		}
		mods = append(mods, m)
	}
	// 这次没识别出来的目录：如果用户在里面建过运行配置（如加到「启动」的脚本、自定义命令），保留模块和这些配置，
	// 去掉已失效的技术标签和插件生成的配置
	detected := map[string]bool{}
	for _, d := range dets {
		detected[d.relDir] = true
	}
	for _, om := range p.Modules {
		if detected[om.RelDir] {
			continue
		}
		var keep []*model.RunConfig
		for _, c := range om.RunConfigs {
			if c.Source == "user" {
				keep = append(keep, c)
			}
		}
		if len(keep) > 0 {
			mods = append(mods, &model.Module{ID: om.ID, Name: om.Name, RelDir: om.RelDir, RunConfigs: keep,
				Models: map[string]json.RawMessage{}, Analysis: map[string]*model.ModuleAnalysis{}})
		}
	}
	// 各模块并行分析（模块内按插件拓扑序）
	var wg sync.WaitGroup
	sem := make(chan struct{}, 4)
	for _, m := range mods {
		wg.Add(1)
		go func(m *model.Module) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			e.analyzeModule(ctx, p, m, ide, false)
		}(m)
	}
	wg.Wait()
	for _, m := range mods {
		m.Fingerprint = e.fingerprint(filepath.Join(p.Path, filepath.FromSlash(m.RelDir)))
	}
	p.Modules = mods
	p.AnalyzedAt = time.Now()
	e.fixDefaultConfig(p)
	p.VCSRoots = e.findVCSRoots(p.Path)
}

// analyzeModule 让参与的插件依次分析模块。onlyNonSync=true 时跳过实现 sync 的插件（同步之后用）。
func (e *Engine) analyzeModule(ctx context.Context, p *model.Project, m *model.Module, ide []sdk.IdeRunConfig, onlyNonSync bool) {
	if m.Analysis == nil {
		m.Analysis = map[string]*model.ModuleAnalysis{}
	}
	var reqs []sdk.ToolchainRequirement
	var configs []sdk.RunConfiguration
	var configPlugins []string
	needsSync := false
	var depState *sdk.DependencyState
	settings := e.Store.Settings()
	for _, plug := range e.Plugins.Active() {
		if !participates(plug, m) {
			continue
		}
		if onlyNonSync && plug.Manifest.Contributes.Sync {
			// 保留它之前的结果
			if a := m.Analysis[plug.ID()]; a != nil && a.Dependencies != nil {
				depState = worseDeps(depState, a.Dependencies)
			}
			for _, c := range m.RunConfigs {
				if c.Plugin == plug.ID() && !c.Edited && c.Source != "user" {
					configs = append(configs, c.RunConfiguration)
					configPlugins = append(configPlugins, plug.ID())
				}
			}
			reqs = append(reqs, pluginReqs(m, plug.ID())...)
			continue
		}
		var mine []sdk.IdeRunConfig
		for _, c := range ide {
			for _, t := range plug.Manifest.Contributes.IdeRunConfigTypes {
				if t == c.Type {
					mine = append(mine, c)
				}
			}
		}
		var r sdk.AnalyzeResult
		cctx, cancel := context.WithTimeout(ctx, 60*time.Second)
		err := e.Plugins.Call(cctx, plug, sdk.MethodAnalyze, &sdk.AnalyzeParams{
			Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Toolchains: e.moduleToolchains(m),
			IdeConfigs: mine, Settings: e.pluginSettings(plug, settings), Env: p.Settings.Env,
		}, &r)
		cancel()
		if err != nil {
			if !sdk.IsCode(err, sdk.ErrCodeNotSupported) {
				e.logf("analyze %s @%s: %v", plug.ID(), m.RelDir, err)
				m.Analysis[plug.ID()] = &model.ModuleAnalysis{At: time.Now(), Problems: []sdk.Diagnosis{{Severity: "warning", Title: i18n.T("diag.analyzeFailed", plug.T(plug.Manifest.Name)), Message: errString(err), Plugin: plug.ID()}}}
			}
			continue
		}
		if len(r.Model) > 0 {
			m.Models[plug.ID()] = r.Model
		}
		for i := range r.Requirements {
			r.Requirements[i].Purpose = strings.TrimSpace(r.Requirements[i].Purpose)
		}
		reqs = append(reqs, tagReqs(r.Requirements, plug.ID())...)
		for _, c := range r.RunConfigurations {
			configs = append(configs, c)
			configPlugins = append(configPlugins, plug.ID())
		}
		for i := range r.Problems {
			r.Problems[i].Plugin = plug.ID()
		}
		for i := range r.Info {
			r.Info[i].Icon = plug.Icon(r.Info[i].Icon)
		}
		m.Analysis[plug.ID()] = &model.ModuleAnalysis{Info: r.Info, Problems: r.Problems, OptionValues: r.OptionValues, Dependencies: r.Dependencies, At: time.Now()}
		if r.Dependencies != nil {
			depState = worseDeps(depState, r.Dependencies)
		}
		if r.NeedsSync {
			needsSync = true
		}
	}
	m.Requirements = reqs
	m.Dependencies = depState
	if !onlyNonSync {
		m.NeedsSync = needsSync
	}
	mergeConfigs(m, configs, configPlugins)
	e.selectToolchains(ctx, p, m)
}

// 需求里记一下来源插件（Purpose 前缀），便于同步后保留。
func tagReqs(rs []sdk.ToolchainRequirement, plugin string) []sdk.ToolchainRequirement {
	out := make([]sdk.ToolchainRequirement, len(rs))
	for i, r := range rs {
		if r.Purpose == "" {
			r.Purpose = plugin
		} else if !strings.HasPrefix(r.Purpose, plugin+":") {
			r.Purpose = plugin + ":" + r.Purpose
		}
		out[i] = r
	}
	return out
}

func pluginReqs(m *model.Module, plugin string) []sdk.ToolchainRequirement {
	var out []sdk.ToolchainRequirement
	for _, r := range m.Requirements {
		if r.Purpose == plugin || strings.HasPrefix(r.Purpose, plugin+":") {
			out = append(out, r)
		}
	}
	return out
}

var depRank = map[string]int{"none": 0, "ok": 1, "unknown": 2, "outdated": 3, "missing": 4}

func worseDeps(a, b *sdk.DependencyState) *sdk.DependencyState {
	if a == nil {
		return b
	}
	if b == nil {
		return a
	}
	if depRank[b.State] > depRank[a.State] {
		return b
	}
	return a
}

// mergeConfigs 合并插件生成的运行配置与用户配置。
func mergeConfigs(m *model.Module, configs []sdk.RunConfiguration, plugs []string) {
	existing := map[string]*model.RunConfig{}
	for _, c := range m.RunConfigs {
		existing[c.ID] = c
	}
	next := []*model.RunConfig{}
	seen := map[string]bool{}
	for i, c := range configs {
		c.Plugin = plugs[i]
		c.ModuleID = m.ID
		if c.ID == "" {
			c.ID = sdk.StableID(m.ID, c.Plugin, c.Type, c.Name)
		} else if _, final := existing[c.ID]; !final {
			// 插件给的 id 只保证插件内稳定，加上模块与插件前缀保证全局唯一
			c.ID = sdk.StableID(m.ID, c.Plugin, c.ID)
		}
		if seen[c.ID] {
			continue
		}
		seen[c.ID] = true
		if c.Source == "" {
			c.Source = "auto"
		}
		if old := existing[c.ID]; old != nil && old.Edited {
			old.Stale = false
			next = append(next, old)
			continue
		}
		next = append(next, &model.RunConfig{RunConfiguration: c})
	}
	for _, c := range m.RunConfigs {
		if seen[c.ID] {
			continue
		}
		if c.Source == "user" {
			next = append(next, c)
		} else if c.Edited {
			c.Stale = true
			next = append(next, c)
		}
	}
	sort.SliceStable(next, func(i, j int) bool {
		a, b := next[i], next[j]
		if a.Default != b.Default {
			return a.Default
		}
		// 都是默认时，框架插件的配置比语言插件的更具体
		if fa, fb := isFramework(a.Plugin), isFramework(b.Plugin); fa != fb {
			return fa
		}
		return a.Priority > b.Priority
	})
	m.RunConfigs = next
}

// isFramework 判断插件 manifest 的 category 是否为 framework（由 Engine 初始化时设置）。
var isFramework = func(plugin string) bool { return false }

func (e *Engine) fixDefaultConfig(p *model.Project) {
	if p.DefaultConfigID != "" && p.DefaultByUser {
		if _, c := findConfig(p, p.DefaultConfigID); c != nil {
			return
		}
	}
	p.DefaultByUser = false
	p.DefaultConfigID = ""
	// 打分选默认运行配置：插件标了默认 > 框架插件生成的 > 所在模块用了框架（是「应用」而不是脚本目录）> 目录层级浅
	best, bestScore := "", -1e9
	for _, m := range p.Modules {
		frameworkModule := false
		for _, f := range m.Facets {
			if isFramework(f.Plugin) {
				frameworkModule = true
			}
		}
		depth := float64(strings.Count(m.RelDir, "/"))
		if m.RelDir != "" {
			depth++
		}
		for i, c := range m.RunConfigs {
			score := -depth*0.5 - float64(i)*0.01
			if c.Default {
				score += 10
			}
			if isFramework(c.Plugin) {
				score += 4
			}
			if frameworkModule {
				score += 3
			}
			if c.Source == "idea" || c.Source == "vscode" || c.Source == "user" {
				score += 6 // 用户在 IDE 里配过的，最可能是真正在用的入口
			}
			if score > bestScore {
				best, bestScore = c.ID, score
			}
		}
	}
	p.DefaultConfigID = best
}

// Reimport 重新识别与分析（任务）。
func (e *Engine) Reimport(projectID string) (model.Task, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return model.Task{}, ErrNotFound
	}
	return e.NewTask("reimport", i18n.T("task.reimport", p.Name), projectID, "", func(ctx context.Context, ts *taskState) (any, error) {
		cur, ok := e.Store.Project(projectID)
		if !ok {
			return nil, ErrNotFound
		}
		ts.console.System(i18n.T("task.detecting"))
		e.refreshModules(ctx, cur)
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		_, err := e.Store.UpdateProject(projectID, func(pp *model.Project) error {
			pp.Modules, pp.AnalyzedAt, pp.VCSRoots, pp.DefaultConfigID, pp.DefaultByUser = cur.Modules, cur.AnalyzedAt, cur.VCSRoots, cur.DefaultConfigID, cur.DefaultByUser
			pp.DetectVersion = cur.DetectVersion
			return nil
		})
		for _, m := range cur.Modules {
			var fs []string
			for _, f := range m.Facets {
				fs = append(fs, f.ID)
			}
			ts.console.System(strings.TrimSpace(orDot(m.RelDir) + "  " + strings.Join(fs, ", ")))
		}
		e.publishProject(projectID)
		go e.afterImport(projectID)
		return nil, err
	}), nil
}

func orDot(s string) string {
	if s == "" {
		return "."
	}
	return s
}

// Sync 对模块执行插件 module/sync（任务）。moduleID 为空表示全部需要同步的模块。
func (e *Engine) Sync(projectID, moduleID string) (model.Task, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return model.Task{}, ErrNotFound
	}
	return e.NewTask("sync", i18n.T("task.sync", p.Name), projectID, moduleID, func(ctx context.Context, ts *taskState) (any, error) {
		return nil, e.syncModules(ctx, projectID, moduleID, ts.t.ID)
	}), nil
}

// syncModules 同步执行（consoleID 为输出控制台：任务或运行）。
func (e *Engine) syncModules(ctx context.Context, projectID, moduleID, consoleID string) error {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return ErrNotFound
	}
	var lastErr error
	for _, m := range p.Modules {
		if moduleID != "" && m.ID != moduleID {
			continue
		}
		var syncers []*plugins.Plugin
		for _, plug := range e.Plugins.Active() {
			if plug.Manifest.Contributes.Sync && participates(plug, m) {
				syncers = append(syncers, plug)
			}
		}
		if len(syncers) == 0 {
			continue
		}
		tcs, err := e.ensureToolchains(ctx, p, m, consoleID)
		if err != nil {
			lastErr = err
			continue
		}
		settings := e.Store.Settings()
		for _, plug := range syncers {
			var r sdk.AnalyzeResult
			err := e.Plugins.Call(ctx, plug, sdk.MethodSync, &sdk.AnalyzeParams{
				Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Toolchains: tcs, Settings: e.pluginSettings(plug, settings), TaskID: consoleID, Env: p.Settings.Env,
			}, &r)
			if err != nil {
				lastErr = err
				e.diagnoseFailure(ctx, p, m, nil, "sync", consoleID, nil)
				continue
			}
			if len(r.Model) > 0 {
				m.Models[plug.ID()] = r.Model
			}
			for i := range r.Info {
				r.Info[i].Icon = plug.Icon(r.Info[i].Icon)
			}
			a := &model.ModuleAnalysis{Info: r.Info, Problems: r.Problems, OptionValues: r.OptionValues, Dependencies: r.Dependencies, At: time.Now()}
			if prev := m.Analysis[plug.ID()]; prev != nil {
				if len(a.Info) == 0 {
					a.Info = prev.Info
				}
				if a.OptionValues == nil {
					a.OptionValues = prev.OptionValues
				}
			}
			m.Analysis[plug.ID()] = a
			if len(r.RunConfigurations) > 0 {
				var others []sdk.RunConfiguration
				var plugs []string
				for _, c := range m.RunConfigs {
					if c.Plugin != plug.ID() && !c.Edited && c.Source != "user" {
						others = append(others, c.RunConfiguration)
						plugs = append(plugs, c.Plugin)
					}
				}
				for _, c := range r.RunConfigurations {
					others = append(others, c)
					plugs = append(plugs, plug.ID())
				}
				mergeConfigs(m, others, plugs)
			}
			if len(r.Requirements) > 0 {
				var keep []sdk.ToolchainRequirement
				for _, q := range m.Requirements {
					if q.Purpose != plug.ID() && !strings.HasPrefix(q.Purpose, plug.ID()+":") {
						keep = append(keep, q)
					}
				}
				m.Requirements = append(keep, tagReqs(r.Requirements, plug.ID())...)
			}
		}
		if lastErr == nil {
			m.Synced = true
			m.NeedsSync = false
		}
		// 同步后让其他插件看到新模型
		e.analyzeModule(ctx, p, m, readIdeConfigs(p.Path), true)
	}
	e.fixDefaultConfig(p)
	_, err := e.Store.UpdateProject(projectID, func(pp *model.Project) error {
		pp.Modules, pp.DefaultConfigID = p.Modules, p.DefaultConfigID
		return nil
	})
	if err != nil {
		return err
	}
	e.publishProject(projectID)
	return lastErr
}

// ScanFolder 在目录下找候选工程（用于批量导入）。
type ScanResult struct {
	Path      string             `json:"path"`
	Name      string             `json:"name"`
	Facets    []model.FacetBadge `json:"facets"`
	Imported  bool               `json:"imported"`
	ProjectID string             `json:"projectId,omitempty"`
}

// ScanFolder：有 .git 的子目录或能被插件识别的子目录作为候选工程；目录本身是工程时只返回它。
func (e *Engine) ScanFolder(ctx context.Context, path string, maxDepth int) ([]ScanResult, error) {
	abs, err := filepath.Abs(sdk.Expand(path))
	if err != nil {
		return nil, err
	}
	if maxDepth <= 0 {
		maxDepth = 2
	}
	var cands []string
	var walk func(dir string, depth int)
	walk = func(dir string, depth int) {
		if depth > 0 {
			if sdk.DirExists(filepath.Join(dir, ".git")) || sdk.FileExists(filepath.Join(dir, ".git")) {
				cands = append(cands, dir)
				return
			}
			if len(e.quickFacets(dir)) > 0 {
				cands = append(cands, dir)
				return
			}
		}
		if depth >= maxDepth {
			return
		}
		for _, s := range subdirsOf(dir) {
			if sdk.IsGeneratedDir(dir, filepath.Base(s)) {
				continue
			}
			walk(s, depth+1)
		}
	}
	if sdk.DirExists(filepath.Join(abs, ".git")) || len(e.quickFacets(abs)) > 0 {
		cands = []string{abs}
	} else {
		walk(abs, 0)
	}
	var out []ScanResult
	var mu sync.Mutex
	var wg sync.WaitGroup
	sem := make(chan struct{}, 6)
	for _, c := range cands {
		wg.Add(1)
		go func(c string) {
			defer wg.Done()
			sem <- struct{}{}
			defer func() { <-sem }()
			r := ScanResult{Path: c, Name: filepath.Base(c), Facets: []model.FacetBadge{}}
			seen := map[string]bool{}
			for _, d := range e.detect(ctx, c) {
				for _, f := range d.facets {
					if !seen[f.ID] {
						seen[f.ID] = true
						r.Facets = append(r.Facets, e.facetBadge(f))
					}
				}
			}
			if p, ok := e.Store.FindProjectByPath(c); ok {
				r.Imported, r.ProjectID = true, p.ID
			}
			if len(r.Facets) == 0 && !sdk.DirExists(filepath.Join(c, ".git")) {
				return
			}
			mu.Lock()
			out = append(out, r)
			mu.Unlock()
		}(c)
	}
	wg.Wait()
	sort.Slice(out, func(i, j int) bool { return out[i].Path < out[j].Path })
	return out, nil
}

// quickFacets 只看激活文件是否存在（不调插件）。
func (e *Engine) quickFacets(dir string) []string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var names []string
	for _, en := range entries {
		n := en.Name()
		if en.IsDir() {
			n += "/"
		}
		names = append(names, n)
	}
	var out []string
	for _, p := range e.Plugins.Active() {
		if p.Manifest.Contributes.VCS != nil {
			continue
		}
		if activates(p.Manifest, names, nil) {
			out = append(out, p.ID())
		}
	}
	return out
}

// ---------------------------------------------------------------------------
// IDEA / VS Code 运行配置读取

var (
	ideaCfgRe     = regexp.MustCompile(`(?s)<configuration ([^>]*)>(.*?)</configuration>`)
	ideaAttrRe    = regexp.MustCompile(`([\w.-]+)="([^"]*)"`)
	ideaOptRe     = regexp.MustCompile(`<option name="([^"]+)" value="([^"]*)"`)
	ideaModRe     = regexp.MustCompile(`<module name="([^"]+)"`)
	ideaEnvRe     = regexp.MustCompile(`<env name="([^"]+)" value="([^"]*)"`)
	recentTempRe  = regexp.MustCompile(`(?s)<recent_temporary>\s*<list>\s*<item itemvalue="([^"]+)"`)
	jsonCommentRe = regexp.MustCompile(`(?m)^\s*//.*$`)
)

func xmlUnescape(s string) string {
	r := strings.NewReplacer("&quot;", `"`, "&apos;", "'", "&lt;", "<", "&gt;", ">", "&#10;", "\n", "&amp;", "&")
	return r.Replace(s)
}

// readIdeConfigs 读取 .idea/workspace.xml、.run/*.run.xml、.vscode/launch.json 中的运行配置。
func readIdeConfigs(root string) []sdk.IdeRunConfig {
	var out []sdk.IdeRunConfig
	files := []string{filepath.Join(root, ".idea", "workspace.xml")}
	if m, _ := filepath.Glob(filepath.Join(root, ".run", "*.run.xml")); m != nil {
		files = append(files, m...)
	}
	if m, _ := filepath.Glob(filepath.Join(root, ".idea", "runConfigurations", "*.xml")); m != nil {
		files = append(files, m...)
	}
	selectedRe := regexp.MustCompile(`<component name="RunManager"[^>]*selected="([^"]*)"`)
	for _, f := range files {
		data := sdk.ReadText(f, 0)
		selected := ""
		if sm := selectedRe.FindStringSubmatch(data); sm != nil {
			selected = xmlUnescape(sm[1])
		} else if rm := recentTempRe.FindStringSubmatch(data); rm != nil {
			// 没有 selected 时取最近使用的临时配置（PyCharm 常见）
			selected = xmlUnescape(rm[1])
		}
		for _, mm := range ideaCfgRe.FindAllStringSubmatch(data, -1) {
			attrs := map[string]string{}
			for _, a := range ideaAttrRe.FindAllStringSubmatch(mm[1], -1) {
				attrs[a[1]] = xmlUnescape(a[2])
			}
			if attrs["type"] == "" || attrs["default"] == "true" {
				continue
			}
			// 临时配置（IDEA 点运行时自动生成的）只导入当前选中的那一个，它往往就是用户实际在用的
			// selected 形如「<factoryName>.<name>」，例如 "Spring Boot.App"
			if attrs["temporary"] == "true" && selected != attrs["factoryName"]+"."+attrs["name"] && selected != attrs["type"]+"."+attrs["name"] {
				continue
			}
			c := sdk.IdeRunConfig{Source: "idea", Type: attrs["type"], Name: attrs["name"], Options: map[string]string{}, Env: map[string]string{}}
			for _, o := range ideaOptRe.FindAllStringSubmatch(mm[2], -1) {
				c.Options[o[1]] = xmlUnescape(o[2])
			}
			for _, en := range ideaEnvRe.FindAllStringSubmatch(mm[2], -1) {
				c.Env[en[1]] = xmlUnescape(en[2])
			}
			if mod := ideaModRe.FindStringSubmatch(mm[2]); mod != nil {
				c.Module = mod[1]
			}
			out = append(out, c)
		}
	}
	// VS Code launch.json（允许注释与尾逗号的宽松解析）
	if data := sdk.ReadText(filepath.Join(root, ".vscode", "launch.json"), 0); data != "" {
		clean := jsonCommentRe.ReplaceAllString(data, "")
		clean = regexp.MustCompile(`,(\s*[}\]])`).ReplaceAllString(clean, "$1")
		var lj struct {
			Configurations []map[string]any `json:"configurations"`
		}
		if json.Unmarshal([]byte(clean), &lj) == nil {
			for _, c := range lj.Configurations {
				ic := sdk.IdeRunConfig{Source: "vscode", Options: map[string]string{}, Env: map[string]string{}}
				for k, v := range c {
					switch k {
					case "type":
						ic.Type, _ = v.(string)
					case "name":
						ic.Name, _ = v.(string)
					case "env":
						if m, ok := v.(map[string]any); ok {
							for ek, ev := range m {
								ic.Env[ek], _ = ev.(string)
							}
						}
					default:
						switch t := v.(type) {
						case string:
							ic.Options[k] = t
						default:
							b, _ := json.Marshal(t)
							ic.Options[k] = string(b)
						}
					}
				}
				if ic.Type != "" {
					out = append(out, ic)
				}
			}
		}
	}
	return out
}

// fingerprint 计算目录里与插件激活规则匹配的文件（构建文件、锁文件、版本文件）的指纹。
func (e *Engine) fingerprint(dir string) string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return ""
	}
	var pats []string
	for _, p := range e.Plugins.Active() {
		pats = append(pats, p.Manifest.Activation.Files...)
	}
	pats = append(pats, ".nvmrc", ".node-version", ".python-version", ".tool-versions", "*.lock", "*-lock.json", "*-lock.yaml", "go.sum", "gradle.properties")
	var parts []string
	for _, en := range entries {
		if en.IsDir() || !sdk.MatchFileName(en.Name(), pats) {
			continue
		}
		info, err := en.Info()
		if err != nil {
			continue
		}
		parts = append(parts, en.Name(), info.ModTime().UTC().Format(time.RFC3339Nano), strconv.FormatInt(info.Size(), 10))
	}
	return sdk.StableID(parts...)
}

// checkStale 发现构建文件变化、或用旧版识别规则识别的工程，后台自动重新分析（不做重型同步）。
func (e *Engine) checkStale() {
	upgraded := 0
	for _, p := range e.Store.Projects("") {
		e.mu.Lock()
		busy := e.busy[p.ID] != ""
		e.mu.Unlock()
		if busy {
			continue
		}
		if p.DetectVersion < detectRulesVersion {
			// 规则升级：每轮最多重新识别几个，避免升级后一次拉起所有工程的分析
			if upgraded < 4 {
				upgraded++
				e.logf("project %s detected with rules v%d, re-detecting with v%d", p.Name, p.DetectVersion, detectRulesVersion)
				_, _ = e.Reimport(p.ID)
			}
			continue
		}
		for _, m := range p.Modules {
			if m.Fingerprint == "" {
				continue
			}
			if fp := e.fingerprint(filepath.Join(p.Path, filepath.FromSlash(m.RelDir))); fp != m.Fingerprint {
				e.logf("project %s changed (%s), reanalyzing", p.Name, orDot(m.RelDir))
				_, _ = e.Reimport(p.ID)
				break
			}
		}
	}
}
