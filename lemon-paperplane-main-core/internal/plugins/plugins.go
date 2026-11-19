// Package plugins 是插件宿主：发现与加载 manifest、按需拉起插件进程、闲置回收、请求转发。
package plugins

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// HostHandler 处理插件发来的 host/* 请求（由 engine 实现）。
type HostHandler interface {
	HandleHost(ctx context.Context, from *Plugin, method string, params json.RawMessage) (any, error)
}

// Plugin 是一个已发现的插件。
type Plugin struct {
	Manifest       *sdk.Manifest
	Dir            string
	Source         string // bundled | user | dev
	Enabled        bool
	Error          string
	BundledVersion string

	bundle *sdk.Bundle

	mu       sync.Mutex
	conn     *sdk.Conn
	cmd      *exec.Cmd
	exited   chan struct{} // 当前进程退出后关闭（只有 start 里的 goroutine 调 cmd.Wait）
	lastUsed time.Time
	inflight int
	starting chan struct{}
	startErr error
}

// ID 返回插件 id。
func (p *Plugin) ID() string { return p.Manifest.ID }

// T 解析 "%key%" 文案。
func (p *Plugin) T(s string) string {
	if p.bundle == nil {
		return s
	}
	return p.bundle.Resolve(s)
}

// Running 判断进程是否活着。
func (p *Plugin) Running() bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.conn != nil
}

// PID 返回进程号（未运行为 0）。
func (p *Plugin) PID() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.cmd != nil && p.cmd.Process != nil && p.conn != nil {
		return p.cmd.Process.Pid
	}
	return 0
}

// Icon 返回图标引用。
func (p *Plugin) Icon(rel string) string {
	if rel == "" {
		return ""
	}
	if strings.HasPrefix(rel, "builtin:") {
		return rel
	}
	return "plugin:" + p.Manifest.ID + "/" + filepath.ToSlash(rel)
}

// Options 是 Manager 的配置。
type Options struct {
	HostVersion string
	BundledDirs []string // 每个目录下是 <id>/plugin.json
	UserDir     string
	DevDirs     []string // 直接是插件目录，或包含 lemon-paperplane-plugin-* 的父目录
	Host        HostHandler
	InitParams  func(p *Plugin) sdk.InitializeParams
	Idle        func() time.Duration
	Disabled    func() []string
	Logger      *log.Logger
	// EnvReady 关闭前不启动插件进程（内核在后台读取登录 shell 的环境变量，插件要拿到完整的 PATH 等）
	EnvReady <-chan struct{}
}

// Manager 管理全部插件。
type Manager struct {
	opt     Options
	mu      sync.RWMutex
	plugins map[string]*Plugin
	order   []*Plugin
	locale  string
	stop    chan struct{}
}

// New 创建并加载。
func New(opt Options) *Manager {
	m := &Manager{opt: opt, plugins: map[string]*Plugin{}, stop: make(chan struct{})}
	m.Reload("en")
	go m.reaper()
	return m
}

// Reload 重新扫描插件目录（不影响正在运行的插件进程，被替换的会被关闭）。
func (m *Manager) Reload(locale string) {
	found := map[string]*Plugin{}
	consider := func(dir, source string) {
		man, err := sdk.LoadManifest(dir)
		if err != nil || !man.SupportsOS(runtime.GOOS) {
			return
		}
		p := &Plugin{Manifest: man, Dir: dir, Source: source}
		if old, ok := found[man.ID]; ok {
			switch {
			case source == "dev":
			case source == "user" && old.Source == "bundled":
				if sdk.CompareVersions(man.Version, old.Manifest.Version) < 0 {
					return
				}
				p.BundledVersion = old.Manifest.Version
			default:
				return
			}
		}
		found[man.ID] = p
	}
	for _, d := range m.opt.BundledDirs {
		for _, sub := range subdirs(d) {
			consider(sub, "bundled")
		}
	}
	for _, sub := range subdirs(m.opt.UserDir) {
		consider(sub, "user")
	}
	for _, d := range m.opt.DevDirs {
		if sdk.FileExists(filepath.Join(d, "plugin.json")) {
			consider(d, "dev")
			continue
		}
		for _, sub := range subdirs(d) {
			if strings.HasPrefix(filepath.Base(sub), "lemon-paperplane-plugin-") {
				consider(sub, "dev")
			}
		}
	}
	disabled := map[string]bool{}
	if m.opt.Disabled != nil {
		for _, id := range m.opt.Disabled() {
			disabled[id] = true
		}
	}
	for _, p := range found {
		p.Enabled = !disabled[p.ID()]
		p.bundle, _ = sdk.LoadBundle(p.Dir, locale)
		if p.Manifest.Protocol != sdk.ProtocolVersion {
			p.Error = fmt.Sprintf("protocol %d not supported", p.Manifest.Protocol)
		} else if c := p.Manifest.Engines.Paperplane; c != "" && !sdk.MatchVersion(c, m.opt.HostVersion) {
			p.Error = fmt.Sprintf("requires paperplane %s", c)
		} else if p.Manifest.Declarative() {
			// 纯声明插件没有进程
		} else if exe, _ := p.command(); !fileExists(exe) {
			p.Error = "executable not found: " + p.Manifest.Executable
		}
	}
	// 依赖检查（缺失或出错的必需依赖使插件不可用）
	for _, p := range found {
		for _, d := range p.Manifest.Dependencies {
			dep := found[d.ID]
			if d.Optional {
				continue
			}
			if dep == nil || !dep.Enabled || dep.Error != "" {
				if p.Error == "" {
					p.Error = "missing dependency: " + d.ID
				}
			}
		}
	}
	m.mu.Lock()
	old := m.plugins
	m.plugins = found
	m.locale = locale
	m.order = topo(found)
	m.mu.Unlock()
	for id, op := range old {
		np := found[id]
		if np == nil || np.Dir != op.Dir || np.Manifest.Version != op.Manifest.Version {
			op.shutdown()
		} else {
			// 保留运行中的进程
			op.mu.Lock()
			np.mu.Lock()
			np.conn, np.cmd, np.lastUsed = op.conn, op.cmd, op.lastUsed
			np.mu.Unlock()
			op.mu.Unlock()
		}
	}
}

// SetLocale 切换文案语言。
func (m *Manager) SetLocale(locale string) {
	m.mu.Lock()
	m.locale = locale
	for _, p := range m.plugins {
		p.bundle, _ = sdk.LoadBundle(p.Dir, locale)
	}
	m.mu.Unlock()
}

func subdirs(d string) []string {
	if d == "" {
		return nil
	}
	entries, err := os.ReadDir(d)
	if err != nil {
		return nil
	}
	var out []string
	for _, e := range entries {
		if e.IsDir() || e.Type()&os.ModeSymlink != 0 {
			out = append(out, filepath.Join(d, e.Name()))
		}
	}
	sort.Strings(out)
	return out
}

func topo(ps map[string]*Plugin) []*Plugin {
	var ids []string
	for id := range ps {
		ids = append(ids, id)
	}
	sort.Strings(ids)
	var out []*Plugin
	state := map[string]int{}
	var visit func(id string)
	visit = func(id string) {
		p := ps[id]
		if p == nil || state[id] != 0 {
			return
		}
		state[id] = 1
		for _, d := range p.Manifest.Dependencies {
			visit(d.ID)
		}
		state[id] = 2
		out = append(out, p)
	}
	for _, id := range ids {
		visit(id)
	}
	return out
}

// All 返回全部插件（拓扑序）。
func (m *Manager) All() []*Plugin {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return append([]*Plugin{}, m.order...)
}

// Active 返回启用且无错误的插件（拓扑序）。
func (m *Manager) Active() []*Plugin {
	var out []*Plugin
	for _, p := range m.All() {
		if p.Enabled && p.Error == "" {
			out = append(out, p)
		}
	}
	return out
}

// Get 按 id 取插件。
func (m *Manager) Get(id string) *Plugin {
	m.mu.RLock()
	defer m.mu.RUnlock()
	return m.plugins[id]
}

// ActiveGet 取可用插件。
func (m *Manager) ActiveGet(id string) *Plugin {
	p := m.Get(id)
	if p == nil || !p.Enabled || p.Error != "" {
		return nil
	}
	return p
}

func (p *Plugin) executable() string {
	exe := filepath.Join(p.Dir, filepath.FromSlash(p.Manifest.Executable))
	if runtime.GOOS == "windows" && !strings.HasSuffix(strings.ToLower(exe), ".exe") {
		exe += ".exe"
	}
	if _, err := os.Stat(exe); err != nil {
		// 商店包布局：bin/<os>-<arch>/<名字>
		alt := filepath.Join(p.Dir, "bin", runtime.GOOS+"-"+runtime.GOARCH, filepath.Base(exe))
		if _, err2 := os.Stat(alt); err2 == nil {
			return alt
		}
	}
	return exe
}

// command 返回插件进程的可执行文件与参数。Windows 没有符号链接：内置插件的 bin 下放
// <名字>.bundled，内容是多入口程序 paperplane-plugins.exe 的相对路径，启动时把插件 id 作为参数传给它。
//
// 单文件版（Linux 无头版）的 .bundled 内容是 "@self"：插件编在内核可执行文件里，以 `<内核> __plugin <id>` 启动。
func (p *Plugin) command() (string, []string) {
	exe := p.executable()
	if !fileExists(exe) {
		base := strings.TrimSuffix(filepath.Join(p.Dir, filepath.FromSlash(p.Manifest.Executable)), ".exe")
		if b, err := os.ReadFile(base + ".bundled"); err == nil {
			target := strings.TrimSpace(string(b))
			if target == "@self" {
				if self, err := os.Executable(); err == nil {
					return self, []string{"__plugin", p.ID()}
				}
			}
			if runtime.GOOS == "windows" {
				return filepath.Clean(filepath.Join(filepath.Dir(base), target)), []string{p.ID()}
			}
		}
	}
	return exe, nil
}

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

// Call 调用插件方法（必要时拉起进程）。
func (m *Manager) Call(ctx context.Context, p *Plugin, method string, params, result any) error {
	if p == nil {
		return errors.New("plugin not available")
	}
	if p.Manifest.Declarative() {
		return sdk.ErrNotSupported
	}
	conn, err := m.ensure(p)
	if err != nil {
		return err
	}
	p.mu.Lock()
	p.inflight++
	p.lastUsed = time.Now()
	p.mu.Unlock()
	defer func() {
		p.mu.Lock()
		p.inflight--
		p.lastUsed = time.Now()
		p.mu.Unlock()
	}()
	return conn.Call(ctx, method, params, result)
}

func (m *Manager) ensure(p *Plugin) (*sdk.Conn, error) {
	if m.opt.EnvReady != nil {
		<-m.opt.EnvReady
	}
	p.mu.Lock()
	if p.conn != nil {
		c := p.conn
		p.mu.Unlock()
		return c, nil
	}
	if p.starting != nil {
		ch := p.starting
		p.mu.Unlock()
		<-ch
		p.mu.Lock()
		defer p.mu.Unlock()
		if p.conn == nil {
			if p.startErr != nil {
				return nil, p.startErr
			}
			return nil, errors.New("plugin failed to start")
		}
		return p.conn, nil
	}
	if p.Error != "" {
		p.mu.Unlock()
		return nil, errors.New(p.Error)
	}
	ch := make(chan struct{})
	p.starting = ch
	p.mu.Unlock()

	conn, cmd, exited, err := m.start(p)
	p.mu.Lock()
	p.conn, p.cmd, p.exited, p.startErr = conn, cmd, exited, err
	p.starting = nil
	p.lastUsed = time.Now()
	p.mu.Unlock()
	close(ch)
	return conn, err
}

func (m *Manager) start(p *Plugin) (*sdk.Conn, *exec.Cmd, chan struct{}, error) {
	exe, args := p.command()
	cmd := exec.Command(exe, args...)
	cmd.Dir = p.Dir
	cmd.Env = os.Environ()
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, nil, nil, err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, nil, nil, err
	}
	stderr, _ := cmd.StderrPipe()
	if err := cmd.Start(); err != nil {
		return nil, nil, nil, fmt.Errorf("start plugin %s: %w", p.ID(), err)
	}
	exited := make(chan struct{})
	go m.pipeLog(p, stderr)
	conn := sdk.NewConn(stdout, stdin, func(ctx context.Context, method string, params json.RawMessage) (any, error) {
		return m.opt.Host.HandleHost(ctx, p, method, params)
	})
	go func() {
		_ = conn.Run()
		_ = stdin.Close()
		// 只在这里 Wait：exec.Cmd.Wait 不能并发调用（以前 shutdown 也 Wait，闲置回收时会 panic 让整个内核退出）
		_ = cmd.Wait()
		close(exited)
		p.mu.Lock()
		if p.conn == conn {
			p.conn = nil
			p.cmd = nil
			p.exited = nil
		}
		p.mu.Unlock()
	}()
	ip := m.opt.InitParams(p)
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	var res sdk.InitializeResult
	if err := conn.Call(ctx, sdk.MethodInitialize, &ip, &res); err != nil {
		conn.Close()
		_ = cmd.Process.Kill()
		return nil, nil, nil, fmt.Errorf("initialize plugin %s: %w", p.ID(), err)
	}
	m.logf("plugin %s started (pid %d)", p.ID(), cmd.Process.Pid)
	return conn, cmd, exited, nil
}

func (m *Manager) pipeLog(p *Plugin, r io.Reader) {
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 64*1024), 1024*1024)
	for sc.Scan() {
		m.logf("[%s] %s", p.ID(), sc.Text())
	}
}

func (m *Manager) logf(format string, args ...any) {
	if m.opt.Logger != nil {
		m.opt.Logger.Printf(format, args...)
	}
}

func (p *Plugin) shutdown() {
	p.mu.Lock()
	conn, cmd, exited := p.conn, p.cmd, p.exited
	p.conn, p.cmd, p.exited = nil, nil, nil
	p.mu.Unlock()
	if conn == nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	_ = conn.Call(ctx, sdk.MethodShutdown, nil, nil)
	cancel()
	conn.Close()
	if cmd != nil && cmd.Process != nil && exited != nil {
		select {
		case <-exited:
		case <-time.After(3 * time.Second):
			_ = cmd.Process.Kill()
		}
	}
}

func (m *Manager) reaper() {
	t := time.NewTicker(10 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-m.stop:
			return
		case <-t.C:
		}
		idle := 60 * time.Second
		if m.opt.Idle != nil {
			idle = m.opt.Idle()
		}
		for _, p := range m.All() {
			p.mu.Lock()
			expired := p.conn != nil && p.inflight == 0 && time.Since(p.lastUsed) > idle
			p.mu.Unlock()
			if expired {
				m.logf("plugin %s idle, stopping", p.ID())
				go p.shutdown()
			}
		}
	}
}

// Shutdown 关闭全部插件进程。
func (m *Manager) Shutdown() {
	close(m.stop)
	var wg sync.WaitGroup
	for _, p := range m.All() {
		wg.Add(1)
		go func(p *Plugin) { defer wg.Done(); p.shutdown() }(p)
	}
	wg.Wait()
}

// StopPlugin 关闭单个插件进程。
func (m *Manager) StopPlugin(id string) {
	if p := m.Get(id); p != nil {
		p.shutdown()
	}
}

// StopIdle 停掉没有在处理请求的插件进程（下次调用时按新参数重新启动）。
func (m *Manager) StopIdle() {
	for _, p := range m.All() {
		p.mu.Lock()
		busy := p.inflight > 0
		p.mu.Unlock()
		if !busy {
			p.shutdown()
		}
	}
}

// RunningPIDs 返回活着的插件进程号。
func (m *Manager) RunningPIDs() []int {
	var out []int
	for _, p := range m.All() {
		if pid := p.PID(); pid > 0 {
			out = append(out, pid)
		}
	}
	return out
}
