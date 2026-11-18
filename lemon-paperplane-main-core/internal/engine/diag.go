package engine

import (
	"context"
	"encoding/json"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

type compiledRule struct {
	rule   sdk.DiagnosticRule
	hint   string
	re     *regexp.Regexp
	plugin *plugins.Plugin
}

type compiledURL struct {
	hint   string
	re     *regexp.Regexp
	url    string
	ready  bool
	facets []string
}

type compiledReady struct {
	hint   string
	re     *regexp.Regexp
	facets []string
}

type compiledRules struct {
	diags []*compiledRule
	urls  []*compiledURL
	ready []*compiledReady
}

// rulesOf 编译并缓存插件的声明式规则。
func (e *Engine) rulesOf(p *plugins.Plugin) *compiledRules {
	key := p.ID() + "@" + p.Manifest.Version + "@" + p.Dir
	e.rulesMu.Lock()
	defer e.rulesMu.Unlock()
	if r := e.rules[key]; r != nil {
		return r
	}
	r := &compiledRules{}
	for _, d := range p.Manifest.Contributes.Diagnostics {
		re, err := regexp.Compile(d.Pattern)
		if err != nil {
			e.logf("plugin %s rule %s: %v", p.ID(), d.ID, err)
			continue
		}
		r.diags = append(r.diags, &compiledRule{rule: d, hint: d.Hint, re: re, plugin: p})
	}
	for _, u := range p.Manifest.Contributes.URLPatterns {
		if re, err := regexp.Compile(u.Pattern); err == nil {
			r.urls = append(r.urls, &compiledURL{hint: u.Hint, re: re, url: u.URL, ready: u.Ready, facets: u.Facets})
		}
	}
	for _, rp := range p.Manifest.Contributes.ReadyPatterns {
		if re, err := regexp.Compile(rp.Pattern); err == nil {
			r.ready = append(r.ready, &compiledReady{hint: rp.Hint, re: re, facets: rp.Facets})
		}
	}
	e.rules[key] = r
	return r
}

// runMatchers 是某次运行/任务生效的规则集合。
type runMatchers struct {
	diags []*diagMatcher
	urls  []*compiledURL
	ready []*compiledReady
}

type diagMatcher struct {
	*compiledRule
	core  bool // 内核通用规则：同一行已被插件规则命中时跳过
	build func(sub []string, line string) sdk.Diagnosis
}

func os_getenv(k string) string { return os.Getenv(k) }

func (m *runMatchers) hasReady() bool {
	if len(m.ready) > 0 {
		return true
	}
	for _, u := range m.urls {
		if u.ready {
			return true
		}
	}
	return false
}

func (m *runMatchers) hasURL() bool { return len(m.urls) > 0 }

func (m *runMatchers) addDynamic(urls []sdk.URLPattern, ready []string) {
	for _, u := range urls {
		if re, err := regexp.Compile(u.Pattern); err == nil {
			m.urls = append(m.urls, &compiledURL{hint: u.Hint, re: re, url: u.URL, ready: u.Ready})
		}
	}
	for _, r := range ready {
		if re, err := regexp.Compile(r); err == nil {
			m.ready = append(m.ready, &compiledReady{re: re})
		}
	}
}

func facetOK(need []string, m *model.Module) bool {
	if len(need) == 0 {
		return true
	}
	for _, n := range need {
		for _, f := range m.Facets {
			if f.ID == n {
				return true
			}
		}
	}
	return false
}

func stageOK(stages []string, stage string) bool {
	if len(stages) == 0 {
		return true
	}
	for _, s := range stages {
		if s == stage {
			return true
		}
	}
	return false
}

func (e *Engine) runMatchers(p *model.Project, m *model.Module, stage string) *runMatchers {
	out := &runMatchers{}
	for _, plug := range e.Plugins.Active() {
		if !participates(plug, m) {
			continue
		}
		cr := e.rulesOf(plug)
		for _, d := range cr.diags {
			if !facetOK(d.rule.Facets, m) || !stageOK(d.rule.Stages, stage) {
				continue
			}
			rule := d
			out.diags = append(out.diags, &diagMatcher{compiledRule: rule, build: func(sub []string, line string) sdk.Diagnosis {
				return buildDiagnosis(rule, sub, line)
			}})
		}
		for _, u := range cr.urls {
			if facetOK(u.facets, m) {
				out.urls = append(out.urls, u)
			}
		}
		for _, r := range cr.ready {
			if facetOK(r.facets, m) {
				out.ready = append(out.ready, r)
			}
		}
	}
	// 内核通用规则
	out.diags = append(out.diags, coreRules()...)
	return out
}

func buildDiagnosis(r *compiledRule, sub []string, line string) sdk.Diagnosis {
	p := r.plugin
	d := sdk.Diagnosis{
		ID: store.NewID(), RuleID: r.rule.ID, Plugin: p.ID(), Severity: r.rule.Severity,
		Title: fill(p.T(r.rule.Title), sub), Message: fill(p.T(r.rule.Message), sub), Line: truncate(line, 500),
	}
	for _, f := range r.rule.Fixes {
		nf := f
		nf.Title = fill(p.T(f.Title), sub)
		if f.Confirm != "" {
			nf.Confirm = fill(p.T(f.Confirm), sub)
		}
		nf.Plugin = p.ID()
		nf.Params = map[string]string{}
		for k, v := range f.Params {
			nf.Params[k] = fill(v, sub)
		}
		d.Fixes = append(d.Fixes, nf)
	}
	return d
}

func truncate(s string, n int) string {
	if len(s) > n {
		return s[:n] + "…"
	}
	return s
}

var (
	corePortRe1 = regexp.MustCompile(`EADDRINUSE[^0-9]*(?:[:\]]\s*)?(\d{2,5})?`)
	corePortRe2 = regexp.MustCompile(`(?i)(?:port\s+(\d{2,5})\s+(?:is\s+|was\s+)?already in use|already in use[^0-9]{0,40}?(\d{2,5})?)`)
)

// coreRules 是内核内置的少量通用规则（与语言无关）。
func coreRules() []*diagMatcher {
	var out []*diagMatcher
	for _, pr := range []struct {
		hint string
		re   *regexp.Regexp
	}{{"EADDRINUSE", corePortRe1}, {"already in use", corePortRe2}} {
		out = append(out, portMatcher(pr.hint, pr.re))
	}
	return out
}

func portMatcher(hint string, re *regexp.Regexp) *diagMatcher {
	portRule := &compiledRule{rule: sdk.DiagnosticRule{ID: "core.port-in-use", Severity: "error"}, hint: hint, re: re}
	return &diagMatcher{compiledRule: portRule, core: true, build: func(sub []string, line string) sdk.Diagnosis {
		port := ""
		for i := len(sub) - 1; i >= 1; i-- {
			if sub[i] != "" {
				port = sub[i]
				break
			}
		}
		d := sdk.Diagnosis{ID: store.NewID(), RuleID: "core.port-in-use", Severity: "error", Title: i18n.T("diag.portInUse.title"), Message: i18n.T("diag.portInUse.message"), Line: truncate(line, 500)}
		if port != "" {
			d.Title = i18n.T("diag.portBusy.title", port)
			d.Fixes = []sdk.Fix{{ID: "kill", Title: i18n.T("fix.killPort", port), Kind: sdk.FixKillPort, Params: map[string]string{"port": port}, Recommended: true, Confirm: i18n.T("fix.killPort.confirm", port)}}
		}
		return d
	}}
}

// ---------------------------------------------------------------------------
// 修复

// FixResult 是 fix.apply 的返回。
type FixResult struct {
	Message string      `json:"message,omitempty"`
	OpenURL string      `json:"openUrl,omitempty"`
	Rerun   *model.Run  `json:"rerun,omitempty"`
	Task    *model.Task `json:"task,omitempty"`
}

// ApplyFix 执行修复。runID 非空时修复来自该运行的诊断，完成后按需重跑。
func (e *Engine) ApplyFix(ctx context.Context, runID, projectID, diagnosisID, fixID string) (*FixResult, error) {
	var diag *sdk.Diagnosis
	var configID, moduleID string
	taskDiag := false
	if runID != "" && e.runByID(runID) == nil {
		// 不是运行：可能是安装/同步任务产生的诊断
		e.mu.Lock()
		ts := e.tasks[runID]
		e.mu.Unlock()
		if ts == nil {
			return nil, ErrNotFound
		}
		t := ts.snapshot()
		projectID, moduleID = t.ProjectID, t.ModuleID
		for i := range t.Diagnoses {
			if t.Diagnoses[i].ID == diagnosisID || (diagnosisID == "" && hasFix(t.Diagnoses[i], fixID)) {
				d := t.Diagnoses[i]
				diag = &d
				break
			}
		}
		runID = ""
		taskDiag = true
	}
	if runID != "" {
		rs := e.runByID(runID)
		if rs == nil {
			return nil, ErrNotFound
		}
		r := rs.snapshot()
		projectID, configID, moduleID = r.ProjectID, r.ConfigID, r.ModuleID
		for i := range r.Diagnoses {
			if r.Diagnoses[i].ID == diagnosisID || (diagnosisID == "" && hasFix(r.Diagnoses[i], fixID)) {
				d := r.Diagnoses[i]
				diag = &d
				break
			}
		}
	} else if !taskDiag {
		for _, d := range e.ProjectProblems(projectID) {
			if d.ID == diagnosisID {
				dd := d
				diag = &dd
				moduleID = d.Line // ProjectProblems 把模块 id 暂放在 Line 里
				break
			}
		}
	}
	if diag == nil {
		return nil, ErrNotFound
	}
	var fix *sdk.Fix
	for i := range diag.Fixes {
		if diag.Fixes[i].ID == fixID {
			fix = &diag.Fixes[i]
		}
	}
	if fix == nil {
		return nil, ErrNotFound
	}
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil, ErrNotFound
	}
	if moduleID == "" && len(p.Modules) > 0 {
		moduleID = p.Modules[0].ID
	}
	res := &FixResult{}
	rerun := false
	switch fix.Kind {
	case sdk.FixSetToolchain:
		kind, cons := fix.Params["kind"], fix.Params["constraint"]
		if _, err := e.SetProjectToolchain(ctx, projectID, kind, &model.ToolchainOverride{Constraint: cons}); err != nil {
			return nil, err
		}
		res.Message = i18n.T("fix.done.toolchain", kind, cons)
		rerun = true
	case sdk.FixSetEnv, sdk.FixAppendEnv, sdk.FixUnsetEnv:
		key, val := fix.Params["key"], fix.Params["value"]
		scope := fix.Params["scope"]
		if scope == "" {
			if configID != "" {
				scope = "config"
			} else {
				scope = "project"
			}
		}
		_, err := e.Store.UpdateProject(projectID, func(pp *model.Project) error {
			var env map[string]string
			if scope == "config" {
				_, c := findConfig(pp, configID)
				if c == nil {
					return ErrNotFound
				}
				if c.Env == nil {
					c.Env = map[string]string{}
				}
				c.Edited = true
				env = c.Env
			} else {
				if pp.Settings.Env == nil {
					pp.Settings.Env = map[string]string{}
				}
				env = pp.Settings.Env
			}
			switch fix.Kind {
			case sdk.FixSetEnv:
				env[key] = val
			case sdk.FixAppendEnv:
				cur := env[key]
				if cur == "" {
					cur = os_getenv(key)
				}
				if !strings.Contains(cur, val) {
					env[key] = strings.TrimSpace(cur + " " + val)
				}
			case sdk.FixUnsetEnv:
				delete(env, key)
			}
			return nil
		})
		if err != nil {
			return nil, err
		}
		res.Message = i18n.T("fix.done.env", key)
		rerun = true
	case sdk.FixSetRunOption:
		if configID == "" {
			configID = p.DefaultConfigID
		}
		var v any = fix.Params["value"]
		// 只有选项声明为数字/布尔等非文本类型时才把值按 JSON 解析；文本和下拉（如设备序列号，可能是纯数字的大数）原样保留
		if !e.optionIsText(p, configID, fix.Params["key"]) {
			var parsed any
			if json.Unmarshal([]byte(fix.Params["value"]), &parsed) == nil {
				if _, isStr := parsed.(string); !isStr {
					v = parsed
				}
			}
		}
		if _, err := e.UpdateRunConfig(projectID, configID, nil, map[string]any{fix.Params["key"]: v}, nil); err != nil {
			return nil, err
		}
		res.Message = i18n.T("fix.done.option", fix.Params["key"])
		rerun = true
	case sdk.FixInstallDependencies:
		t, err := e.InstallDeps(projectID, moduleID, fix.Params["clean"] == "true")
		if err != nil {
			return nil, err
		}
		res.Task = &t
		if runID != "" {
			go func() {
				if done, err := e.WaitTask(context.Background(), t.ID); err == nil && done.State == model.TaskSucceeded {
					_, _ = e.RestartRun(runID, "", "")
				}
			}()
		}
		return res, nil
	case sdk.FixKillPort:
		port, _ := strconv.Atoi(fix.Params["port"])
		pids := e.KillPort(port)
		res.Message = i18n.T("fix.done.killPort", port, len(pids))
		rerun = true
		time.Sleep(500 * time.Millisecond)
	case sdk.FixReimport:
		t, err := e.Reimport(projectID)
		if err != nil {
			return nil, err
		}
		res.Task = &t
		return res, nil
	case sdk.FixRestart:
		rerun = true
	case sdk.FixOpenURL:
		res.OpenURL = fix.Params["url"]
		return res, nil
	case sdk.FixCustom:
		plug := e.Plugins.ActiveGet(fix.Plugin)
		if plug == nil {
			plug = e.Plugins.ActiveGet(diag.Plugin)
		}
		if plug == nil {
			return nil, Errf("err.pluginUnavailable", fix.Plugin)
		}
		m := findModule(p, moduleID)
		if m == nil {
			return nil, ErrNotFound
		}
		var rc *sdk.RunConfiguration
		if _, c := findConfig(p, configID); c != nil {
			x := c.RunConfiguration
			rc = &x
		}
		var r sdk.ApplyFixResult
		t := e.NewTask("fix", fix.Title, projectID, moduleID, func(tctx context.Context, ts *taskState) (any, error) {
			tcs, err := e.ensureToolchains(tctx, p, m, ts.t.ID)
			if err != nil {
				return nil, err
			}
			err = e.Plugins.Call(tctx, plug, sdk.MethodApplyFix, &sdk.ApplyFixParams{Fix: *fix, Diagnosis: *diag, Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Config: rc, Toolchains: tcs, Env: p.Settings.Env, TaskID: ts.t.ID}, &r)
			if err == nil && r.Message != "" {
				ts.console.System(r.Message)
			}
			return nil, err
		})
		res.Task = &t
		done, err := e.WaitTask(ctx, t.ID)
		if err != nil {
			return res, nil // 调用方断开：任务继续在后台执行
		}
		if done.State != model.TaskSucceeded {
			return nil, &UserError{Msg: firstNonEmptyStr(done.Error, fix.Title)}
		}
		if r.Changes != nil {
			if err := e.applyChanges(ctx, projectID, configID, r.Changes); err != nil {
				return nil, err
			}
		}
		res.Message = r.Message
		rerun = r.Rerun
	default:
		return nil, Errf("err.unknownFix", fix.Kind)
	}
	// 改了工具链/环境变量的修复：重新分析，让已被修复的静态问题消失
	switch fix.Kind {
	case sdk.FixSetToolchain, sdk.FixSetEnv, sdk.FixAppendEnv, sdk.FixUnsetEnv, sdk.FixCustom:
		go e.reanalyze(projectID)
	}
	if rerun && runID != "" {
		nr, err := e.RestartRun(runID, "", "")
		if err == nil {
			res.Rerun = &nr
		}
	}
	return res, nil
}

// reanalyze 静默重新分析（不建任务），用于修复之后刷新问题列表。
func (e *Engine) reanalyze(projectID string) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	e.refreshModules(ctx, p)
	_, err := e.Store.UpdateProject(projectID, func(pp *model.Project) error {
		pp.Modules, pp.AnalyzedAt, pp.VCSRoots, pp.DefaultConfigID, pp.DefaultByUser = p.Modules, p.AnalyzedAt, p.VCSRoots, p.DefaultConfigID, p.DefaultByUser
		pp.DetectVersion = p.DetectVersion
		return nil
	})
	if err == nil {
		e.publishProject(projectID)
	}
}

func hasFix(d sdk.Diagnosis, id string) bool {
	for _, f := range d.Fixes {
		if f.ID == id {
			return true
		}
	}
	return false
}

func (e *Engine) applyChanges(ctx context.Context, projectID, configID string, ch *sdk.ProjectChanges) error {
	for kind, cons := range ch.Toolchains {
		if _, err := e.SetProjectToolchain(ctx, projectID, kind, &model.ToolchainOverride{Constraint: cons}); err != nil {
			return err
		}
	}
	if len(ch.Env) > 0 {
		_, err := e.Store.UpdateProject(projectID, func(pp *model.Project) error {
			if pp.Settings.Env == nil {
				pp.Settings.Env = map[string]string{}
			}
			for k, v := range ch.Env {
				if v == nil {
					delete(pp.Settings.Env, k)
				} else {
					pp.Settings.Env[k] = *v
				}
			}
			return nil
		})
		if err != nil {
			return err
		}
	}
	if len(ch.RunOptions) > 0 && configID != "" {
		if _, err := e.UpdateRunConfig(projectID, configID, nil, ch.RunOptions, nil); err != nil {
			return err
		}
	}
	if ch.Reimport {
		_, _ = e.Reimport(projectID)
	}
	e.publishProject(projectID)
	return nil
}

// ProjectProblems 汇总工程的静态问题、缺失工具链、依赖未安装。
// 返回的 Diagnosis.Line 暂存模块 id（供 ApplyFix 定位）。
func (e *Engine) ProjectProblems(projectID string) []sdk.Diagnosis {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return nil
	}
	var out []sdk.Diagnosis
	for _, m := range p.Modules {
		for plugID, a := range m.Analysis {
			for i, d := range a.Problems {
				if d.ID == "" {
					d.ID = sdk.StableID(m.ID, plugID, d.RuleID, d.Title, strconv.Itoa(i))
				}
				d.Plugin = plugID
				for j := range d.Fixes {
					d.Fixes[j].Plugin = plugID
				}
				d.Line = m.ID
				out = append(out, d)
			}
		}
		for kind, sel := range m.Toolchains {
			if !sel.Missing {
				continue
			}
			want := constraintForInstall(sel)
			out = append(out, sdk.Diagnosis{
				ID: sdk.StableID(m.ID, "toolchain", kind), RuleID: "core.toolchain-missing", Severity: "warning",
				Title: i18n.T("diag.toolchainMissing.title", kind, sel.Constraint), Message: sel.Source, Line: m.ID,
				Fixes: []sdk.Fix{{ID: "install", Title: i18n.T("fix.installToolchain", kind, want), Kind: sdk.FixSetToolchain, Params: map[string]string{"kind": kind, "constraint": firstNonEmptyStr(sel.Constraint, want)}, Recommended: true}},
			})
		}
		if m.Dependencies != nil && (m.Dependencies.State == "missing" || m.Dependencies.State == "outdated") {
			out = append(out, sdk.Diagnosis{
				ID: sdk.StableID(m.ID, "deps"), RuleID: "core.deps-" + m.Dependencies.State, Severity: "warning",
				Title: i18n.T("diag.deps."+m.Dependencies.State, orDot(m.RelDir)), Message: m.Dependencies.Message, Line: m.ID,
				Fixes: []sdk.Fix{{ID: "install", Title: i18n.T("fix.installDeps"), Kind: sdk.FixInstallDependencies, Recommended: true}},
			})
		}
	}
	return out
}

func firstNonEmptyStr(xs ...string) string {
	for _, x := range xs {
		if x != "" {
			return x
		}
	}
	return ""
}

// optionIsText 判断运行配置的某个选项是否是文本类（string / select / 未声明），这类值不做数字解析。
func (e *Engine) optionIsText(p *model.Project, configID, key string) bool {
	_, c := findConfig(p, configID)
	if c == nil {
		return false
	}
	plug := e.Plugins.Get(c.Plugin)
	if plug == nil {
		return false
	}
	for _, t := range plug.Manifest.Contributes.RunConfigurationTypes {
		if t.ID != c.Type {
			continue
		}
		for _, o := range t.Options {
			if o.Key == key {
				switch o.Type {
				case "number", "boolean", "multiselect", "keyvalue", "list":
					return false
				}
				return true
			}
		}
	}
	return false
}
