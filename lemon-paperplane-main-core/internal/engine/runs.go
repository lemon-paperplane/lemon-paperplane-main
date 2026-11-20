package engine

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

type runState struct {
	mu       sync.Mutex
	r        model.Run
	cancel   context.CancelFunc
	cmd      *exec.Cmd
	stdin    io.WriteCloser
	console  *console.Console
	spec     *sdk.LaunchSpec
	stopSig  string
	stopWait time.Duration
	exited   chan struct{}
	matchers *runMatchers
	diagSeen map[string]bool
	lastPub  time.Time
	portScan time.Time
	history  []model.ResourceSample // 最近 5 分钟（2 秒一点，最多 150 点）
	procs    []model.ProcessInfo
}

const statsHistory = 150

func (rs *runState) snapshot() model.Run {
	rs.mu.Lock()
	defer rs.mu.Unlock()
	r := rs.r
	r.URLs = append([]string{}, rs.r.URLs...)
	r.Ports = append([]int{}, rs.r.Ports...)
	r.Diagnoses = append([]sdk.Diagnosis{}, rs.r.Diagnoses...)
	if rs.console != nil {
		r.Lines = rs.console.LastSeq()
	}
	return r
}

func active(state string) bool {
	return state == model.RunPreparing || state == model.RunStarting || state == model.RunRunning || state == model.RunStopping
}

func (e *Engine) activeRunCount() int {
	e.mu.Lock()
	defer e.mu.Unlock()
	n := 0
	for _, rs := range e.runs {
		if active(rs.snapshot().State) {
			n++
		}
	}
	return n
}

// Runs 返回运行列表（新的在前）。
func (e *Engine) Runs(projectID string, activeOnly bool) []model.Run {
	e.mu.Lock()
	ids := append([]string{}, e.runOrder...)
	e.mu.Unlock()
	var out []model.Run
	for i := len(ids) - 1; i >= 0; i-- {
		rs := e.runByID(ids[i])
		if rs == nil {
			continue
		}
		r := rs.snapshot()
		if projectID != "" && r.ProjectID != projectID {
			continue
		}
		if activeOnly && !active(r.State) {
			continue
		}
		out = append(out, r)
	}
	return out
}

func (e *Engine) runByID(id string) *runState {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.runs[id]
}

// Run 返回单个运行。
func (e *Engine) Run(id string) (model.Run, bool) {
	rs := e.runByID(id)
	if rs == nil {
		return model.Run{}, false
	}
	return rs.snapshot(), true
}

func (e *Engine) publishRun(rs *runState, force bool) {
	rs.mu.Lock()
	if !force && time.Since(rs.lastPub) < 150*time.Millisecond {
		rs.mu.Unlock()
		return
	}
	rs.lastPub = time.Now()
	rs.mu.Unlock()
	r := rs.snapshot()
	e.Bus.Publish(model.EvRun, r)
	e.publishProject(r.ProjectID)
}

// StartRun 启动运行配置（configID 为空用默认配置）。同一配置已在运行时返回现有运行。
func (e *Engine) StartRun(projectID, configID string) (model.Run, error) {
	p, ok := e.Store.Project(projectID)
	if !ok {
		return model.Run{}, ErrNotFound
	}
	if configID == "" {
		configID = p.DefaultConfigID
	}
	m, cfg := findConfig(p, configID)
	if cfg == nil {
		return model.Run{}, Errf("err.noRunConfig")
	}
	for _, r := range e.Runs(projectID, true) {
		if r.ConfigID == configID {
			return r, nil
		}
	}
	plug := e.Plugins.ActiveGet(cfg.Plugin)
	if plug == nil {
		return model.Run{}, Errf("err.pluginUnavailable", cfg.Plugin)
	}
	id := store.NewID()
	settings := e.Store.Settings()
	logFile := filepath.Join(e.Layout.RunLogs, id+".log")
	c := console.New(id, settings.LogLinesPerRun, logFile)
	ctx, cancel := context.WithCancel(context.Background())
	rs := &runState{
		r:      model.Run{ID: id, ProjectID: projectID, ModuleID: m.ID, ConfigID: cfg.ID, Name: cfg.Name, Plugin: cfg.Plugin, State: model.RunPreparing, StartedAt: time.Now(), LogFile: logFile},
		cancel: cancel, console: c, exited: make(chan struct{}), diagSeen: map[string]bool{},
	}
	rs.matchers = e.runMatchers(p, m, "run", cfg.Plugin)
	c.OnLine(func(l model.LogLine) { e.onRunLine(rs, l) })
	e.mu.Lock()
	e.runs[id] = rs
	e.runOrder = append(e.runOrder, id)
	e.consoles[id] = c
	e.trimRunsLocked()
	e.mu.Unlock()
	e.publishRun(rs, true)
	go e.runLifecycle(ctx, rs, plug)
	return rs.snapshot(), nil
}

// runsPerConfig：每个运行配置保留最近几次已结束的运行（连同日志）
const runsPerConfig = 5

func (e *Engine) trimRunsLocked() {
	keep := e.Store.Settings().LogRetentionRuns
	if keep <= 0 {
		keep = 20
	}
	// 每个配置只保留最近若干次已结束的运行
	count := map[string]int{}
	var next []string
	for i := len(e.runOrder) - 1; i >= 0; i-- {
		id := e.runOrder[i]
		rs := e.runs[id]
		if rs == nil {
			continue
		}
		r := rs.snapshot()
		if !active(r.State) {
			count[r.ConfigID]++
			if count[r.ConfigID] > runsPerConfig || len(next) > keep*4 {
				delete(e.runs, id)
				delete(e.consoles, id)
				_ = os.Remove(r.LogFile)
				continue
			}
		}
		next = append([]string{id}, next...)
	}
	e.runOrder = next
}

func (e *Engine) setRun(rs *runState, fn func(r *model.Run)) {
	rs.mu.Lock()
	fn(&rs.r)
	rs.mu.Unlock()
	e.publishRun(rs, true)
}

func (e *Engine) failRun(rs *runState, err error) {
	msg := errString(err)
	rs.console.System(i18n.T("run.prepareFailed", msg))
	now := time.Now()
	e.setRun(rs, func(r *model.Run) {
		if r.StoppedByUser {
			r.State = model.RunExited
		} else {
			r.State = model.RunFailed
			r.Error = msg
		}
		r.EndedAt = &now
	})
	e.recordLastRun(rs.snapshot())
	rs.console.Close()
	close(rs.exited)
}

// recordLastRun 把结束的运行记到工程上（随工程保存，卡片上显示「上次运行」）。
func (e *Engine) recordLastRun(r model.Run) {
	if r.EndedAt == nil || r.ProjectID == "" {
		return
	}
	lr := &model.LastRun{ConfigID: r.ConfigID, Name: r.Name, EndedAt: *r.EndedAt, Failed: r.State == model.RunFailed}
	if r.ExitCode != nil {
		lr.ExitCode = *r.ExitCode
	}
	if _, err := e.Store.UpdateProject(r.ProjectID, func(p *model.Project) error {
		p.LastRun = lr
		return nil
	}); err == nil {
		e.publishProject(r.ProjectID)
	}
	go e.persistRuns(false)
}

func (e *Engine) runLifecycle(ctx context.Context, rs *runState, plug *plugins.Plugin) {
	r := rs.snapshot()
	p, ok := e.Store.Project(r.ProjectID)
	if !ok {
		e.failRun(rs, ErrNotFound)
		return
	}
	m, cfg := findConfig(p, r.ConfigID)
	if cfg == nil {
		e.failRun(rs, Errf("err.noRunConfig"))
		return
	}
	// 1. 工具链
	tcs, err := e.ensureToolchains(ctx, p, m, r.ID)
	if err != nil {
		e.failRun(rs, err)
		e.diagnoseRunFailure(rs, p, m, cfg)
		return
	}
	// 2. 同步（首次运行前）
	if m.NeedsSync && !m.Synced {
		rs.console.System(i18n.T("run.syncing"))
		if err := e.syncModules(ctx, p.ID, m.ID, r.ID); err != nil {
			e.failRun(rs, err)
			e.diagnoseRunFailure(rs, p, m, cfg)
			return
		}
		if np, ok := e.Store.Project(p.ID); ok {
			p = np
			m, cfg = findConfig(p, r.ConfigID)
			if cfg == nil {
				e.failRun(rs, Errf("err.noRunConfig"))
				return
			}
			tcs = e.moduleToolchains(m)
		}
	}
	// 3. 依赖
	if m.Dependencies != nil && m.Dependencies.State == "missing" && e.Store.Settings().AutoInstallDependencies {
		rs.console.System(i18n.T("run.installingDeps"))
		if err := e.installDeps(ctx, p, m, r.ID, false); err != nil {
			e.failRun(rs, err)
			e.diagnoseRunFailure(rs, p, m, cfg)
			return
		}
	}
	if ctx.Err() != nil {
		e.failRun(rs, ctx.Err())
		return
	}
	// 4. 插件准备
	env := sdk.MergeEnv(p.Settings.Env, cfg.Env)
	var spec sdk.LaunchSpec
	err = e.Plugins.Call(ctx, plug, sdk.MethodPrepareRun, &sdk.PrepareRunParams{
		RunID: r.ID, Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Config: cfg.RunConfiguration,
		Toolchains: tcs, Env: env, Settings: e.pluginSettings(plug, e.Store.Settings()),
	}, &spec)
	if err != nil {
		e.failRun(rs, err)
		if ctx.Err() == nil {
			e.diagnoseRunFailure(rs, p, m, cfg)
		}
		return
	}
	// 5. 启动主进程
	var tcEnv map[string]string
	var tcPath []string
	{
		var list []sdk.Toolchain
		for _, t := range tcs {
			list = append(list, t)
		}
		tcEnv, tcPath = sdk.ToolchainEnv(list...)
	}
	envList := e.buildEnv(sdk.MergeEnv(tcEnv, env, spec.Env), append(append([]string{}, spec.PathPrepend...), tcPath...))
	cwd := spec.Cwd
	if cwd == "" {
		cwd = filepath.Join(p.Path, filepath.FromSlash(m.RelDir))
	}
	cmd := exec.Command(lookPath(spec.Command, envList), spec.Args...)
	cmd.Dir = cwd
	cmd.Env = envList
	proc.Prepare(cmd)
	stdout := rs.console.Writer("stdout")
	stderr := rs.console.Writer("stderr")
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	stdin, _ := cmd.StdinPipe()
	display := spec.Display
	if display == "" {
		display = displayCommand(spec.Command, spec.Args)
	}
	if len(spec.URLPatterns) > 0 || len(spec.ReadyPatterns) > 0 {
		rs.matchers.addDynamic(spec.URLPatterns, spec.ReadyPatterns)
	}
	// 端口冲突预检
	for _, port := range spec.Ports {
		if pids := userPIDs(proc.PIDsOnPort(port)); len(pids) > 0 {
			rs.console.System(i18n.T("run.portBusy", port, pids[0], proc.ProcessName(pids[0])))
			e.addDiagnosis(rs, p, m, sdk.Diagnosis{RuleID: "core.port-busy", Severity: "warning", Title: i18n.T("diag.portBusy.title", port), Message: i18n.T("diag.portBusy.message", pids[0], proc.ProcessName(pids[0])),
				Fixes: []sdk.Fix{{ID: "kill", Title: i18n.T("fix.killPort", port), Kind: sdk.FixKillPort, Params: map[string]string{"port": strconv.Itoa(port)}, Recommended: true, Confirm: i18n.T("fix.killPort.confirm", port)}}})
		}
	}
	if ctx.Err() != nil {
		e.failRun(rs, ctx.Err())
		return
	}
	rs.console.System("$ " + display)
	if err := cmd.Start(); err != nil {
		e.failRun(rs, Errf("err.startFailed", err.Error()))
		e.diagnoseRunFailure(rs, p, m, cfg)
		return
	}
	proc.Started(cmd)
	stopSig := spec.StopSignal
	if stopSig == "" {
		stopSig = "SIGTERM"
	}
	wait := time.Duration(spec.StopTimeoutMs) * time.Millisecond
	if wait <= 0 {
		wait = 8 * time.Second
	}
	rs.mu.Lock()
	rs.cmd, rs.stdin, rs.spec, rs.stopSig, rs.stopWait = cmd, stdin, &spec, stopSig, wait
	stopped := rs.r.StoppedByUser
	rs.mu.Unlock()
	e.setRun(rs, func(r *model.Run) {
		r.State = model.RunStarting
		r.PID = cmd.Process.Pid
		r.Command = display
		r.Cwd = cwd
	})
	e.saveActivePIDs()
	if stopped {
		go e.signalStop(rs)
	}
	// 就绪兜底：没有就绪规则的 3 秒后视为运行中；有规则的 120 秒后兜底
	go func() {
		d := 3 * time.Second
		if rs.matchers.hasReady() {
			d = 120 * time.Second
		}
		select {
		case <-time.After(d):
			e.markReady(rs)
		case <-rs.exited:
		}
	}()
	err = cmd.Wait()
	stdout.Flush()
	stderr.Flush()
	code := exitCode(err)
	now := time.Now()
	var final model.Run
	e.setRun(rs, func(r *model.Run) {
		if !r.StoppedByUser {
			// 用户停止时进程是被信号结束的，退出码没有意义
			r.ExitCode = &code
		}
		r.EndedAt = &now
		r.CPU, r.RSSMB = 0, 0
		if r.StoppedByUser || code == 0 {
			r.State = model.RunExited
		} else {
			r.State = model.RunFailed
		}
		final = *r
	})
	e.recordLastRun(final)
	if final.StoppedByUser {
		rs.console.System(i18n.T("run.stopped"))
	} else {
		rs.console.System(i18n.T("run.exited", code))
	}
	if final.State == model.RunFailed {
		e.diagnoseRunFailure(rs, p, m, cfg)
	}
	// 进程组里可能还有残留子进程（如被收养的 node 子进程），一并清理
	proc.KillGroup(cmd.Process.Pid)
	rs.console.Close()
	close(rs.exited)
	e.publishRun(rs, true)
	e.saveActivePIDs()
}

func (e *Engine) markReady(rs *runState) {
	rs.mu.Lock()
	if rs.r.State != model.RunStarting {
		rs.mu.Unlock()
		return
	}
	now := time.Now()
	rs.r.State = model.RunRunning
	rs.r.ReadyAt = &now
	rs.mu.Unlock()
	e.publishRun(rs, true)
}

// StopRun 停止运行。
func (e *Engine) StopRun(id string) (model.Run, error) {
	rs := e.runByID(id)
	if rs == nil {
		return model.Run{}, ErrNotFound
	}
	rs.mu.Lock()
	state := rs.r.State
	if !active(state) {
		rs.mu.Unlock()
		return rs.snapshot(), nil
	}
	rs.r.StoppedByUser = true
	rs.mu.Unlock()
	if state == model.RunPreparing {
		rs.cancel()
		e.killExecs(id)
		return rs.snapshot(), nil
	}
	e.setRun(rs, func(r *model.Run) { r.State = model.RunStopping })
	go e.signalStop(rs)
	return rs.snapshot(), nil
}

func (e *Engine) signalStop(rs *runState) {
	rs.mu.Lock()
	cmd, sig, wait := rs.cmd, rs.stopSig, rs.stopWait
	rs.mu.Unlock()
	if cmd == nil || cmd.Process == nil {
		return
	}
	pid := cmd.Process.Pid
	_ = proc.SignalGroup(pid, sig)
	select {
	case <-rs.exited:
	case <-time.After(wait):
		rs.console.System(i18n.T("run.forceKill"))
		proc.KillGroup(pid)
	}
}

// RestartRun 停止后以同一配置重新启动。
func (e *Engine) RestartRun(runID, projectID, configID string) (model.Run, error) {
	if runID != "" {
		rs := e.runByID(runID)
		if rs == nil {
			return model.Run{}, ErrNotFound
		}
		r := rs.snapshot()
		projectID, configID = r.ProjectID, r.ConfigID
		if active(r.State) {
			_, _ = e.StopRun(runID)
			select {
			case <-rs.exited:
			case <-time.After(15 * time.Second):
			}
		}
	} else {
		for _, r := range e.Runs(projectID, true) {
			if r.ConfigID == configID || (configID == "" && r.ConfigID == "") {
				return e.RestartRun(r.ID, "", "")
			}
		}
	}
	return e.StartRun(projectID, configID)
}

// StopAll 停止工作区/工程（都为空则全部）的运行。
func (e *Engine) StopAll(workspaceID, projectID string) int {
	n := 0
	inWS := map[string]bool{}
	if workspaceID != "" {
		if w, ok := e.Store.Workspace(workspaceID); ok {
			for _, id := range w.ProjectIDs {
				inWS[id] = true
			}
		}
	}
	for _, r := range e.Runs("", true) {
		if projectID != "" && r.ProjectID != projectID {
			continue
		}
		if workspaceID != "" && !inWS[r.ProjectID] {
			continue
		}
		_, _ = e.StopRun(r.ID)
		n++
	}
	return n
}

// RunInput 写入标准输入。
func (e *Engine) RunInput(id, text string) error {
	rs := e.runByID(id)
	if rs == nil {
		return ErrNotFound
	}
	rs.mu.Lock()
	w := rs.stdin
	rs.mu.Unlock()
	if w == nil {
		return Errf("err.notRunning")
	}
	if !strings.HasSuffix(text, "\n") {
		text += "\n"
	}
	rs.console.Append("system", "> "+strings.TrimRight(text, "\n"))
	_, err := io.WriteString(w, text)
	return err
}

// ---------------------------------------------------------------------------
// 日志行处理：URL、就绪、诊断

func (e *Engine) onRunLine(rs *runState, l model.LogLine) {
	text := console.StripANSI(l.Text)
	if text == "" {
		return
	}
	m := rs.matchers
	for _, u := range m.urls {
		if u.hint != "" && !strings.Contains(text, u.hint) {
			continue
		}
		sub := u.re.FindStringSubmatch(text)
		if sub == nil {
			continue
		}
		url := u.url
		if url == "" {
			if len(sub) > 1 {
				url = sub[1]
			} else {
				url = sub[0]
			}
		} else {
			url = fill(url, sub)
		}
		url = normalizeURL(url)
		changed := false
		rs.mu.Lock()
		if !containsStr(rs.r.URLs, url) && len(rs.r.URLs) < 8 {
			rs.r.URLs = append(rs.r.URLs, url)
			changed = true
		}
		rs.mu.Unlock()
		if u.ready {
			e.markReady(rs)
		} else if changed {
			e.publishRun(rs, true)
		}
	}
	// 通用兜底：插件规则没给出地址时，识别日志里的本机地址
	rs.mu.Lock()
	noURL := len(rs.r.URLs) == 0
	rs.mu.Unlock()
	if noURL && (strings.Contains(text, "://localhost:") || strings.Contains(text, "://127.0.0.1:") || strings.Contains(text, "://0.0.0.0:") || strings.Contains(text, "://[::1]:")) {
		if u := coreURLRe.FindString(text); u != "" {
			rs.mu.Lock()
			rs.r.URLs = append(rs.r.URLs, normalizeURL(u))
			rs.mu.Unlock()
			e.publishRun(rs, true)
		}
	}
	for _, re := range m.ready {
		if re.hint != "" && !strings.Contains(text, re.hint) {
			continue
		}
		if re.re.MatchString(text) {
			e.markReady(rs)
		}
	}
	pluginHit := false
	for _, d := range m.diags {
		if d.core && pluginHit {
			continue
		}
		if d.hint != "" && !strings.Contains(text, d.hint) {
			continue
		}
		sub := d.re.FindStringSubmatch(text)
		if sub == nil {
			continue
		}
		if !d.core {
			pluginHit = true
		}
		rs.mu.Lock()
		seen := rs.diagSeen[d.rule.ID]
		rs.diagSeen[d.rule.ID] = true
		rs.mu.Unlock()
		if seen {
			continue
		}
		diag := d.build(sub, text)
		rs.mu.Lock()
		if !mergePortDiag(rs, diag) {
			rs.r.Diagnoses = append(rs.r.Diagnoses, diag)
		}
		rs.mu.Unlock()
		e.publishRun(rs, true)
	}
}

var coreURLRe = regexp.MustCompile(`https?://(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):\d{2,5}[^\s'"<>]*`)

var localhostRe = regexp.MustCompile(`^(https?://)(0\.0\.0\.0|\[::\]|\[::1\]|::)(:|/|$)`)

func normalizeURL(u string) string {
	u = strings.TrimRight(strings.TrimSpace(u), ".,;)")
	return localhostRe.ReplaceAllString(u, "${1}localhost$3")
}

func fill(tpl string, sub []string) string {
	for i := len(sub) - 1; i >= 1; i-- {
		tpl = strings.ReplaceAll(tpl, "{"+strconv.Itoa(i)+"}", sub[i])
	}
	return tpl
}

func containsStr(xs []string, s string) bool {
	for _, x := range xs {
		if x == s {
			return true
		}
	}
	return false
}

// userPIDs 去掉系统进程（如 macOS 隔空播放占用的 5000/7000 端口），它们既不该被结束，也不影响绑定 127.0.0.1 的应用。
func userPIDs(pids []int) []int {
	var out []int
	for _, pid := range pids {
		if !proc.IsSystemProcess(pid) {
			out = append(out, pid)
		}
	}
	return out
}

// portKey 返回诊断涉及的端口（带 killPort 修复的），用于合并同一端口的多条诊断。
func portKey(d sdk.Diagnosis) string {
	for _, f := range d.Fixes {
		if f.Kind == sdk.FixKillPort && f.Params["port"] != "" {
			return "port:" + f.Params["port"]
		}
	}
	return ""
}

// mergePortDiag：同一端口已有诊断时合并（插件的更具体，替换内核通用的），返回 true 表示无需再追加。调用方持有 rs.mu。
func mergePortDiag(rs *runState, d sdk.Diagnosis) bool {
	pk := portKey(d)
	if pk == "" {
		if d.RuleID == "core.port-in-use" {
			for _, x := range rs.r.Diagnoses {
				if portKey(x) != "" || x.RuleID == "core.port-in-use" {
					return true
				}
			}
		}
		return false
	}
	for i, x := range rs.r.Diagnoses {
		if portKey(x) != pk && x.RuleID != "core.port-in-use" {
			continue
		}
		if strings.HasPrefix(x.RuleID, "core.") && !strings.HasPrefix(d.RuleID, "core.") {
			for _, f := range x.Fixes {
				if !hasFix(d, f.ID) && !(f.Kind == sdk.FixKillPort && hasKill(d)) {
					d.Fixes = append(d.Fixes, f)
				}
			}
			d.ID = x.ID
			rs.r.Diagnoses[i] = d
			return true
		}
		for _, f := range d.Fixes {
			if !hasFix(rs.r.Diagnoses[i], f.ID) && f.Kind != sdk.FixKillPort {
				rs.r.Diagnoses[i].Fixes = append(rs.r.Diagnoses[i].Fixes, f)
			}
		}
		return true
	}
	return false
}

func hasKill(d sdk.Diagnosis) bool {
	for _, f := range d.Fixes {
		if f.Kind == sdk.FixKillPort {
			return true
		}
	}
	return false
}

func (e *Engine) addDiagnosis(rs *runState, p *model.Project, m *model.Module, d sdk.Diagnosis) {
	if d.ID == "" {
		d.ID = store.NewID()
	}
	rs.mu.Lock()
	key := d.RuleID + "|" + d.Title
	if rs.diagSeen[key] {
		rs.mu.Unlock()
		return
	}
	rs.diagSeen[key] = true
	if mergePortDiag(rs, d) {
		rs.mu.Unlock()
		e.publishRun(rs, true)
		return
	}
	rs.r.Diagnoses = append(rs.r.Diagnoses, d)
	rs.mu.Unlock()
	e.publishRun(rs, true)
}

// diagnoseRunFailure 在运行失败后请插件补充诊断。
func (e *Engine) diagnoseRunFailure(rs *runState, p *model.Project, m *model.Module, cfg *model.RunConfig) {
	r := rs.snapshot()
	tail := rs.console.Tail(300)
	var lines []string
	for _, l := range tail {
		lines = append(lines, console.StripANSI(l.Text))
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	for _, plug := range e.Plugins.Active() {
		if !participates(plug, m) {
			continue
		}
		var res sdk.DiagnoseResult
		var rc *sdk.RunConfiguration
		env := p.Settings.Env
		if cfg != nil {
			c := cfg.RunConfiguration
			rc = &c
			env = sdk.MergeEnv(p.Settings.Env, cfg.Env)
		}
		err := e.Plugins.Call(ctx, plug, sdk.MethodDiagnose, &sdk.DiagnoseParams{RunID: r.ID, Stage: "run", Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Config: rc, ExitCode: r.ExitCode, Env: env, LogTail: lines, Matched: r.Diagnoses}, &res)
		if err != nil {
			continue
		}
		for _, d := range res.Diagnoses {
			d.Plugin = plug.ID()
			for i := range d.Fixes {
				d.Fixes[i].Plugin = plug.ID()
			}
			e.addDiagnosis(rs, p, m, d)
		}
	}
	// 准备阶段的错误（rs.r.Error）也走一遍声明式规则：插件的报错信息里经常就有关键字
	if r.Error != "" {
		for _, line := range strings.Split(r.Error, "\n") {
			e.onRunLine(rs, model.LogLine{Text: line})
		}
	}
}

// diagnoseFailure 对任务（同步、安装）失败做诊断，结果写进任务。
func (e *Engine) diagnoseFailure(ctx context.Context, p *model.Project, m *model.Module, cfg *model.RunConfig, stage, consoleID string, _ []string) {
	c := e.Console(consoleID)
	if c == nil {
		return
	}
	ownPlugin := ""
	if cfg != nil {
		ownPlugin = cfg.Plugin
	}
	matchers := e.runMatchers(p, m, stage, ownPlugin)
	var diags []sdk.Diagnosis
	seen := map[string]bool{}
	var lines []string
	for _, l := range c.Tail(500) {
		text := console.StripANSI(l.Text)
		lines = append(lines, text)
		for _, d := range matchers.diags {
			if seen[d.rule.ID] || (d.hint != "" && !strings.Contains(text, d.hint)) {
				continue
			}
			if sub := d.re.FindStringSubmatch(text); sub != nil {
				seen[d.rule.ID] = true
				diags = append(diags, d.build(sub, text))
			}
		}
	}
	for _, plug := range e.Plugins.Active() {
		if !participates(plug, m) {
			continue
		}
		var res sdk.DiagnoseResult
		if err := e.Plugins.Call(ctx, plug, sdk.MethodDiagnose, &sdk.DiagnoseParams{Stage: stage, Project: e.projectInfo(p), Module: e.moduleInfo(p, m), Env: p.Settings.Env, LogTail: lines, Matched: diags}, &res); err == nil {
			for _, d := range res.Diagnoses {
				d.Plugin = plug.ID()
				if d.ID == "" {
					d.ID = store.NewID()
				}
				diags = append(diags, d)
			}
		}
	}
	if len(diags) == 0 {
		return
	}
	e.mu.Lock()
	ts := e.tasks[consoleID]
	e.mu.Unlock()
	if ts != nil {
		ts.mu.Lock()
		ts.t.Diagnoses = append(ts.t.Diagnoses, diags...)
		ts.mu.Unlock()
		e.Bus.Publish(model.EvTask, ts.snapshot())
	}
	if rs := e.runByID(consoleID); rs != nil {
		for _, d := range diags {
			e.addDiagnosis(rs, p, m, d)
		}
	}
}

// ---------------------------------------------------------------------------
// 统计循环：CPU、内存、端口

func (e *Engine) statsLoop() {
	t := time.NewTicker(2 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-e.stopCh:
			return
		case <-t.C:
		}
		var list []*runState
		var roots []int
		e.mu.Lock()
		for _, rs := range e.runs {
			r := rs.snapshot()
			if (r.State == model.RunStarting || r.State == model.RunRunning || r.State == model.RunStopping) && r.PID > 0 {
				list = append(list, rs)
				roots = append(roots, r.PID)
			}
		}
		e.mu.Unlock()
		if len(list) == 0 {
			continue
		}
		stats := proc.TreeStats(roots)
		var briefs []model.RunBrief
		for _, rs := range list {
			r := rs.snapshot()
			st := stats[r.PID]
			var ports []int
			scan := time.Since(rs.portScan) > 5*time.Second
			if scan {
				ports = proc.ListeningPorts(st.PIDs)
			}
			now := time.Now().UnixMilli()
			rs.mu.Lock()
			rs.r.CPU = round1(st.CPU)
			rs.r.RSSMB = round1(st.RSSMB)
			if rs.r.RSSMB > rs.r.PeakRSSMB {
				rs.r.PeakRSSMB = rs.r.RSSMB
			}
			rs.r.Procs = len(st.PIDs)
			rs.history = append(rs.history, model.ResourceSample{T: now, CPU: rs.r.CPU, RSSMB: rs.r.RSSMB})
			if len(rs.history) > statsHistory {
				rs.history = append(rs.history[:0:0], rs.history[len(rs.history)-statsHistory:]...)
			}
			rs.procs = rs.procs[:0]
			for _, p := range st.Procs {
				rs.procs = append(rs.procs, model.ProcessInfo{PID: p.PID, PPID: p.PPID, Name: p.Name, CPU: round1(p.CPU), RSSMB: round1(p.RSSMB)})
			}
			newPort := false
			if scan {
				rs.portScan = time.Now()
				for _, pt := range ports {
					if !containsInt(rs.r.Ports, pt) {
						rs.r.Ports = append(rs.r.Ports, pt)
						newPort = true
					}
				}
			}
			urlsEmpty := len(rs.r.URLs) == 0
			rs.mu.Unlock()
			if newPort {
				// 进程开始监听端口，视为服务已就绪
				e.markReady(rs)
				if urlsEmpty && len(ports) > 0 && looksLikeWeb(rs) {
					rs.mu.Lock()
					rs.r.URLs = append(rs.r.URLs, "http://localhost:"+strconv.Itoa(ports[0]))
					rs.mu.Unlock()
				}
				e.publishRun(rs, true)
			}
			r = rs.snapshot()
			briefs = append(briefs, model.RunBrief{ID: r.ID, ProjectID: r.ProjectID, ConfigID: r.ConfigID, Name: r.Name, State: r.State, URLs: r.URLs, CPU: r.CPU, RSSMB: r.RSSMB, PeakRSSMB: r.PeakRSSMB, Procs: r.Procs, T: now})
		}
		e.Bus.Publish(model.EvStats, briefs)
	}
}

// looksLikeWeb：有 URL 规则的插件参与时，端口兜底生成地址。
func looksLikeWeb(rs *runState) bool { return rs.matchers.hasURL() }

func round1(f float64) float64 { return float64(int(f*10+0.5)) / 10 }

func containsInt(xs []int, v int) bool {
	for _, x := range xs {
		if x == v {
			return true
		}
	}
	return false
}

// KillPort 结束占用端口的进程。
func (e *Engine) KillPort(port int) []int {
	pids := userPIDs(proc.PIDsOnPort(port))
	for _, pid := range pids {
		_ = proc.Kill(pid)
	}
	return pids
}

// ---------------------------------------------------------------------------
// 孤儿进程：上次 daemon 异常退出时留下的运行

type activePID struct {
	PID   int    `json:"pid"`
	RunID string `json:"runId"`
}

func (e *Engine) pidFile() string { return filepath.Join(e.Layout.Root, "active-runs.json") }

func (e *Engine) saveActivePIDs() {
	var list []activePID
	for _, r := range e.Runs("", true) {
		if r.PID > 0 {
			list = append(list, activePID{PID: r.PID, RunID: r.ID})
		}
	}
	b, _ := json.Marshal(list)
	_ = os.WriteFile(e.pidFile(), b, 0o644)
}

func (e *Engine) killOrphans() {
	var list []activePID
	if sdk.ReadJSON(e.pidFile(), &list) != nil {
		return
	}
	for _, a := range list {
		if proc.Alive(a.PID) {
			e.logf("killing orphaned run %s (pid %d)", a.RunID, a.PID)
			proc.KillGroup(a.PID)
		}
	}
	_ = os.Remove(e.pidFile())
}

// RunStats 返回一次运行最近 5 分钟的资源趋势与进程明细。
func (e *Engine) RunStats(id string) (*model.RunStats, error) {
	rs := e.runByID(id)
	if rs == nil {
		return nil, ErrNotFound
	}
	rs.mu.Lock()
	defer rs.mu.Unlock()
	out := &model.RunStats{RunID: id, State: rs.r.State, CPU: rs.r.CPU, RSSMB: rs.r.RSSMB, PeakRSSMB: rs.r.PeakRSSMB, Ports: append([]int{}, rs.r.Ports...), Interval: 2000,
		Samples: append([]model.ResourceSample{}, rs.history...), Processes: append([]model.ProcessInfo{}, rs.procs...)}
	if !rs.r.StartedAt.IsZero() {
		end := time.Now()
		if rs.r.EndedAt != nil {
			end = *rs.r.EndedAt
		}
		out.UptimeSec = int64(end.Sub(rs.r.StartedAt).Seconds())
	}
	if !active(rs.r.State) {
		out.Processes = []model.ProcessInfo{}
	}
	return out, nil
}
