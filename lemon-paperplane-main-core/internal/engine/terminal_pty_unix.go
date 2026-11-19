//go:build !windows

package engine

import (
	"errors"
	"os"
	"os/exec"
	"syscall"
	"unsafe"

	"github.com/creack/pty"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
)

// unixPTY 是 macOS / Linux 的伪终端主端。shell 由 pty.Start 放进新会话（Setsid + 控制终端），
// 主端关闭时内核给前台进程组发 SIGHUP，daemon 异常退出也不会留下孤儿 shell。
type unixPTY struct {
	f   *os.File
	pid int
}

type unixProc struct{ cmd *exec.Cmd }

func (u unixProc) Pid() int { return u.cmd.Process.Pid }

func (u unixProc) Wait() (int, error) {
	err := u.cmd.Wait()
	if err == nil {
		return 0, nil
	}
	var ee *exec.ExitError
	if errors.As(err, &ee) {
		code := ee.ExitCode()
		if code < 0 {
			code = 128 + 1 // 被信号结束（多为挂断）
		}
		return code, nil
	}
	return -1, err
}

func startPTY(shell string, args []string, dir string, env []string, cols, rows int) (ptyHandle, termProc, error) {
	cmd := exec.Command(shell, args...)
	cmd.Dir = dir
	cmd.Env = env
	f, err := pty.StartWithSize(cmd, &pty.Winsize{Cols: uint16(cols), Rows: uint16(rows)})
	if err != nil {
		return nil, nil, err
	}
	return &unixPTY{f: f, pid: cmd.Process.Pid}, unixProc{cmd}, nil
}

func (u *unixPTY) Read(b []byte) (int, error)  { return u.f.Read(b) }
func (u *unixPTY) Write(b []byte) (int, error) { return u.f.Write(b) }
func (u *unixPTY) Close() error                { return u.f.Close() }
func (u *unixPTY) Resize(cols, rows int) error {
	return pty.Setsize(u.f, &pty.Winsize{Cols: uint16(cols), Rows: uint16(rows)})
}
func (u *unixPTY) Hangup() { _ = proc.SignalGroup(u.pid, "SIGHUP") }

// Foreground 返回终端前台进程组（shell 自己在等输入时就是 shell 的进程组）。
// 用 SyscallConn 而不是 Fd()：Fd() 会把文件切成阻塞模式，读协程就没法被 Close 打断了。
func (u *unixPTY) Foreground() int {
	rc, err := u.f.SyscallConn()
	if err != nil {
		return 0
	}
	var pgrp int32
	_ = rc.Control(func(fd uintptr) {
		_, _, errno := syscall.Syscall(syscall.SYS_IOCTL, fd, uintptr(syscall.TIOCGPGRP), uintptr(unsafe.Pointer(&pgrp)))
		if errno != 0 {
			pgrp = 0
		}
	})
	return int(pgrp)
}

func terminalSupported() bool { return true }

// defaultShell 用 $SHELL，没有或不可执行时依次找 zsh、bash、sh。
func defaultShell(env map[string]string) string {
	shell := env["SHELL"]
	if shell == "" || !isExecutable(shell) {
		shell = ""
		for _, c := range []string{"/bin/zsh", "/bin/bash", "/bin/sh"} {
			if isExecutable(c) {
				shell = c
				break
			}
		}
		env["SHELL"] = shell
	}
	return shell
}

// shellArgs：和系统终端一样以登录 shell 启动。
func shellArgs(shell string) []string { return []string{"-l"} }
