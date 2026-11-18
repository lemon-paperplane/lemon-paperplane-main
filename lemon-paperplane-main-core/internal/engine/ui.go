package engine

import (
	"context"
	"encoding/json"
	"sort"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// ---------------------------------------------------------------------------
// 卡片摘要

func (e *Engine) facetBadge(f sdk.Facet) model.FacetBadge {
	b := model.FacetBadge{ID: f.ID, Name: f.ID, Plugin: f.Plugin}
	if p := e.Plugins.Get(f.Plugin); p != nil {
		for _, d := range p.Manifest.Contributes.Facets {
			if d.ID == f.ID {
				b.Name, b.Icon, b.Category = p.T(d.Name), p.Icon(d.Icon), d.Category
			}
		}
	}
	return b
}

var categoryRank = map[string]int{"framework": 0, "language": 1, "runtime": 2, "buildSystem": 3, "packageManager": 4}

// Summary 生成工程卡片摘要。
func (e *Engine) Summary(p *model.Project) model.ProjectSummary {
	s := model.ProjectSummary{ID: p.ID, Name: p.Name, Path: p.Path, WorkspaceID: p.WorkspaceID, DefaultConfigID: p.DefaultConfigID, Configs: []model.ConfigBrief{}, Facets: []model.FacetBadge{},
		Launch: launchIDs(p)}
	seen := map[string]bool{}
	prio := map[string]int{}
	for _, m := range p.Modules {
		for _, f := range m.Facets {
			if seen[f.ID] {
				continue
			}
			seen[f.ID] = true
			b := e.facetBadge(f)
			s.Facets = append(s.Facets, b)
			if pl := e.Plugins.Get(f.Plugin); pl != nil {
				for _, d := range pl.Manifest.Contributes.Facets {
					if d.ID == f.ID {
						prio[f.ID] = d.Priority
					}
				}
			}
		}
	}
	sort.SliceStable(s.Facets, func(i, j int) bool {
		a, b := s.Facets[i], s.Facets[j]
		if prio[a.ID] != prio[b.ID] {
			return prio[a.ID] > prio[b.ID]
		}
		ra, okA := categoryRank[a.Category]
		rb, okB := categoryRank[b.Category]
		if !okA {
			ra = 9
		}
		if !okB {
			rb = 9
		}
		return ra < rb
	})
	// info：根模块优先
	mods := append([]*model.Module{}, p.Modules...)
	sort.SliceStable(mods, func(i, j int) bool { return len(mods[i].RelDir) < len(mods[j].RelDir) })
	infoSeen := map[string]bool{}
	for _, m := range mods {
		var ids []string
		for id := range m.Analysis {
			ids = append(ids, id)
		}
		sort.Strings(ids)
		for _, id := range ids {
			for _, it := range m.Analysis[id].Info {
				if len(s.Info) >= 4 || infoSeen[it.Label] {
					continue
				}
				infoSeen[it.Label] = true
				s.Info = append(s.Info, it)
			}
		}
		for _, c := range m.RunConfigs {
			icon := ""
			if pl := e.Plugins.Get(c.Plugin); pl != nil {
				for _, t := range pl.Manifest.Contributes.RunConfigurationTypes {
					if t.ID == c.Type {
						icon = pl.Icon(t.Icon)
					}
				}
			}
			s.Configs = append(s.Configs, model.ConfigBrief{ID: c.ID, Name: c.Name, Type: c.Type, Icon: icon, ModuleID: m.ID, ModuleName: m.Name})
		}
		for kind, sel := range m.Toolchains {
			if sel.Missing {
				s.MissingToolchains = append(s.MissingToolchains, strings.TrimSpace(kind+" "+firstNonEmptyStr(sel.Preferred, sel.Constraint)))
			}
		}
		if m.Dependencies != nil && depRank[m.Dependencies.State] > depRank[s.Dependencies] {
			s.Dependencies = m.Dependencies.State
		}
	}
	probs := e.ProjectProblems(p.ID)
	s.Problems = len(probs)
	sevRank := map[string]int{"info": 1, "warning": 2, "error": 3}
	for _, d := range probs {
		if sevRank[d.Severity] > sevRank[s.ProblemSeverity] {
			s.ProblemSeverity = d.Severity
		}
	}
	// 运行
	s.Status = "idle"
	var failed bool
	running, starting := 0, 0
	for _, r := range e.Runs(p.ID, false) {
		if active(r.State) {
			s.Runs = append(s.Runs, model.RunBrief{ID: r.ID, ProjectID: r.ProjectID, ConfigID: r.ConfigID, Name: r.Name, State: r.State, URLs: r.URLs, CPU: r.CPU, RSSMB: r.RSSMB, PeakRSSMB: r.PeakRSSMB, Procs: r.Procs})
			switch r.State {
			case model.RunRunning:
				running++
			case model.RunStopping:
				if s.Status == "idle" {
					s.Status = "stopping"
				}
			default:
				starting++
			}
		}
	}
	// 最近一次结束的默认配置运行失败，卡片显示失败
	if len(s.Runs) == 0 {
		for _, r := range e.Runs(p.ID, false) {
			if r.State == model.RunFailed && r.EndedAt != nil && time.Since(*r.EndedAt) < 30*time.Minute {
				failed = true
			}
			break
		}
	}
	switch {
	case starting > 0:
		s.Status = "starting"
	case running > 0:
		s.Status = "running"
	case failed:
		s.Status = "failed"
	}
	e.mu.Lock()
	if b := e.vcsBrief[p.ID]; b != nil {
		c := *b
		s.VCS = &c
	}
	s.Busy = e.busy[p.ID]
	e.mu.Unlock()
	return s
}

// ProjectSummaries 返回工作区的卡片列表。
func (e *Engine) ProjectSummaries(workspaceID string) []model.ProjectSummary {
	out := []model.ProjectSummary{}
	for _, p := range e.Store.Projects(workspaceID) {
		out = append(out, e.Summary(p))
	}
	return out
}

func (e *Engine) publishProject(id string) {
	p, ok := e.Store.Project(id)
	if !ok {
		return
	}
	e.Bus.Publish(model.EvProject, e.Summary(p))
}

// ---------------------------------------------------------------------------
// 界面贡献

// Contributions 汇总全部插件的界面贡献（已本地化）。
func (e *Engine) Contributions() model.Contributions {
	c := model.Contributions{Facets: []model.FacetBadge{}, ToolchainKinds: e.ToolchainKinds(), RunConfigTypes: []model.RunConfigTypeInfo{}, Views: []model.ViewInfo{}, Actions: []model.ActionInfo{}, Settings: []model.PluginSettingsInfo{}, VCS: []model.VCSInfo{},
		Languages: []model.LanguageInfo{}, Keymaps: []model.KeymapInfo{}, EditorThemes: []model.EditorThemeInfo{}, DefaultKeymap: DefaultKeymap()}
	s := e.Store.Settings()
	for _, p := range e.Plugins.Active() {
		mc := p.Manifest.Contributes
		for _, f := range mc.Facets {
			c.Facets = append(c.Facets, model.FacetBadge{ID: f.ID, Name: p.T(f.Name), Icon: p.Icon(f.Icon), Category: f.Category, Plugin: p.ID()})
		}
		for _, t := range mc.RunConfigurationTypes {
			c.RunConfigTypes = append(c.RunConfigTypes, model.RunConfigTypeInfo{RunConfigTypeDef: localizeRCType(p, t), Plugin: p.ID()})
		}
		for _, v := range mc.Views {
			v.Title = p.T(v.Title)
			v.Icon = p.Icon(v.Icon)
			c.Views = append(c.Views, model.ViewInfo{ViewDef: v, Plugin: p.ID()})
		}
		for _, a := range mc.Actions {
			a.Title = p.T(a.Title)
			a.Icon = p.Icon(a.Icon)
			a.Confirm = p.T(a.Confirm)
			a.Form = localizeFields(p, a.Form)
			if len(a.Locations) == 0 {
				a.Locations = []string{sdk.ActProjectMenu}
			}
			c.Actions = append(c.Actions, model.ActionInfo{ActionDef: a, Plugin: p.ID()})
		}
		if len(mc.Settings) > 0 {
			c.Settings = append(c.Settings, model.PluginSettingsInfo{Plugin: p.ID(), Title: p.T(p.Manifest.Name), Fields: localizeFields(p, mc.Settings), Values: e.pluginSettings(p, s)})
		}
		if mc.VCS != nil {
			c.VCS = append(c.VCS, model.VCSInfo{VCSDef: *mc.VCS, Plugin: p.ID()})
		}
		for _, l := range mc.Languages {
			l.Name = p.T(l.Name)
			l.Module = p.Icon(l.Module)
			l.Icon = p.Icon(l.Icon)
			c.Languages = append(c.Languages, model.LanguageInfo{LanguageDef: l, Plugin: p.ID()})
		}
		for _, k := range mc.Keymaps {
			k.Name = p.T(k.Name)
			k.File = p.Icon(k.File)
			k.Module = p.Icon(k.Module)
			c.Keymaps = append(c.Keymaps, model.KeymapInfo{KeymapDef: k, Plugin: p.ID()})
		}
		for _, t := range mc.EditorThemes {
			t.Name = p.T(t.Name)
			t.File = p.Icon(t.File)
			c.EditorThemes = append(c.EditorThemes, model.EditorThemeInfo{EditorThemeDef: t, Plugin: p.ID()})
		}
	}
	return c
}

// ---------------------------------------------------------------------------
// 插件界面与工具

func (e *Engine) projectModule(projectID, moduleID string) (*sdk.ProjectInfo, *sdk.ModuleInfo) {
	if projectID == "" {
		return nil, nil
	}
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil, nil
	}
	pi := e.projectInfo(p)
	m := findModule(p, moduleID)
	if m == nil && len(p.Modules) > 0 {
		m = p.Modules[0]
	}
	if m == nil {
		return &pi, nil
	}
	mi := e.moduleInfo(p, m)
	return &pi, &mi
}

// RenderView 渲染插件界面。
func (e *Engine) RenderView(ctx context.Context, pluginID, view, projectID, moduleID, runID string, state map[string]any) (json.RawMessage, error) {
	plug := e.Plugins.ActiveGet(pluginID)
	if plug == nil {
		return nil, ErrNotFound
	}
	if runID != "" && projectID == "" {
		if r, ok := e.Run(runID); ok {
			projectID, moduleID = r.ProjectID, r.ModuleID
		}
	}
	for _, v := range plug.Manifest.Contributes.Views {
		if v.ID == view && v.Kind == "webview" {
			// webview 视图不调插件：返回页面地址，由界面用内嵌浏览器加载
			q := "?token=" + e.token + "&projectId=" + projectID + "&moduleId=" + moduleID + "&runId=" + runID
			b, _ := json.Marshal(map[string]any{"webviewUrl": e.apiBase + "/plugins/" + plug.ID() + "/" + strings.TrimPrefix(v.Entry, "/") + q})
			return b, nil
		}
	}
	pi, mi := e.projectModule(projectID, moduleID)
	var res json.RawMessage
	err := e.Plugins.Call(ctx, plug, sdk.MethodViewRender, &sdk.ViewRenderParams{View: view, Project: pi, Module: mi, RunID: runID, Run: e.runInfo(runID), State: state}, &res)
	return res, err
}

// ViewEvent 处理插件界面事件。
func (e *Engine) ViewEvent(ctx context.Context, pluginID, view, event string, values map[string]any, projectID, moduleID, runID string, state map[string]any) (*sdk.ViewEventResult, error) {
	plug := e.Plugins.ActiveGet(pluginID)
	if plug == nil {
		return nil, ErrNotFound
	}
	if runID != "" && projectID == "" {
		if r, ok := e.Run(runID); ok {
			projectID, moduleID = r.ProjectID, r.ModuleID
		}
	}
	pi, mi := e.projectModule(projectID, moduleID)
	var res sdk.ViewEventResult
	err := e.Plugins.Call(ctx, plug, sdk.MethodViewEvent, &sdk.ViewEventParams{View: view, Event: event, Values: values, Project: pi, Module: mi, RunID: runID, Run: e.runInfo(runID), State: state}, &res)
	if err == nil && res.Reimport && projectID != "" {
		_, _ = e.Reimport(projectID)
	}
	return &res, err
}

// PluginTool 是插件声明的一个工具。
type PluginTool struct {
	Plugin *plugins.Plugin
	Def    sdk.ToolDef
}

// PluginTools 列出全部插件工具。
func (e *Engine) PluginTools() []PluginTool {
	var out []PluginTool
	for _, p := range e.Plugins.Active() {
		for _, t := range p.Manifest.Contributes.Tools {
			out = append(out, PluginTool{Plugin: p, Def: t})
		}
	}
	return out
}

// CallTool 调用插件工具。
func (e *Engine) CallTool(ctx context.Context, pluginID, tool string, args json.RawMessage) (json.RawMessage, error) {
	plug := e.Plugins.ActiveGet(pluginID)
	if plug == nil {
		return nil, ErrNotFound
	}
	var def *sdk.ToolDef
	for _, t := range plug.Manifest.Contributes.Tools {
		if t.Name == tool {
			tt := t
			def = &tt
		}
	}
	if def == nil {
		return nil, ErrNotFound
	}
	var ids struct {
		ProjectID string `json:"projectId"`
		ModuleID  string `json:"moduleId"`
	}
	_ = json.Unmarshal(args, &ids)
	params := &sdk.ToolCallParams{Tool: tool, Args: args}
	if def.Scope == "project" || def.Scope == "module" {
		pi, mi := e.projectModule(ids.ProjectID, ids.ModuleID)
		if pi == nil {
			return nil, ErrNotFound
		}
		params.Project, params.Module = pi, mi
	}
	var res json.RawMessage
	err := e.Plugins.Call(ctx, plug, sdk.MethodToolCall, params, &res)
	return res, err
}

// ---------------------------------------------------------------------------
// 系统信息

// SystemInfo 返回系统信息。
func (e *Engine) SystemInfo(pid int, locale, mcpURL string) model.SystemInfo {
	pp := e.Plugins.RunningPIDs()
	region := e.Mirrors().Region
	info := model.SystemInfo{
		Version: e.cfg.Version, OS: runtimeGOOS(), Arch: runtimeGOARCH(), PID: pid, StartedAt: e.startedAt,
		DataDir: e.Layout.Root, APIBase: e.apiBase, MCPURL: mcpURL, Executable: e.cfg.Executable,
		MemoryMB: round1(proc.RSSMB(pid)), PluginProcs: len(pp), PluginMemoryMB: round1(proc.RSSMB(pp...)),
		Locale: locale, MirrorRegion: region,
		Plugins: len(e.Plugins.Active()), PluginDirs: e.cfg.BundledDirs,
	}
	for _, p := range e.Plugins.All() {
		if p.Error != "" {
			info.PluginErrors = append(info.PluginErrors, p.ID()+": "+p.Error)
		}
	}
	return info
}

// runInfo 给插件视图与动作的运行快照。
func (e *Engine) runInfo(runID string) *sdk.RunInfo {
	if runID == "" {
		return nil
	}
	r, ok := e.Run(runID)
	if !ok {
		return nil
	}
	info := &sdk.RunInfo{ID: r.ID, State: r.State, ConfigID: r.ConfigID, URLs: r.URLs, Ports: r.Ports, PID: r.PID}
	if p, ok := e.Store.Project(r.ProjectID); ok {
		if _, c := findConfig(p, r.ConfigID); c != nil {
			rc := c.RunConfiguration
			info.Config = &rc
		}
	}
	return info
}

// PublishProject 推送工程摘要变化（给 ops 层用）。
func (e *Engine) PublishProject(id string) { e.publishProject(id) }

// DefaultKeymap 返回当前平台默认的快捷键方案 id。
func DefaultKeymap() string {
	if runtimeGOOS() == "darwin" {
		return "eclipse-macos"
	}
	return "eclipse-windows"
}
