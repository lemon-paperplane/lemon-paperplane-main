package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime/debug"
	"sync"
	"time"
)

// 插件按需实现下面的接口；Serve 根据插件实现了哪些接口分发请求，
// 没实现的方法返回 ErrNotSupported。

type Detector interface {
	Detect(ctx *Context, p *DetectParams) (*DetectResult, error)
}

type Analyzer interface {
	Analyze(ctx *Context, p *AnalyzeParams) (*AnalyzeResult, error)
}

// Syncer 实现重型同步（如 Gradle 模型导入），manifest 里需声明 contributes.sync=true。
type Syncer interface {
	Sync(ctx *Context, p *AnalyzeParams) (*AnalyzeResult, error)
}

type DependencyInstaller interface {
	InstallDependencies(ctx *Context, p *InstallDepsParams) (*InstallDepsResult, error)
}

// OptionsProvider 实时提供表单下拉选项（manifest 里 live 的 optionsFrom，如已连接设备）。
type OptionsProvider interface {
	Options(ctx *Context, p *OptionsParams) (*OptionsResult, error)
}

type RunPreparer interface {
	PrepareRun(ctx *Context, p *PrepareRunParams) (*LaunchSpec, error)
}

type Diagnoser interface {
	Diagnose(ctx *Context, p *DiagnoseParams) (*DiagnoseResult, error)
}

type FixApplier interface {
	ApplyFix(ctx *Context, p *ApplyFixParams) (*ApplyFixResult, error)
}

type ActionInvoker interface {
	InvokeAction(ctx *Context, p *InvokeActionParams) (*InvokeActionResult, error)
}

type ToolchainProvider interface {
	DiscoverToolchains(ctx *Context, p *DiscoverToolchainsParams) (*DiscoverToolchainsResult, error)
	AvailableToolchains(ctx *Context, p *AvailableToolchainsParams) (*AvailableToolchainsResult, error)
	InstallToolchain(ctx *Context, p *InstallToolchainParams) (*InstallToolchainResult, error)
}

// ServiceProvider 处理其他插件经 host/call 发来的调用。
type ServiceProvider interface {
	CallService(ctx *Context, method string, params json.RawMessage) (any, error)
}

// VCSProvider 处理 vcs/call。
type VCSProvider interface {
	VCS(ctx *Context, p *VCSCallParams) (any, error)
}

// ViewProvider 渲染插件贡献的界面（manifest contributes.views）并处理界面事件。
type ViewProvider interface {
	RenderView(ctx *Context, p *ViewRenderParams) (*ViewRenderResult, error)
	HandleViewEvent(ctx *Context, p *ViewEventParams) (*ViewEventResult, error)
}

// ToolProvider 执行插件声明的工具（manifest contributes.tools），这些工具同时暴露为 API、MCP 和 CLI。
type ToolProvider interface {
	CallTool(ctx *Context, p *ToolCallParams) (any, error)
}

// Initializer 可选：初始化时被调用一次。
// Runner 是插件自带的「运行器」：内核托管的进程就是插件可执行文件本身，
// 命令行为 `<插件可执行文件> __run <参数...>`（见 RunnerCommand）。用于需要多步、要在停止时收尾的运行，
// 例如安卓：启动主界面 → 跟踪该进程的 logcat → 应用退出即结束 → 收到停止信号时 force-stop。
// RunMain 在独立进程里执行，返回进程退出码；不经过 JSON-RPC。
type Runner interface {
	RunMain(args []string) int
}

// RunnerArg 是运行器子命令的标记参数。
const RunnerArg = "__run"

// RunnerCommand 返回启动本插件运行器的命令（用于 LaunchSpec.Command / Args）。
func RunnerCommand(args ...string) (string, []string) {
	return SelfExecutable(), append([]string{RunnerArg}, args...)
}

// SelfExecutable 返回本插件可执行文件的路径。内置插件是多入口二进制的同名链接，
// 必须用启动时的路径（链接名决定运行哪个插件），不能解析链接。
func SelfExecutable() string {
	if a := os.Args[0]; filepath.IsAbs(a) {
		return a
	}
	if exe, err := os.Executable(); err == nil {
		return exe
	}
	return os.Args[0]
}

type Initializer interface {
	Initialize(ctx *Context) error
}

// Context 是每次请求的上下文。它嵌入 context.Context（请求被取消时 Done），
// 并提供调用内核的 Host、初始化信息与本地化。
type Context struct {
	context.Context
	Host *Host
	Init *InitializeParams
	l10n *Bundle
}

// T 取插件 l10n 里的文案，支持 {0}{1}… 占位。找不到 key 时返回 key 本身。
func (c *Context) T(key string, args ...any) string {
	return c.l10n.T(key, args...)
}

// Locale 返回当前界面语言。
func (c *Context) Locale() string {
	if c.Init == nil {
		return "en"
	}
	return c.Init.Locale
}

// Setting 读取插件设置项，不存在返回 def。
func (c *Context) Setting(key string, def any) any {
	if c.Init != nil && c.Init.Settings != nil {
		if v, ok := c.Init.Settings[key]; ok {
			return v
		}
	}
	return def
}

type server struct {
	plugin any
	conn   *Conn
	host   *Host

	relMu    sync.Mutex
	relTimer *time.Timer

	mu   sync.RWMutex
	init *InitializeParams
	l10n *Bundle
}

// Serve 在 stdin/stdout 上运行插件，直到内核关闭连接或发送 shutdown。
// 插件的日志请写 stderr 或 ctx.Host.Log，不要写 stdout。
func Serve(plugin any) {
	if len(os.Args) > 1 && os.Args[1] == RunnerArg {
		r, ok := plugin.(Runner)
		if !ok {
			fmt.Fprintln(os.Stderr, "plugin has no runner")
			os.Exit(2)
		}
		os.Exit(r.RunMain(os.Args[2:]))
	}
	if err := ServeIO(plugin, os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, "plugin exited:", err)
		os.Exit(1)
	}
}

// ServeIO 在指定的读写端上运行插件（测试与内置多入口二进制使用）。
func ServeIO(plugin any, r io.Reader, w io.Writer) error {
	// 插件是短命、按需的进程：GC 更积极，请求结束后把内存还给系统，避免一次大解析后常驻
	debug.SetGCPercent(50)
	debug.SetMemoryLimit(48 << 20)
	s := &server{plugin: plugin, l10n: &Bundle{}}
	s.conn = NewConn(r, w, func(c context.Context, method string, params json.RawMessage) (any, error) {
		defer s.releaseLater()
		return s.handle(c, method, params)
	})
	s.host = &Host{conn: s.conn}
	return s.conn.Run()
}

func (s *server) ctx(c context.Context) *Context {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return &Context{Context: c, Host: s.host, Init: s.init, l10n: s.l10n}
}

func decode[T any](raw json.RawMessage) (*T, error) {
	var v T
	if len(raw) > 0 {
		if err := json.Unmarshal(raw, &v); err != nil {
			return nil, NewError(ErrCodeInvalidParams, "%v", err)
		}
	}
	return &v, nil
}

func (s *server) handle(c context.Context, method string, raw json.RawMessage) (any, error) {
	switch method {
	case MethodInitialize:
		p, err := decode[InitializeParams](raw)
		if err != nil {
			return nil, err
		}
		b, _ := LoadBundle(p.PluginDir, p.Locale)
		s.mu.Lock()
		s.init = p
		if b != nil {
			s.l10n = b
		}
		s.mu.Unlock()
		if i, ok := s.plugin.(Initializer); ok {
			if err := i.Initialize(s.ctx(c)); err != nil {
				return nil, err
			}
		}
		return &InitializeResult{ProtocolVersion: ProtocolVersion}, nil
	case MethodShutdown:
		go func() { s.conn.Close() }()
		return nil, nil
	case MethodDetect:
		if h, ok := s.plugin.(Detector); ok {
			p, err := decode[DetectParams](raw)
			if err != nil {
				return nil, err
			}
			return h.Detect(s.ctx(c), p)
		}
	case MethodAnalyze:
		if h, ok := s.plugin.(Analyzer); ok {
			p, err := decode[AnalyzeParams](raw)
			if err != nil {
				return nil, err
			}
			return h.Analyze(s.ctx(c), p)
		}
	case MethodSync:
		if h, ok := s.plugin.(Syncer); ok {
			p, err := decode[AnalyzeParams](raw)
			if err != nil {
				return nil, err
			}
			return h.Sync(s.ctx(c), p)
		}
	case MethodInstallDeps:
		if h, ok := s.plugin.(DependencyInstaller); ok {
			p, err := decode[InstallDepsParams](raw)
			if err != nil {
				return nil, err
			}
			return h.InstallDependencies(s.ctx(c), p)
		}
	case MethodOptions:
		if h, ok := s.plugin.(OptionsProvider); ok {
			p, err := decode[OptionsParams](raw)
			if err != nil {
				return nil, err
			}
			return h.Options(s.ctx(c), p)
		}
	case MethodPrepareRun:
		if h, ok := s.plugin.(RunPreparer); ok {
			p, err := decode[PrepareRunParams](raw)
			if err != nil {
				return nil, err
			}
			return h.PrepareRun(s.ctx(c), p)
		}
	case MethodDiagnose:
		if h, ok := s.plugin.(Diagnoser); ok {
			p, err := decode[DiagnoseParams](raw)
			if err != nil {
				return nil, err
			}
			return h.Diagnose(s.ctx(c), p)
		}
	case MethodApplyFix:
		if h, ok := s.plugin.(FixApplier); ok {
			p, err := decode[ApplyFixParams](raw)
			if err != nil {
				return nil, err
			}
			return h.ApplyFix(s.ctx(c), p)
		}
	case MethodInvokeAction:
		if h, ok := s.plugin.(ActionInvoker); ok {
			p, err := decode[InvokeActionParams](raw)
			if err != nil {
				return nil, err
			}
			return h.InvokeAction(s.ctx(c), p)
		}
	case MethodDiscoverToolchains:
		if h, ok := s.plugin.(ToolchainProvider); ok {
			p, err := decode[DiscoverToolchainsParams](raw)
			if err != nil {
				return nil, err
			}
			return h.DiscoverToolchains(s.ctx(c), p)
		}
	case MethodAvailableToolchain:
		if h, ok := s.plugin.(ToolchainProvider); ok {
			p, err := decode[AvailableToolchainsParams](raw)
			if err != nil {
				return nil, err
			}
			return h.AvailableToolchains(s.ctx(c), p)
		}
	case MethodInstallToolchain:
		if h, ok := s.plugin.(ToolchainProvider); ok {
			p, err := decode[InstallToolchainParams](raw)
			if err != nil {
				return nil, err
			}
			return h.InstallToolchain(s.ctx(c), p)
		}
	case MethodServiceCall:
		if h, ok := s.plugin.(ServiceProvider); ok {
			p, err := decode[ServiceCallParams](raw)
			if err != nil {
				return nil, err
			}
			return h.CallService(s.ctx(c), p.Method, p.Params)
		}
	case MethodVCS:
		if h, ok := s.plugin.(VCSProvider); ok {
			p, err := decode[VCSCallParams](raw)
			if err != nil {
				return nil, err
			}
			return h.VCS(s.ctx(c), p)
		}
	case MethodViewRender:
		if h, ok := s.plugin.(ViewProvider); ok {
			p, err := decode[ViewRenderParams](raw)
			if err != nil {
				return nil, err
			}
			return h.RenderView(s.ctx(c), p)
		}
	case MethodViewEvent:
		if h, ok := s.plugin.(ViewProvider); ok {
			p, err := decode[ViewEventParams](raw)
			if err != nil {
				return nil, err
			}
			return h.HandleViewEvent(s.ctx(c), p)
		}
	case MethodToolCall:
		if h, ok := s.plugin.(ToolProvider); ok {
			p, err := decode[ToolCallParams](raw)
			if err != nil {
				return nil, err
			}
			return h.CallTool(s.ctx(c), p)
		}
	default:
		return nil, NewError(ErrCodeMethodNotFound, "method not found: %s", method)
	}
	return nil, ErrNotSupported
}

// releaseLater 在最后一个请求结束 1.5 秒后归还空闲内存。
func (s *server) releaseLater() {
	s.relMu.Lock()
	defer s.relMu.Unlock()
	if s.relTimer != nil {
		s.relTimer.Stop()
	}
	s.relTimer = time.AfterFunc(1500*time.Millisecond, debug.FreeOSMemory)
}
