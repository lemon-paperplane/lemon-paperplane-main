package engine

import (
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
)

// 「启动」面板：用户从各工程挑出要一起跑的运行配置，工作区里按工程分组显示，可以一键全部运行/停止。
// 存在工程上（Project.Launch），工程换工作区时跟着走；运行配置被删掉时自动移出。

// LaunchItem 是启动面板的一项。
type LaunchItem struct {
	ConfigID   string          `json:"configId"`
	Name       string          `json:"name"`
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

// LaunchRemove 把运行配置移出启动面板。
func (e *Engine) LaunchRemove(projectID string, configIDs []string) ([]string, error) {
	p, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		p.Launch = removeIDs(launchIDs(p), configIDs)
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
			it := LaunchItem{ConfigID: id, Name: c.Name, Type: c.Type, Plugin: c.Plugin, ModuleID: m.ID, ModuleName: m.Name}
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
