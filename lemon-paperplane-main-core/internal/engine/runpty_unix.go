//go:build !windows

package engine

import (
	"os"
	"os/exec"

	"github.com/creack/pty"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/console"
)

// startRunPTY 在伪终端里启动运行进程（LaunchSpec.Terminal）。pty 把它放进新会话并设为控制终端，
// 进程组号就是 pid，停止 / 清理照旧按进程组发信号。返回伪终端主端：读输出、写输入。
func startRunPTY(cmd *exec.Cmd) (*os.File, error) {
	cmd.SysProcAttr = nil // 不能带 Setpgid：会话首进程再设进程组会失败
	return pty.StartWithSize(cmd, &pty.Winsize{Cols: console.TermCols, Rows: console.TermRows})
}

func runPTYSupported() bool { return true }
