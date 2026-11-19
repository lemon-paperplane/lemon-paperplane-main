package engine

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/mirrors"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk/archive"
)

type execHandle struct {
	cmd *exec.Cmd
}

// HandleHost 实现 plugins.HostHandler。
func (e *Engine) HandleHost(ctx context.Context, from *plugins.Plugin, method string, raw json.RawMessage) (any, error) {
	switch method {
	case sdk.HostLog:
		var p sdk.LogParams
		_ = json.Unmarshal(raw, &p)
		e.logf("[%s %s] %s", from.ID(), p.Level, p.Message)
		return nil, nil
	case sdk.HostConsole:
		var p sdk.ConsoleParams
		_ = json.Unmarshal(raw, &p)
		if c := e.Console(p.Console); c != nil {
			stream := p.Stream
			if stream == "" {
				stream = "system"
			}
			for _, l := range strings.Split(p.Text, "\n") {
				c.Append(stream, l)
			}
		}
		return nil, nil
	case sdk.HostProgress:
		var p sdk.ProgressParams
		_ = json.Unmarshal(raw, &p)
		e.taskProgress(p.Token, p.Message, p.Percent)
		return nil, nil
	case sdk.HostNotify:
		var p sdk.NotifyParams
		_ = json.Unmarshal(raw, &p)
		e.Bus.Publish(model.EvNotify, p)
		return nil, nil
	case sdk.HostToolchains:
		var p sdk.HostToolchainsParams
		_ = json.Unmarshal(raw, &p)
		return &sdk.HostToolchainsResult{Toolchains: e.ToolchainsOf(ctx, p.Kind, false)}, nil
	case sdk.HostCall:
		var p sdk.HostCallParams
		if err := json.Unmarshal(raw, &p); err != nil {
			return nil, err
		}
		target := e.Plugins.ActiveGet(p.Plugin)
		if target == nil {
			return nil, sdk.NewError(sdk.ErrCodeNotSupported, "plugin %s not available", p.Plugin)
		}
		var res json.RawMessage
		err := e.Plugins.Call(ctx, target, sdk.MethodServiceCall, &sdk.ServiceCallParams{Method: p.Method, Params: p.Params, Caller: from.ID()}, &res)
		if err != nil {
			return nil, err
		}
		return res, nil
	case sdk.HostDownload:
		var p sdk.DownloadParams
		if err := json.Unmarshal(raw, &p); err != nil {
			return nil, err
		}
		return e.download(ctx, &p)
	case sdk.HostPrompt:
		var p sdk.PromptParams
		_ = json.Unmarshal(raw, &p)
		return e.Ask(ctx, from.ID(), p), nil
	case sdk.HostExec:
		var p sdk.ExecParams
		if err := json.Unmarshal(raw, &p); err != nil {
			return nil, err
		}
		return e.exec(ctx, &p), nil
	}
	return nil, sdk.NewError(sdk.ErrCodeMethodNotFound, "unknown host method %s", method)
}

// baseEnv 返回 daemon 环境 + 镜像环境变量 + 覆盖层，以及合并后的 PATH 前缀。
func (e *Engine) buildEnv(overlay map[string]string, pathPrepend []string) []string {
	if e.cfg.EnvReady != nil {
		<-e.cfg.EnvReady // 登录 shell 的环境变量还在后台读取
	}
	env := newEnvMap()
	for _, kv := range os.Environ() {
		if k, v, ok := strings.Cut(kv, "="); ok && k != "" {
			env.set(k, v)
		}
	}
	// 从图形界面启动的 daemon 拿不到用户 shell 的 PATH，补齐常见目录
	env.set("PATH", augmentPath(env.get("PATH")))
	for k, v := range mirrors.Env(e.Mirrors()) {
		if !env.has(k) {
			env.set(k, v)
		}
	}
	for k, v := range overlay {
		env.set(k, v)
	}
	if len(pathPrepend) > 0 {
		env.set("PATH", strings.Join(pathPrepend, string(os.PathListSeparator))+string(os.PathListSeparator)+env.get("PATH"))
	}
	return env.list()
}

// envMap 按平台规则合并环境变量：Windows 上变量名不分大小写（系统里是 Path，插件和代码里写 PATH），
// 按大写合并、保留第一次出现的写法；否则子进程会同时拿到 Path 和 PATH，随机用到其中一个。
type envMap struct {
	vals  map[string]string
	names map[string]string
}

func newEnvMap() *envMap { return &envMap{vals: map[string]string{}, names: map[string]string{}} }

func envCanon(k string) string {
	if runtime.GOOS == "windows" {
		return strings.ToUpper(k)
	}
	return k
}

func (m *envMap) set(k, v string) {
	c := envCanon(k)
	if _, ok := m.names[c]; !ok {
		m.names[c] = k
	}
	m.vals[c] = v
}

func (m *envMap) get(k string) string { return m.vals[envCanon(k)] }

func (m *envMap) has(k string) bool {
	_, ok := m.vals[envCanon(k)]
	return ok
}

func (m *envMap) list() []string {
	out := make([]string, 0, len(m.vals))
	for c, v := range m.vals {
		out = append(out, m.names[c]+"="+v)
	}
	return out
}

func augmentPath(p string) string {
	if runtime.GOOS == "windows" {
		return p
	}
	home, _ := os.UserHomeDir()
	extra := []string{"/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin", filepath.Join(home, ".local", "bin"), filepath.Join(home, "go", "bin"), filepath.Join(home, ".cargo", "bin")}
	have := map[string]bool{}
	for _, d := range filepath.SplitList(p) {
		have[d] = true
	}
	parts := filepath.SplitList(p)
	for _, d := range extra {
		if !have[d] {
			if st, err := os.Stat(d); err == nil && st.IsDir() {
				parts = append(parts, d)
			}
		}
	}
	return strings.Join(parts, string(os.PathListSeparator))
}

// lookPath 在给定环境的 PATH 中找命令。
func lookPath(cmd string, env []string) string {
	if filepath.IsAbs(cmd) || strings.ContainsRune(cmd, filepath.Separator) || (runtime.GOOS == "windows" && strings.Contains(cmd, "/")) {
		return cmd
	}
	path := ""
	for _, kv := range env {
		if strings.HasPrefix(kv, "PATH=") || (runtime.GOOS == "windows" && strings.HasPrefix(strings.ToUpper(kv), "PATH=")) {
			path = kv[5:]
		}
	}
	exts := []string{""}
	if runtime.GOOS == "windows" {
		exts = []string{".exe", ".cmd", ".bat", ".com", ""}
	}
	for _, d := range filepath.SplitList(path) {
		for _, ext := range exts {
			c := filepath.Join(d, cmd+ext)
			if st, err := os.Stat(c); err == nil && !st.IsDir() {
				return c
			}
		}
	}
	return cmd
}

type capBuf struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (c *capBuf) Write(p []byte) (int, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.buf.Len() < 4<<20 {
		c.buf.Write(p)
	}
	return len(p), nil
}

func (c *capBuf) String() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.buf.String()
}

// exec 执行插件请求的命令。
func (e *Engine) exec(ctx context.Context, p *sdk.ExecParams) *sdk.ExecResult {
	start := time.Now()
	if p.TimeoutMs > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, time.Duration(p.TimeoutMs)*time.Millisecond)
		defer cancel()
	}
	env := e.buildEnv(p.Env, p.PathPrepend)
	cmd := exec.Command(lookPath(p.Command, env), p.Args...)
	cmd.Dir = p.Cwd
	cmd.Env = env
	proc.Prepare(cmd)
	if p.Stdin != "" {
		cmd.Stdin = strings.NewReader(p.Stdin)
	}
	var out, errb capBuf
	c := e.Console(p.Console)
	var ow, ew interface {
		io.Writer
		Flush()
	}
	if c != nil {
		title := p.Title
		cmdline := displayCommand(p.Command, p.Args)
		if p.Display != "" {
			cmdline = p.Display
		}
		if title != "" {
			c.System(title)
		}
		c.System("$ " + cmdline)
		ow = c.Writer("stdout")
		ew = c.Writer("stderr")
		cmd.Stdout = io.MultiWriter(ow, &out)
		cmd.Stderr = io.MultiWriter(ew, &errb)
	} else {
		cmd.Stdout = &out
		cmd.Stderr = &errb
	}
	res := &sdk.ExecResult{}
	if err := cmd.Start(); err != nil {
		res.ExitCode = 127
		res.Stderr = err.Error()
		if c != nil {
			c.Append("stderr", err.Error())
		}
		return res
	}
	proc.Started(cmd)
	h := &execHandle{cmd: cmd}
	if p.Console != "" {
		e.mu.Lock()
		if e.execs[p.Console] == nil {
			e.execs[p.Console] = map[*execHandle]struct{}{}
		}
		e.execs[p.Console][h] = struct{}{}
		e.mu.Unlock()
		defer func() {
			e.mu.Lock()
			delete(e.execs[p.Console], h)
			e.mu.Unlock()
		}()
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	select {
	case err := <-done:
		res.ExitCode = exitCode(err)
	case <-ctx.Done():
		_ = proc.SignalGroup(cmd.Process.Pid, "SIGTERM")
		select {
		case <-done:
		case <-time.After(5 * time.Second):
			proc.KillGroup(cmd.Process.Pid)
			<-done
		}
		res.ExitCode = -1
		if ctx.Err() == context.DeadlineExceeded {
			res.TimedOut = true
		} else {
			res.Cancelled = true
		}
	}
	if ow != nil {
		ow.Flush()
		ew.Flush()
	}
	if p.Capture {
		res.Stdout = out.String()
		res.Stderr = errb.String()
	} else if res.ExitCode != 0 {
		s := errb.String()
		if len(s) > 8000 {
			s = s[len(s)-8000:]
		}
		res.Stderr = s
	}
	res.DurationMs = time.Since(start).Milliseconds()
	return res
}

func (e *Engine) killExecs(consoleID string) {
	e.mu.Lock()
	var hs []*execHandle
	for h := range e.execs[consoleID] {
		hs = append(hs, h)
	}
	e.mu.Unlock()
	for _, h := range hs {
		if h.cmd.Process != nil {
			_ = proc.SignalGroup(h.cmd.Process.Pid, "SIGTERM")
			pid := h.cmd.Process.Pid
			time.AfterFunc(5*time.Second, func() { proc.KillGroup(pid) })
		}
	}
}

func exitCode(err error) int {
	if err == nil {
		return 0
	}
	if ee, ok := err.(*exec.ExitError); ok {
		if ee.ExitCode() >= 0 {
			return ee.ExitCode()
		}
		return 1
	}
	return 1
}

func displayCommand(cmd string, args []string) string {
	parts := []string{quoteArg(shortHome(cmd))}
	for _, a := range args {
		parts = append(parts, quoteArg(a))
	}
	s := strings.Join(parts, " ")
	if len(s) > 2000 {
		s = s[:2000] + " …"
	}
	return s
}

func quoteArg(a string) string {
	if a == "" {
		return `""`
	}
	if strings.ContainsAny(a, " \t\"'$&|;<>()") {
		return `"` + strings.ReplaceAll(a, `"`, `\"`) + `"`
	}
	return a
}

// download 执行 host/download。
func (e *Engine) download(ctx context.Context, p *sdk.DownloadParams) (*sdk.DownloadResult, error) {
	file := p.Dest
	if p.Extract != "" {
		file = filepath.Join(e.Layout.Cache, "downloads", sdk.StableID(p.URLs...)+"-"+filepath.Base(firstNonEmpty(p.URLs)))
	}
	title := p.Title
	if title == "" {
		title = i18n.T("task.download", filepath.Base(firstNonEmpty(p.URLs)))
	}
	if p.TaskID != "" {
		e.consoleSystem(p.TaskID, title)
	}
	var lastLog time.Time
	u, err := archive.Download(ctx, p.URLs, file, p.SHA256, func(done, total int64) {
		pct := -1.0
		if total > 0 {
			pct = float64(done) * 100 / float64(total)
		}
		msg := fmt.Sprintf("%s  %.1f MB", title, float64(done)/1048576)
		if total > 0 {
			msg = fmt.Sprintf("%s  %.1f / %.1f MB", title, float64(done)/1048576, float64(total)/1048576)
		}
		e.taskProgress(p.TaskID, msg, pct)
		if p.TaskID != "" && time.Since(lastLog) > 3*time.Second {
			lastLog = time.Now()
			e.consoleSystem(p.TaskID, msg)
		}
	})
	if err != nil {
		return nil, err
	}
	if p.Extract != "" {
		if p.TaskID != "" {
			e.consoleSystem(p.TaskID, i18n.T("task.extracting"))
		}
		if err := archive.Extract(file, p.Dest, p.Extract, p.StripComponents); err != nil {
			return nil, err
		}
		_ = os.Remove(file)
	}
	return &sdk.DownloadResult{Path: p.Dest, URL: u}, nil
}

func firstNonEmpty(xs []string) string {
	for _, x := range xs {
		if x != "" {
			return x
		}
	}
	return ""
}

// ---------------------------------------------------------------------------
// 对话框（host/prompt、askpass）

type promptState struct {
	p    model.Prompt
	resp chan sdk.PromptResult
}

// Ask 发起对话框并等待界面回答；没有界面连接时直接返回取消。
func (e *Engine) Ask(ctx context.Context, plugin string, p sdk.PromptParams) *sdk.PromptResult {
	if e.Bus.Subscribers() == 0 {
		return &sdk.PromptResult{Cancelled: true}
	}
	ps := &promptState{p: model.Prompt{ID: store.NewID(), PromptParams: p, Plugin: plugin}, resp: make(chan sdk.PromptResult, 1)}
	e.mu.Lock()
	e.prompts[ps.p.ID] = ps
	e.mu.Unlock()
	defer func() {
		e.mu.Lock()
		delete(e.prompts, ps.p.ID)
		e.mu.Unlock()
		e.Bus.Publish(model.EvPromptDone, map[string]string{"id": ps.p.ID})
	}()
	e.Bus.Publish(model.EvPrompt, ps.p)
	timeout := time.NewTimer(10 * time.Minute)
	defer timeout.Stop()
	select {
	case r := <-ps.resp:
		return &r
	case <-ctx.Done():
	case <-timeout.C:
	}
	return &sdk.PromptResult{Cancelled: true}
}

// Respond 是界面对对话框的回答。
func (e *Engine) Respond(id string, r sdk.PromptResult) error {
	e.mu.Lock()
	ps := e.prompts[id]
	e.mu.Unlock()
	if ps == nil {
		return ErrNotFound
	}
	select {
	case ps.resp <- r:
	default:
	}
	return nil
}

// PendingPrompts 返回未回答的对话框。
func (e *Engine) PendingPrompts() []model.Prompt {
	e.mu.Lock()
	defer e.mu.Unlock()
	out := []model.Prompt{}
	for _, ps := range e.prompts {
		out = append(out, ps.p)
	}
	return out
}
