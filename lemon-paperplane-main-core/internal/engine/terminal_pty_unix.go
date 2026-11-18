//go:build !windows

package engine

import (
	"os"
	"os/exec"
	"syscall"
	"unsafe"

	"github.com/creack/pty"
)

// unixPTY 是 macOS / Linux 的伪终端主端。shell 由 pty.Start 放进新会话（Setsid + 控制终端），
// 主端关闭时内核给前台进程组发 SIGHUP，daemon 异常退出也不会留下孤儿 shell。
type unixPTY struct{ f *os.File }

func startPTY(cmd *exec.Cmd, cols, rows int) (ptyHandle, error) {
	f, err := pty.StartWithSize(cmd, &pty.Winsize{Cols: uint16(cols), Rows: uint16(rows)})
	if err != nil {
		return nil, err
	}
	return &unixPTY{f: f}, nil
}

func (u *unixPTY) Read(b []byte) (int, error)  { return u.f.Read(b) }
func (u *unixPTY) Write(b []byte) (int, error) { return u.f.Write(b) }
func (u *unixPTY) Close() error                { return u.f.Close() }
func (u *unixPTY) Resize(cols, rows int) error {
	return pty.Setsize(u.f, &pty.Winsize{Cols: uint16(cols), Rows: uint16(rows)})
}

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
