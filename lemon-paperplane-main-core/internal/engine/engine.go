// Package engine 是内核业务逻辑：导入识别、工具链、依赖、运行托管、诊断修复、版本控制路由、插件界面与工具。
package engine

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/events"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/mirrors"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/model"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/paths"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/plugins"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/store"
	sdk "github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-plugin-sdk"
)

// UserError 是可直接展示给用户的错误。
type UserError struct{ Msg string }

func (e *UserError) Error() string { return e.Msg }

// Errf 构造本地化的用户错误。
func Errf(key string, args ...any) error { return &UserError{Msg: i18n.T(key, args...)} }

// ErrNotFound 表示对象不存在。
var ErrNotFound = errors.New("not found")

// Config 是 Engine 的启动参数。
type Config struct {
	Version     string
	Layout      *paths.Layout
	BundledDirs []string
	DevDirs     []string
	// BundledStore 是随 App 附带的离线商店索引（index.json 的路径）；设置里没填商店地址时使用
	BundledStore string
	Executable   string
	Logger       *log.Logger
	// EnvReady 在登录 shell 的环境变量读完后关闭；之前不启动插件进程
	EnvReady <-chan struct{}
}

// Engine 是内核。
type Engine struct {
	cfg     Config
	Layout  *paths.Layout
	Store   *store.Store
	Bus     *events.Bus
	Plugins *plugins.Manager
	Log     *log.Logger

	startedAt time.Time
	apiBase   string
	token     string
	askpass   string

	mirrorDet mirrors.Detector
	mirMu     sync.RWMutex
	mir       sdk.Mirrors

	mu        sync.Mutex
	runs      map[string]*runState
	runOrder  []string
	tasks     map[string]*taskState
	taskOrder []string
	consoles  map[string]*console.Console
	execs     map[string]map[*execHandle]struct{} // consoleID → 进行中的 host/exec
	prompts   map[string]*promptState
	vcsBrief  map[string]*model.VCSBrief
	busy      map[string]string // projectId → 当前任务标题

	tcMu    sync.Mutex
	tcCache map[string][]sdk.Toolchain
	tcAt    map[string]time.Time

	rulesMu sync.Mutex
	rules   map[string]*compiledRules // pluginID@version

	stopCh chan struct{}
}

// New 创建内核（不启动 HTTP）。
func New(cfg Config) (*Engine, error) {
	st, err := store.Open(cfg.Layout)
	if err != nil {
		return nil, err
	}
	e := &Engine{
		cfg: cfg, Layout: cfg.Layout, Store: st, Bus: events.New(), Log: cfg.Logger,
		startedAt: time.Now(),
		runs:      map[string]*runState{}, tasks: map[string]*taskState{}, consoles: map[string]*console.Console{},
		execs: map[string]map[*execHandle]struct{}{}, prompts: map[string]*promptState{},
		vcsBrief: map[string]*model.VCSBrief{}, busy: map[string]string{},
		tcCache: map[string][]sdk.Toolchain{}, tcAt: map[string]time.Time{},
		rules: map[string]*compiledRules{}, stopCh: make(chan struct{}),
	}
	settings := st.Settings()
	i18n.SetLocale(settings.Locale)
	e.Plugins = plugins.New(plugins.Options{
		HostVersion: cfg.Version,
		BundledDirs: cfg.BundledDirs,
		UserDir:     cfg.Layout.Plugins,
		DevDirs:     cfg.DevDirs,
		Host:        e,
		InitParams:  e.initParams,
		Idle: func() time.Duration {
			s := e.Store.Settings().PluginIdleSeconds
			if s <= 0 {
				s = 60
			}
			return time.Duration(s) * time.Second
		},
		Disabled: func() []string { return e.Store.Settings().DisabledPlugins },
		Logger:   cfg.Logger,
		EnvReady: cfg.EnvReady,
	})
	e.Plugins.Reload(i18n.Locale())
	e.startMirrors() // 插件是按需启动的，第一次启动时才取镜像
	isFramework = func(id string) bool {
		p := e.Plugins.Get(id)
		return p != nil && p.Manifest.Category == "framework"
	}
	e.killOrphans()
	e.writeAskpass()
	go e.statsLoop()
	go e.vcsLoop()
	return e, nil
}

// SetAPI 记录 API 地址与 token（插件 initialize 与 askpass 用）。
func (e *Engine) SetAPI(base, token string) {
	e.apiBase, e.token = base, token
}

// APIBase 返回 API 地址。
func (e *Engine) APIBase() string { return e.apiBase }

// Executable 返回内核可执行文件路径。
func (e *Engine) Executable() string { return e.cfg.Executable }

// Version 返回内核版本。
func (e *Engine) Version() string { return e.cfg.Version }

// StartedAt 返回启动时间。
func (e *Engine) StartedAt() time.Time { return e.startedAt }

func (e *Engine) logf(format string, args ...any) {
	if e.Log != nil {
		e.Log.Printf(format, args...)
	}
}

// Close 停止后台循环与插件。stopRuns 为 true 时停止所有运行。
func (e *Engine) Close(stopRuns bool) {
	if stopRuns {
		e.StopAll("", "")
		deadline := time.Now().Add(10 * time.Second)
		for time.Now().Before(deadline) && e.activeRunCount() > 0 {
			time.Sleep(200 * time.Millisecond)
		}
	}
	select {
	case <-e.stopCh:
	default:
		close(e.stopCh)
	}
	e.Plugins.Shutdown()
	e.saveActivePIDs()
}

// ---------------------------------------------------------------------------
// 镜像

func (e *Engine) applyMirrors(ctx context.Context, force bool) {
	region := e.Store.Settings().MirrorRegion
	if region == "auto" || region == "" {
		r, _ := e.mirrorDet.Region(ctx, force)
		region = r
		e.saveMirrorCache()
	}
	e.setRegion(region)
}

// mirrorCacheFile 保存上次测速结果，下次启动直接用（24 小时内有效）。
func (e *Engine) mirrorCacheFile() string { return filepath.Join(e.Layout.Cache, "mirror-region.json") }

type mirrorCache struct {
	Region string          `json:"region"`
	Probes []mirrors.Probe `json:"probes"`
	At     time.Time       `json:"at"`
}

func (e *Engine) saveMirrorCache() {
	r, probes, at := e.mirrorDet.Snapshot()
	if r == "" {
		return
	}
	if b, err := json.Marshal(mirrorCache{r, probes, at}); err == nil {
		_ = os.MkdirAll(e.Layout.Cache, 0o755)
		_ = os.WriteFile(e.mirrorCacheFile(), b, 0o644)
	}
}

// startMirrors 在启动时决定镜像，不等网络测速（测速最多要 3 秒，会拖慢 App 打开）：
// 手动指定区域直接用；auto 时先用上次的测速结果（24 小时内），没有就按时区先猜，后台测速，
// 结果与猜测不同则换镜像并重启空闲的插件进程（插件在 initialize 时拿到镜像）。
func (e *Engine) startMirrors() {
	if r := e.Store.Settings().MirrorRegion; r != "auto" && r != "" {
		e.setRegion(r)
		return
	}
	var c mirrorCache
	if sdk.ReadJSON(e.mirrorCacheFile(), &c) == nil && c.Region != "" && time.Since(c.At) < 24*time.Hour {
		e.mirrorDet.Seed(c.Region, c.Probes, c.At)
		e.setRegion(c.Region)
		return
	}
	guess := "global"
	if mirrors.InChina() {
		guess = "china"
	}
	e.setRegion(guess)
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		r, _ := e.mirrorDet.Region(ctx, false)
		e.saveMirrorCache()
		if s := e.Store.Settings().MirrorRegion; (s == "auto" || s == "") && r != guess {
			e.setRegion(r)
			e.Plugins.StopIdle()
		}
	}()
}

func (e *Engine) setRegion(region string) {
	m := mirrors.Global()
	if region == "china" {
		m = mirrors.China()
	}
	e.mirMu.Lock()
	e.mir = m
	e.mirMu.Unlock()
}

// Mirrors 返回当前生效的镜像。
func (e *Engine) Mirrors() sdk.Mirrors {
	e.mirMu.RLock()
	defer e.mirMu.RUnlock()
	return e.mir
}

// MirrorStatus 返回镜像状态。
func (e *Engine) MirrorStatus(ctx context.Context, probe bool) mirrors.Status {
	if probe {
		e.applyMirrors(ctx, true)
		e.restartPlugins()
	}
	region, probes := e.mirrorDet.Region(ctx, false)
	cfg := e.Store.Settings().MirrorRegion
	eff := cfg
	if cfg == "auto" || cfg == "" {
		eff = region
	}
	return mirrors.Status{Configured: cfg, Effective: eff, Probes: probes, At: time.Now()}
}

func (e *Engine) restartPlugins() {
	for _, p := range e.Plugins.All() {
		e.Plugins.StopPlugin(p.ID())
	}
}

// ---------------------------------------------------------------------------
// 设置

// UpdateSettings 应用设置变更并处理副作用。
func (e *Engine) UpdateSettings(patch json.RawMessage) (model.Settings, error) {
	before := e.Store.Settings()
	s, err := e.Store.UpdateSettings(patch)
	if err != nil {
		return s, err
	}
	if s.Locale != before.Locale {
		loc := i18n.SetLocale(s.Locale)
		e.Plugins.SetLocale(loc)
		e.restartPlugins()
		e.Bus.Publish(model.EvPlugins, nil)
	}
	if s.MirrorRegion != before.MirrorRegion {
		go func() {
			e.applyMirrors(context.Background(), false)
			e.restartPlugins()
		}()
	}
	if !equalJSON(s.PluginSettings, before.PluginSettings) || !equalJSON(s.DisabledPlugins, before.DisabledPlugins) {
		e.Plugins.Reload(i18n.Locale())
		e.restartPlugins()
		e.Bus.Publish(model.EvPlugins, nil)
	}
	e.Bus.Publish(model.EvSettings, s)
	return s, nil
}

func equalJSON(a, b any) bool {
	x, _ := json.Marshal(a)
	y, _ := json.Marshal(b)
	return string(x) == string(y)
}

// ---------------------------------------------------------------------------
// 插件初始化参数

func (e *Engine) initParams(p *plugins.Plugin) sdk.InitializeParams {
	data := filepath.Join(e.Layout.PluginData, p.ID())
	cache := filepath.Join(e.Layout.Cache, "plugins", p.ID())
	_ = os.MkdirAll(data, 0o755)
	_ = os.MkdirAll(cache, 0o755)
	s := e.Store.Settings()
	return sdk.InitializeParams{
		ProtocolVersion: sdk.ProtocolVersion,
		HostVersion:     e.cfg.Version,
		Locale:          i18n.Locale(),
		OS:              runtime.GOOS,
		Arch:            runtime.GOARCH,
		PluginDir:       p.Dir,
		DataDir:         data,
		CacheDir:        cache,
		HostDataDir:     e.Layout.Root,
		HostExecutable:  e.cfg.Executable,
		AskpassPath:     e.askpass,
		APIBase:         e.apiBase,
		Mirrors:         e.Mirrors(),
		Settings:        e.pluginSettings(p, s),
	}
}

func (e *Engine) pluginSettings(p *plugins.Plugin, s model.Settings) map[string]any {
	out := map[string]any{}
	for _, f := range p.Manifest.Contributes.Settings {
		if f.Default != nil {
			out[f.Key] = f.Default
		}
	}
	for k, v := range s.PluginSettings[p.ID()] {
		out[k] = v
	}
	return out
}

// writeAskpass 生成 git/ssh 用的 askpass 包装脚本。
func (e *Engine) writeAskpass() {
	if e.cfg.Executable == "" {
		return
	}
	if runtime.GOOS == "windows" {
		p := filepath.Join(e.Layout.Bin, "paperplane-askpass.cmd")
		_ = os.WriteFile(p, []byte("@echo off\r\n\""+e.cfg.Executable+"\" askpass %*\r\n"), 0o755)
		e.askpass = p
		return
	}
	p := filepath.Join(e.Layout.Bin, "paperplane-askpass")
	script := "#!/bin/sh\nexec \"" + e.cfg.Executable + "\" askpass \"$@\"\n"
	if err := os.WriteFile(p, []byte(script), 0o755); err == nil {
		e.askpass = p
	}
}

// ---------------------------------------------------------------------------
// 杂项工具

func (e *Engine) projectInfo(p *model.Project) sdk.ProjectInfo {
	return sdk.ProjectInfo{ID: p.ID, Name: p.Name, Root: p.Path}
}

func (e *Engine) moduleInfo(p *model.Project, m *model.Module) sdk.ModuleInfo {
	return sdk.ModuleInfo{ID: m.ID, Name: m.Name, Dir: filepath.Join(p.Path, filepath.FromSlash(m.RelDir)), RelDir: m.RelDir, Facets: m.Facets, Models: m.Models}
}

func findModule(p *model.Project, id string) *model.Module {
	for _, m := range p.Modules {
		if m.ID == id {
			return m
		}
	}
	return nil
}

func findConfig(p *model.Project, id string) (*model.Module, *model.RunConfig) {
	for _, m := range p.Modules {
		for _, c := range m.RunConfigs {
			if c.ID == id {
				return m, c
			}
		}
	}
	return nil, nil
}

// participates 判断插件是否参与该模块（贡献了 facet，或由 facet 激活）。
func participates(p *plugins.Plugin, m *model.Module) bool {
	for _, f := range m.Facets {
		if f.Plugin == p.ID() {
			return true
		}
		for _, af := range p.Manifest.Activation.Facets {
			if af == f.ID {
				return true
			}
		}
	}
	return false
}

func (e *Engine) moduleToolchains(m *model.Module) map[string]sdk.Toolchain {
	out := map[string]sdk.Toolchain{}
	for kind, sel := range m.Toolchains {
		if sel.ToolchainID == "" {
			continue
		}
		if t, ok := e.toolchainByID(sel.ToolchainID); ok {
			out[kind] = t
		}
	}
	return out
}

func sortStrings(xs []string) []string {
	sort.Strings(xs)
	return xs
}

func shortHome(p string) string {
	home, _ := os.UserHomeDir()
	if home != "" && strings.HasPrefix(p, home) {
		return "~" + p[len(home):]
	}
	return p
}

func ptrTime(t time.Time) *time.Time { return &t }

func errString(err error) string {
	if err == nil {
		return ""
	}
	var ue *UserError
	if errors.As(err, &ue) {
		return ue.Msg
	}
	var re *sdk.Error
	if errors.As(err, &re) {
		return re.Message
	}
	return err.Error()
}

var _ = fmt.Sprintf

// Token 返回 API token（MCP 配置展示用）。
func (e *Engine) Token() string { return e.token }

// Workspaces 返回工作区列表；未命名的默认工作区按当前语言显示名字。
func (e *Engine) Workspaces() []model.Workspace {
	ws := e.Store.Workspaces()
	for i := range ws {
		if ws[i].Name == "" || ws[i].Name == "workspace.default" {
			ws[i].Name = i18n.T("workspace.default")
		}
	}
	return ws
}
