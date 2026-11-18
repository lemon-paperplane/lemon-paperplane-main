package engine

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// 「启动」面板：用户从各工程挑出要一起跑的运行配置，工作区里按工程分组显示，可以一键全部运行/停止。
// 存在工程上（Project.Launch），工程换工作区时跟着走；运行配置被删掉时自动移出。

// LaunchItem 是启动面板的一项。
type LaunchItem struct {
	ConfigID   string          `json:"configId"`
	Name       string          `json:"name"`
	Alias      string          `json:"alias,omitempty"` // 用户起的别名（显示时优先）
	Type       string          `json:"type"`
	Plugin     string          `json:"plugin,omitempty"`
	ModuleID   string          `json:"moduleId"`
	ModuleName string          `json:"moduleName,omitempty"`
	Run        *model.RunBrief `json:"run,omitempty"` // 最近一次运行（进行中优先）
}

// LaunchGroup 是一个工程的启动项。
type LaunchGroup struct {
	ProjectID   string       `json:"projectId"`
	ProjectName string       `json:"projectName"`
	Path        string       `json:"path"`
	Items       []LaunchItem `json:"items"`
}

// launchIDs 返回工程里仍然存在的启动项（按加入顺序）。
func launchIDs(p *model.Project) []string {
	var out []string
	for _, id := range p.Launch {
		if _, c := findConfig(p, id); c != nil {
			out = append(out, id)
		}
	}
	return out
}

func removeIDs(list, ids []string) []string {
	drop := map[string]bool{}
	for _, id := range ids {
		drop[id] = true
	}
	var out []string
	for _, id := range list {
		if !drop[id] {
			out = append(out, id)
		}
	}
	return out
}

// LaunchAdd 把运行配置加到启动面板（已在里面的忽略，保持原顺序）。
func (e *Engine) LaunchAdd(projectID string, configIDs []string) ([]string, error) {
	p, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		for _, id := range configIDs {
			if _, c := findConfig(p, id); c == nil {
				return ErrNotFound
			}
		}
		cur := launchIDs(p)
		have := map[string]bool{}
		for _, id := range cur {
			have[id] = true
		}
		for _, id := range configIDs {
			if !have[id] {
				cur = append(cur, id)
				have[id] = true
			}
		}
		p.Launch = cur
		return nil
	})
	if err != nil {
		return nil, err
	}
	e.publishProject(projectID)
	return p.Launch, nil
}

// LaunchRemove 把运行配置移出启动面板（别名一起去掉）。
func (e *Engine) LaunchRemove(projectID string, configIDs []string) ([]string, error) {
	p, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		p.Launch = removeIDs(launchIDs(p), configIDs)
		for _, id := range configIDs {
			delete(p.LaunchAlias, id)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	e.publishProject(projectID)
	return p.Launch, nil
}

// LaunchList 返回启动面板（workspaceID 为空时是全部工作区；projectID 可只看一个工程）。
func (e *Engine) LaunchList(workspaceID, projectID string) []LaunchGroup {
	latest := map[string]model.Run{}      // configId → 最近一次运行（进行中优先）
	for _, r := range e.Runs("", false) { // 新的在前
		if cur, ok := latest[r.ConfigID]; !ok || (!active(cur.State) && active(r.State)) {
			latest[r.ConfigID] = r
		}
	}
	out := []LaunchGroup{}
	for _, p := range e.Store.Projects(workspaceID) {
		if projectID != "" && p.ID != projectID {
			continue
		}
		ids := launchIDs(p)
		if len(ids) == 0 {
			continue
		}
		g := LaunchGroup{ProjectID: p.ID, ProjectName: p.Name, Path: p.Path, Items: []LaunchItem{}}
		for _, id := range ids {
			m, c := findConfig(p, id)
			it := LaunchItem{ConfigID: id, Name: c.Name, Alias: p.LaunchAlias[id], Type: c.Type, Plugin: c.Plugin, ModuleID: m.ID, ModuleName: m.Name}
			if r, ok := latest[id]; ok && r.ProjectID == p.ID {
				b := model.RunBrief{ID: r.ID, ProjectID: r.ProjectID, ConfigID: r.ConfigID, Name: r.Name, State: r.State, URLs: r.URLs, CPU: r.CPU, RSSMB: r.RSSMB}
				it.Run = &b
			}
			g.Items = append(g.Items, it)
		}
		out = append(out, g)
	}
	return out
}

// LaunchResult 是 launch.run / launch.stop 的结果。
type LaunchResult struct {
	Started []model.Run      `json:"started,omitempty"`
	Stopped int              `json:"stopped,omitempty"`
	Skipped int              `json:"skipped,omitempty"` // 已经在运行（run）或本来就没运行（stop）
	Errors  []LaunchRunError `json:"errors,omitempty"`
}

// LaunchRunError 是某一项没能启动的原因。
type LaunchRunError struct {
	ProjectID string `json:"projectId"`
	ConfigID  string `json:"configId"`
	Error     string `json:"error"`
}

// LaunchRun 运行启动面板里还没在运行的项（可限定工作区/工程/单项）。
func (e *Engine) LaunchRun(workspaceID, projectID string, configIDs []string) LaunchResult {
	only := map[string]bool{}
	for _, id := range configIDs {
		only[id] = true
	}
	activeCfg := map[string]bool{}
	for _, r := range e.Runs("", true) {
		activeCfg[r.ProjectID+"/"+r.ConfigID] = true
	}
	res := LaunchResult{}
	for _, g := range e.LaunchList(workspaceID, projectID) {
		for _, it := range g.Items {
			if len(only) > 0 && !only[it.ConfigID] {
				continue
			}
			if activeCfg[g.ProjectID+"/"+it.ConfigID] {
				res.Skipped++
				continue
			}
			r, err := e.StartRun(g.ProjectID, it.ConfigID)
			if err != nil {
				res.Errors = append(res.Errors, LaunchRunError{ProjectID: g.ProjectID, ConfigID: it.ConfigID, Error: err.Error()})
				continue
			}
			res.Started = append(res.Started, r)
		}
	}
	return res
}

// LaunchStop 停止启动面板里正在运行的项（可限定工作区/工程/单项）。
func (e *Engine) LaunchStop(workspaceID, projectID string, configIDs []string) LaunchResult {
	only := map[string]bool{}
	for _, id := range configIDs {
		only[id] = true
	}
	inLaunch := map[string]bool{}
	for _, g := range e.LaunchList(workspaceID, projectID) {
		for _, it := range g.Items {
			if len(only) == 0 || only[it.ConfigID] {
				inLaunch[g.ProjectID+"/"+it.ConfigID] = true
			}
		}
	}
	res := LaunchResult{}
	for _, r := range e.Runs("", true) {
		if !inLaunch[r.ProjectID+"/"+r.ConfigID] {
			continue
		}
		if _, err := e.StopRun(r.ID); err != nil {
			res.Errors = append(res.Errors, LaunchRunError{ProjectID: r.ProjectID, ConfigID: r.ConfigID, Error: err.Error()})
			continue
		}
		res.Stopped++
	}
	return res
}

// LaunchAddResult 是 launch.addFile / launch.addCommand 的结果。
type LaunchAddResult struct {
	ConfigID string   `json:"configId"`
	ModuleID string   `json:"moduleId"`
	Name     string   `json:"name"`
	Created  bool     `json:"created"` // false 表示复用了已有的运行配置
	Launch   []string `json:"launch"`
}

// anyModuleTypePlugin 返回提供某个「任何模块都能建」运行配置类型的插件 id（如 shell 插件的 script / command）。
func (e *Engine) anyModuleTypePlugin(typ string) string {
	for _, plug := range e.Plugins.Active() {
		for _, t := range plug.Manifest.Contributes.RunConfigurationTypes {
			if t.ID == typ && t.AnyModule {
				return plug.ID()
			}
		}
	}
	return ""
}

// moduleFor 找包含 rel（相对工程根的路径）的最深模块；没有就用（必要时新建）工程根目录的普通模块。
func moduleFor(p *model.Project, rel string) *model.Module {
	var best *model.Module
	for _, m := range p.Modules {
		if m.RelDir == "" || rel == m.RelDir || strings.HasPrefix(rel, m.RelDir+"/") {
			if best == nil || len(m.RelDir) > len(best.RelDir) {
				best = m
			}
		}
	}
	if best != nil {
		return best
	}
	m := &model.Module{ID: sdk.StableID(p.ID, ""), Name: p.Name, RelDir: "", Models: map[string]json.RawMessage{}, Analysis: map[string]*model.ModuleAnalysis{}}
	p.Modules = append(p.Modules, m)
	return m
}

// addToLaunch 把配置加到工程的启动项（已在里面就不动）。
func addToLaunch(p *model.Project, id string) {
	ids := launchIDs(p)
	for _, x := range ids {
		if x == id {
			p.Launch = ids
			return
		}
	}
	p.Launch = append(ids, id)
}

// LaunchAddFile 把工程里的脚本或可执行文件加到「启动」：建一个「脚本 / 可执行文件」运行配置（同一文件已有就复用）。
func (e *Engine) LaunchAddFile(projectID, path string) (*LaunchAddResult, error) {
	root, abs, err := e.projectPath(projectID, path)
	if err != nil {
		return nil, err
	}
	fi, err := os.Stat(abs)
	if err != nil || fi.IsDir() {
		return nil, Errf("err.notAFile", path)
	}
	if runnableKind(abs, fi) == "" {
		return nil, Errf("err.notRunnable", filepath.Base(abs))
	}
	rel := relOf(root, abs)
	plug := e.anyModuleTypePlugin("script")
	if plug == "" {
		return nil, Errf("err.unknownConfigType", "script")
	}
	res := &LaunchAddResult{}
	_, err = e.Store.UpdateProject(projectID, func(p *model.Project) error {
		for _, m := range p.Modules {
			for _, c := range m.RunConfigs {
				if c.Plugin == plug && c.Type == "script" && sdk.OptString(c.Options, "file", "") == rel {
					res.ConfigID, res.ModuleID, res.Name = c.ID, m.ID, c.Name
				}
			}
		}
		if res.ConfigID == "" {
			m := moduleFor(p, rel)
			c := &model.RunConfig{RunConfiguration: sdk.RunConfiguration{ID: store.NewID(), Name: filepath.Base(abs), Type: "script", Plugin: plug,
				ModuleID: m.ID, Options: map[string]any{"file": rel, "interpreter": "auto"}, Source: "user"}}
			m.RunConfigs = append(m.RunConfigs, c)
			res.ConfigID, res.ModuleID, res.Name, res.Created = c.ID, m.ID, c.Name, true
			if p.DefaultConfigID == "" {
				p.DefaultConfigID = c.ID
			}
		}
		addToLaunch(p, res.ConfigID)
		res.Launch = p.Launch
		return nil
	})
	if err != nil {
		return nil, err
	}
	e.publishProject(projectID)
	return res, nil
}

// LaunchAddCommand 把一段自定义命令加到「启动」：建一个「自定义命令」运行配置。
// cwd 为空表示工程根目录；工程内的目录存相对路径，工程外的存绝对路径。
func (e *Engine) LaunchAddCommand(projectID, name, command, cwd, shell string) (*LaunchAddResult, error) {
	p0, ok := e.Store.Project(projectID)
	if !ok {
		return nil, ErrNotFound
	}
	name, command = strings.TrimSpace(name), strings.TrimSpace(command)
	if command == "" {
		return nil, Errf("err.emptyCommand")
	}
	relCwd := ""
	if c := strings.TrimSpace(cwd); c != "" {
		abs := c
		if !filepath.IsAbs(abs) {
			abs = filepath.Join(p0.Path, filepath.FromSlash(c))
		}
		if !sdk.DirExists(abs) {
			return nil, Errf("err.dirMissing", c)
		}
		relCwd = abs
		if r, err := filepath.Rel(p0.Path, abs); err == nil && !strings.HasPrefix(r, "..") {
			relCwd = filepath.ToSlash(r)
			if relCwd == "." {
				relCwd = ""
			}
		}
	}
	if name == "" {
		name = strings.SplitN(command, "\n", 2)[0]
		if len([]rune(name)) > 40 {
			name = string([]rune(name)[:40]) + "…"
		}
	}
	if shell == "" {
		shell = "auto"
	}
	plug := e.anyModuleTypePlugin("command")
	if plug == "" {
		return nil, Errf("err.unknownConfigType", "command")
	}
	res := &LaunchAddResult{Name: name, Created: true}
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		m := moduleFor(p, relCwd)
		c := &model.RunConfig{RunConfiguration: sdk.RunConfiguration{ID: store.NewID(), Name: name, Type: "command", Plugin: plug, ModuleID: m.ID,
			Options: map[string]any{"command": command, "cwd": relCwd, "shell": shell}, Source: "user"}}
		m.RunConfigs = append(m.RunConfigs, c)
		res.ConfigID, res.ModuleID = c.ID, m.ID
		if p.DefaultConfigID == "" {
			p.DefaultConfigID = c.ID
		}
		addToLaunch(p, c.ID)
		res.Launch = p.Launch
		return nil
	})
	if err != nil {
		return nil, err
	}
	e.publishProject(projectID)
	return res, nil
}

// launchAliases 返回仍在启动里的项的别名。
func launchAliases(p *model.Project) map[string]string {
	if len(p.LaunchAlias) == 0 {
		return nil
	}
	out := map[string]string{}
	for _, id := range launchIDs(p) {
		if a := p.LaunchAlias[id]; a != "" {
			out[id] = a
		}
	}
	return out
}

// LaunchSetAlias 设置（alias 为空时清除）启动项的别名。
func (e *Engine) LaunchSetAlias(projectID, configID, alias string) error {
	alias = strings.TrimSpace(alias)
	if len([]rune(alias)) > 80 {
		alias = string([]rune(alias)[:80])
	}
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		if _, c := findConfig(p, configID); c == nil {
			return ErrNotFound
		}
		if alias == "" {
			delete(p.LaunchAlias, configID)
			return nil
		}
		if p.LaunchAlias == nil {
			p.LaunchAlias = map[string]string{}
		}
		p.LaunchAlias[configID] = alias
		return nil
	})
	if err == nil {
		e.publishProject(projectID)
	}
	return err
}
