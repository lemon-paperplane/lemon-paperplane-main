package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
)

// Host 是插件调用内核能力的客户端。
type Host struct {
	conn *Conn
}

// NewHost 用已有连接构造 Host（测试用）。
func NewHost(conn *Conn) *Host { return &Host{conn: conn} }

// Exec 让内核执行一个命令。Console 非空时输出实时进入对应运行/任务的控制台；
// 取消 ctx 会终止该命令（连同子进程）。
func (h *Host) Exec(ctx context.Context, p *ExecParams) (*ExecResult, error) {
	var r ExecResult
	if err := h.conn.Call(ctx, HostExec, p, &r); err != nil {
		return nil, err
	}
	return &r, nil
}

// ExecCapture 执行命令并捕获输出（不进控制台）。
func (h *Host) ExecCapture(ctx context.Context, cwd string, env map[string]string, command string, args ...string) (*ExecResult, error) {
	return h.Exec(ctx, &ExecParams{Command: command, Args: args, Cwd: cwd, Env: env, Capture: true})
}

// Console 向运行/任务控制台写一行系统信息。
func (h *Host) Console(console, text string) {
	if console == "" {
		return
	}
	_ = h.conn.Notify(HostConsole, &ConsoleParams{Console: console, Text: text, Stream: "system"})
}

// Progress 汇报任务进度；percent < 0 表示不确定。
func (h *Host) Progress(token, message string, percent float64) {
	if token == "" {
		return
	}
	_ = h.conn.Notify(HostProgress, &ProgressParams{Token: token, Message: message, Percent: percent})
}

// Log 写 daemon 日志。
func (h *Host) Log(level, format string, args ...any) {
	_ = h.conn.Notify(HostLog, &LogParams{Level: level, Message: fmt.Sprintf(format, args...)})
}

// Call 调用其他插件声明的服务（manifest contributes.services）。
func (h *Host) Call(ctx context.Context, plugin, method string, params any, result any) error {
	raw, err := json.Marshal(params)
	if err != nil {
		return err
	}
	return h.conn.Call(ctx, HostCall, &HostCallParams{Plugin: plugin, Method: method, Params: raw}, result)
}

// Toolchains 列出内核已知的某类工具链（所有插件发现的 + 已下载的）。
func (h *Host) Toolchains(ctx context.Context, kind string) ([]Toolchain, error) {
	var r HostToolchainsResult
	if err := h.conn.Call(ctx, HostToolchains, &HostToolchainsParams{Kind: kind}, &r); err != nil {
		return nil, err
	}
	return r.Toolchains, nil
}

// Download 让内核下载（并可解压）文件，带进度、校验与镜像回退。
func (h *Host) Download(ctx context.Context, p *DownloadParams) (*DownloadResult, error) {
	var r DownloadResult
	if err := h.conn.Call(ctx, HostDownload, p, &r); err != nil {
		return nil, err
	}
	return &r, nil
}

// Notify 在界面弹出通知。
func (h *Host) Notify(level, title, message string) {
	_ = h.conn.Notify(HostNotify, &NotifyParams{Level: level, Title: title, Message: message})
}

// Prompt 在界面弹出对话框向用户要输入或确认；没有界面连接时返回 Cancelled。
func (h *Host) Prompt(ctx context.Context, p *PromptParams) (*PromptResult, error) {
	var r PromptResult
	if err := h.conn.Call(ctx, HostPrompt, p, &r); err != nil {
		return nil, err
	}
	return &r, nil
}

// ExecError 把非零退出的 ExecResult 包装成错误，附带输出末尾。
func ExecError(what string, r *ExecResult) error {
	if r == nil {
		return fmt.Errorf("%s failed", what)
	}
	if r.Cancelled {
		return NewError(ErrCodeCancelled, "%s cancelled", what)
	}
	tail := strings.TrimSpace(r.Stderr)
	if tail == "" {
		tail = strings.TrimSpace(r.Stdout)
	}
	if len(tail) > 2000 {
		tail = tail[len(tail)-2000:]
	}
	if tail != "" {
		return fmt.Errorf("%s exited with %d: %s", what, r.ExitCode, tail)
	}
	return fmt.Errorf("%s exited with %d", what, r.ExitCode)
}
