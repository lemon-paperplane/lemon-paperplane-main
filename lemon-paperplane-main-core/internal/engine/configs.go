package engine

import (
	"context"
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// ---------------------------------------------------------------------------
// 运行配置

// RunConfigTypeChoice 是 runconfig.types 的一项。
type RunConfigTypeChoice struct {
	Type         model.RunConfigTypeInfo `json:"type"`
	OptionValues map[string][]sdk.Option `json:"optionValues,omitempty"`
}

// RunConfigTypes 列出模块可新建的运行配置类型。
func (e *Engine) RunConfigTypes(projectID, moduleID string) ([]RunConfigTypeChoice, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil, ErrNotFound
	}
	m := findModule(p, moduleID)
	if m == nil {
		return nil, ErrNotFound
	}
	opts := map[string][]sdk.Option{}
	for _, a := range m.Analysis {
		for k, v := range model.NonNilOptions(a.OptionValues) {
			opts[k] = v
		}
	}
	var out []RunConfigTypeChoice
	for _, plug := range e.Plugins.Active() {
		in := participates(plug, m)
		for _, t := range plug.Manifest.Contributes.RunConfigurationTypes {
			if !(in || t.AnyModule) || !facetOK(t.Facets, m) {
				continue
			}
			out = append(out, RunConfigTypeChoice{Type: model.RunConfigTypeInfo{RunConfigTypeDef: localizeRCType(plug, t), Plugin: plug.ID()}, OptionValues: opts})
		}
	}
	return out, nil
}

func localizeRCType(p *plugins.Plugin, t sdk.RunConfigTypeDef) sdk.RunConfigTypeDef {
	t.Name = p.T(t.Name)
	t.Icon = p.Icon(t.Icon)
	t.Options = localizeFields(p, t.Options)
	return t
}

func localizeFields(p *plugins.Plugin, fs []sdk.FormField) []sdk.FormField {
	out := make([]sdk.FormField, len(fs))
	for i, f := range fs {
		f.Title = p.T(f.Title)
		f.Info = p.T(f.Info)
		f.Placeholder = p.T(f.Placeholder)
		var os []sdk.Option
		for _, o := range f.Options {
			o.Label = p.T(o.Label)
			os = append(os, o)
		}
		f.Options = os
		out[i] = f
	}
	return out
}

// CreateRunConfig 新建用户运行配置。
func (e *Engine) CreateRunConfig(projectID, moduleID, typ, name string, options map[string]any, env map[string]string) (*model.RunConfig, error) {
	var plugID string
	for _, plug := range e.Plugins.Active() {
		for _, t := range plug.Manifest.Contributes.RunConfigurationTypes {
			if t.ID == typ {
				plugID = plug.ID()
			}
		}
	}
	if plugID == "" {
		return nil, Errf("err.unknownConfigType", typ)
	}
	c := &model.RunConfig{RunConfiguration: sdk.RunConfiguration{ID: store.NewID(), Name: name, Type: typ, Plugin: plugID, ModuleID: moduleID, Options: options, Env: env, Source: "user"}}
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		m := findModule(p, moduleID)
		if m == nil {
			return ErrNotFound
		}
		m.RunConfigs = append(m.RunConfigs, c)
		if p.DefaultConfigID == "" {
			p.DefaultConfigID = c.ID
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	e.publishProject(projectID)
	return c, nil
}

// UpdateRunConfig 修改运行配置（options 合并、env 整体替换），标记为已编辑。
func (e *Engine) UpdateRunConfig(projectID, configID string, name *string, options map[string]any, env map[string]string) (*model.RunConfig, error) {
	var out *model.RunConfig
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		_, c := findConfig(p, configID)
		if c == nil {
			return ErrNotFound
		}
		if name != nil {
			c.Name = *name
		}
		if options != nil {
			if c.Options == nil {
				c.Options = map[string]any{}
			}
			for k, v := range options {
				if v == nil {
					delete(c.Options, k)
				} else {
					c.Options[k] = v
				}
			}
		}
		if env != nil {
			c.Env = env
		}
		c.Edited = true
		cp := *c
		out = &cp
		return nil
	})
	if err != nil {
		return nil, err
	}
	e.publishProject(projectID)
	return out, nil
}

// DeleteRunConfig 删除运行配置。
func (e *Engine) DeleteRunConfig(projectID, configID string) error {
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		for _, m := range p.Modules {
			for i, c := range m.RunConfigs {
				if c.ID == configID {
					m.RunConfigs = append(m.RunConfigs[:i], m.RunConfigs[i+1:]...)
					p.Launch = removeIDs(p.Launch, []string{configID})
					if p.DefaultConfigID == configID {
						p.DefaultConfigID = ""
						e.fixDefaultConfig(p)
					}
					return nil
				}
			}
		}
		return ErrNotFound
	})
	if err == nil {
		e.publishProject(projectID)
	}
	return err
}

// ResetRunConfig 取消「已编辑」，下次分析恢复插件版本（立即重新分析该模块）。
func (e *Engine) ResetRunConfig(ctx context.Context, projectID, configID string) (*model.RunConfig, error) {
	var moduleID string
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		m, c := findConfig(p, configID)
		if c == nil {
			return ErrNotFound
		}
		moduleID = m.ID
		c.Edited = false
		return nil
	})
	if err != nil {
		return nil, err
	}
	p, _ := e.Store.Project(projectID)
	m := findModule(p, moduleID)
	e.analyzeModule(ctx, p, m, readIdeConfigs(p.Path), true)
	updated, err := e.Store.UpdateProject(projectID, func(pp *model.Project) error {
		for i, mm := range pp.Modules {
			if mm.ID == moduleID {
				pp.Modules[i] = m
			}
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	e.publishProject(projectID)
	_, c := findConfig(updated, configID)
	return c, nil
}

// SetDefaultConfig 设置默认运行配置。
func (e *Engine) SetDefaultConfig(projectID, configID string) error {
	_, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		if _, c := findConfig(p, configID); c == nil {
			return ErrNotFound
		}
		p.DefaultConfigID = configID
		p.DefaultByUser = true
		return nil
	})
	if err == nil {
		e.publishProject(projectID)
	}
	return err
}

// ---------------------------------------------------------------------------
// 工程设置

// SetProjectToolchain 设置/清除工程级工具链覆盖并重新选择；需要时自动下载。
func (e *Engine) SetProjectToolchain(ctx context.Context, projectID, kind string, ov *model.ToolchainOverride) (*model.Project, error) {
	p, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
		if p.Settings.Toolchains == nil {
			p.Settings.Toolchains = map[string]model.ToolchainOverride{}
		}
		if ov == nil || (ov.ID == "" && ov.Constraint == "") {
			delete(p.Settings.Toolchains, kind)
		} else {
			p.Settings.Toolchains[kind] = *ov
		}
		for _, m := range p.Modules {
			e.selectToolchains(ctx, p, m)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	// 缺失且可安装：后台下载
	for _, m := range p.Modules {
		if sel, ok := m.Toolchains[kind]; ok && sel.Missing && e.Store.Settings().AutoInstallToolchains {
			if plug := e.toolchainPlugin(kind); plug != nil {
				_, _ = e.InstallToolchain(kind, constraintForInstall(sel))
			}
			break
		}
	}
	e.publishProject(projectID)
	return p, nil
}

// UpdateProjectSettings 修改工程环境变量与工具链覆盖。
func (e *Engine) UpdateProjectSettings(ctx context.Context, projectID string, env map[string]string, toolchains map[string]*model.ToolchainOverride) (*model.Project, error) {
	if env != nil {
		if _, err := e.Store.UpdateProject(projectID, func(p *model.Project) error {
			p.Settings.Env = env
			return nil
		}); err != nil {
			return nil, err
		}
	}
	for kind, ov := range toolchains {
		if _, err := e.SetProjectToolchain(ctx, projectID, kind, ov); err != nil {
			return nil, err
		}
	}
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil, ErrNotFound
	}
	e.publishProject(projectID)
	return p, nil
}

// ---------------------------------------------------------------------------
// 依赖安装

// InstallDeps 创建依赖安装任务（moduleID 为空表示全部模块）。
func (e *Engine) InstallDeps(projectID, moduleID string, clean bool) (model.Task, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return model.Task{}, ErrNotFound
	}
	return e.NewTask("install", i18n.T("task.installDeps", p.Name), projectID, moduleID, func(ctx context.Context, ts *taskState) (any, error) {
		var lastErr error
		for _, m := range p.Modules {
			if moduleID != "" && m.ID != moduleID {
				continue
			}
			if err := e.installDeps(ctx, p, m, ts.t.ID, clean); err != nil {
				lastErr = err
				e.diagnoseFailure(ctx, p, m, nil, "install", ts.t.ID, nil)
			}
		}
		return nil, lastErr
	}), nil
}

// installDeps 同步安装一个模块的依赖（输出进 consoleID）。
func (e *Engine) installDeps(ctx context.Context, p *model.Project, m *model.Module, consoleID string, clean bool) error {
	var installers []*plugins.Plugin
	for _, plug := range e.Plugins.Active() {
		if len(plug.Manifest.Contributes.DependencyManagers) > 0 && participates(plug, m) {
			installers = append(installers, plug)
		}
	}
	if len(installers) == 0 {
		return nil
	}
	tcs, err := e.ensureToolchains(ctx, p, m, consoleID)
	if err != nil {
		return err
	}
	settings := e.Store.Settings()
	var state *sdk.DependencyState
	for _, plug := range installers {
		var r sdk.InstallDepsResult
		err := e.Plugins.Call(ctx, plug, sdk.MethodInstallDeps, &sdk.InstallDepsParams{
			Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Toolchains: tcs, Env: p.Settings.Env,
			Settings: e.pluginSettings(plug, settings), TaskID: consoleID, Clean: clean,
		}, &r)
		if err != nil {
			if sdk.IsCode(err, sdk.ErrCodeNotSupported) {
				continue
			}
			return err
		}
		d := r.Dependencies
		state = worseDeps(state, &d)
		mid := m.ID
		pid := plug.ID()
		_, _ = e.Store.UpdateProject(p.ID, func(pp *model.Project) error {
			if mm := findModule(pp, mid); mm != nil {
				if a := mm.Analysis[pid]; a != nil {
					a.Dependencies = &d
				}
				mm.Dependencies = worseDeps(nil, &d)
				for _, a := range mm.Analysis {
					if a.Dependencies != nil && a != mm.Analysis[pid] {
						mm.Dependencies = worseDeps(mm.Dependencies, a.Dependencies)
					}
				}
			}
			return nil
		})
	}
	if state != nil {
		m.Dependencies = state
	}
	e.publishProject(p.ID)
	return nil
}

// ---------------------------------------------------------------------------
// 动作

// InvokeAction 执行插件动作（任务）。
func (e *Engine) InvokeAction(projectID, moduleID, pluginID, action string, args map[string]any, runID string) (model.Task, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return model.Task{}, ErrNotFound
	}
	if moduleID == "" && len(p.Modules) > 0 {
		moduleID = p.Modules[0].ID
	}
	m := findModule(p, moduleID)
	plug := e.Plugins.ActiveGet(pluginID)
	if plug == nil {
		return model.Task{}, ErrNotFound
	}
	if m == nil {
		// 纯仓库工程（没有识别出模块）：以工程根作为一个空模块，供 VCS 等插件动作使用
		m = &model.Module{ID: "", Name: p.Name, RelDir: "", Models: map[string]json.RawMessage{}}
	}
	title := action
	for _, a := range plug.Manifest.Contributes.Actions {
		if a.ID == action {
			title = plug.T(a.Title)
		}
	}
	return e.NewTask("action", title, projectID, moduleID, func(ctx context.Context, ts *taskState) (any, error) {
		tcs, err := e.ensureToolchains(ctx, p, m, ts.t.ID)
		if err != nil {
			return nil, err
		}
		var r sdk.InvokeActionResult
		if err := e.Plugins.Call(ctx, plug, sdk.MethodInvokeAction, &sdk.InvokeActionParams{Action: action, Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Toolchains: tcs, Env: p.Settings.Env, TaskID: ts.t.ID, Args: args, Run: e.runInfo(runID)}, &r); err != nil {
			return nil, err
		}
		if r.Message != "" {
			ts.console.System(r.Message)
		}
		if r.Sync {
			if err := e.syncModules(ctx, projectID, moduleID, ts.t.ID); err != nil {
				return r, err
			}
		} else if r.Reimport {
			_, _ = e.Reimport(projectID)
		}
		return r, nil
	}), nil
}

// ---------------------------------------------------------------------------
// 打开方式

// OpenProject 在 Finder/终端/IDE 中打开。
func (e *Engine) OpenProject(projectID, moduleID, with string) error {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return ErrNotFound
	}
	dir := p.Path
	if m := findModule(p, moduleID); m != nil {
		dir = filepath.Join(p.Path, filepath.FromSlash(m.RelDir))
	}
	var cmd *exec.Cmd
	switch runtime.GOOS {
	case "darwin":
		switch with {
		case "terminal":
			app := e.Store.Settings().Terminal
			if app == "" {
				app = "Terminal"
			}
			cmd = exec.Command("open", "-a", app, dir)
		case "idea":
			cmd = exec.Command("open", "-a", firstInstalled([]string{"IntelliJ IDEA", "IntelliJ IDEA Ultimate", "IntelliJ IDEA CE", "IntelliJ IDEA Community Edition"}), dir)
		case "vscode":
			cmd = exec.Command("open", "-a", firstInstalled([]string{"Visual Studio Code", "Cursor", "VSCodium"}), dir)
		default:
			cmd = exec.Command("open", dir)
		}
	case "windows":
		switch with {
		case "terminal":
			cmd = exec.Command("cmd", "/c", "start", "wt", "-d", dir)
		case "vscode":
			cmd = exec.Command("cmd", "/c", "code", dir)
		case "idea":
			cmd = exec.Command("cmd", "/c", "idea64", dir)
		default:
			cmd = exec.Command("explorer", dir)
		}
	default:
		switch with {
		case "vscode":
			cmd = exec.Command("code", dir)
		case "idea":
			cmd = exec.Command("idea", dir)
		case "terminal":
			cmd = exec.Command("x-terminal-emulator", "--working-directory", dir)
		default:
			cmd = exec.Command("xdg-open", dir)
		}
	}
	return cmd.Start()
}

func firstInstalled(apps []string) string {
	home, _ := os.UserHomeDir()
	for _, a := range apps {
		for _, root := range []string{"/Applications", filepath.Join(home, "Applications"), filepath.Join(home, "Applications", "JetBrains Toolbox")} {
			if _, err := os.Stat(filepath.Join(root, a+".app")); err == nil {
				return a
			}
		}
	}
	return apps[0]
}

var _ = time.Now

// LiveOptions 实时取运行配置/动作表单里 live 的下拉选项（如已连接设备）。只调用声明了这些键的插件，
// 避免把模块上其他插件的进程也拉起来；某个插件失败不影响其他插件。
func (e *Engine) LiveOptions(ctx context.Context, projectID, moduleID string, keys []string) (map[string][]sdk.Option, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil, ErrNotFound
	}
	m := findModule(p, moduleID)
	if m == nil && len(p.Modules) > 0 {
		m = p.Modules[0]
	}
	if m == nil {
		return map[string][]sdk.Option{}, nil
	}
	want := map[string]bool{}
	for _, k := range keys {
		want[k] = true
	}
	out := map[string][]sdk.Option{}
	for _, plug := range e.Plugins.Active() {
		if !participates(plug, m) {
			continue
		}
		mine := liveKeys(plug.Manifest, want)
		if len(mine) == 0 {
			continue
		}
		cctx, cancel := context.WithTimeout(ctx, 15*time.Second)
		var res sdk.OptionsResult
		err := e.Plugins.Call(cctx, plug, sdk.MethodOptions, &sdk.OptionsParams{Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Keys: mine, Env: p.Settings.Env}, &res)
		cancel()
		if err != nil {
			if !sdk.IsCode(err, sdk.ErrCodeNotSupported) {
				e.logf("live options %s: %v", plug.ID(), err)
			}
			continue
		}
		for k, v := range model.NonNilOptions(res.Values) {
			out[k] = v
		}
	}
	return out, nil
}

// liveKeys 返回插件声明为 live 且在 want 里的 optionsFrom 键。
func liveKeys(man *sdk.Manifest, want map[string]bool) []string {
	seen := map[string]bool{}
	var out []string
	add := func(fs []sdk.FormField) {
		for _, f := range fs {
			if f.Live && f.OptionsFrom != "" && want[f.OptionsFrom] && !seen[f.OptionsFrom] {
				seen[f.OptionsFrom] = true
				out = append(out, f.OptionsFrom)
			}
		}
	}
	for _, t := range man.Contributes.RunConfigurationTypes {
		add(t.Options)
	}
	for _, a := range man.Contributes.Actions {
		add(a.Form)
	}
	return out
}
