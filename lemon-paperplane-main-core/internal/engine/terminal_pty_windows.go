//go:build windows

package engine

import (
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"unicode/utf16"
	"unsafe"

	"golang.org/x/sys/windows"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
)

// Windows 终端走 ConPTY（Windows 10 1809 起）：shell 挂在伪控制台上，输出是 VT 序列，xterm.js 直接显示。
// shell 和它启动的命令放进 Job（关闭终端或 daemon 退出时整棵树结束）。

type conPTY struct {
	hpc       windows.Handle
	in        *os.File // 写给 shell 的输入
	out       *os.File // shell 的输出
	pid       int
	closeOnce sync.Once
}

type conProc struct {
	h   windows.Handle
	pid int
}

func (c *conProc) Pid() int { return c.pid }

func (c *conProc) Wait() (int, error) {
	defer windows.CloseHandle(c.h)
	if _, err := windows.WaitForSingleObject(c.h, windows.INFINITE); err != nil {
		return -1, err
	}
	var code uint32
	if err := windows.GetExitCodeProcess(c.h, &code); err != nil {
		return -1, err
	}
	return int(int32(code)), nil
}

func startPTY(shell string, args []string, dir string, env []string, cols, rows int) (ptyHandle, termProc, error) {
	var inR, inW, outR, outW windows.Handle
	if err := windows.CreatePipe(&inR, &inW, nil, 0); err != nil {
		return nil, nil, err
	}
	if err := windows.CreatePipe(&outR, &outW, nil, 0); err != nil {
		windows.CloseHandle(inR)
		windows.CloseHandle(inW)
		return nil, nil, err
	}
	var hpc windows.Handle
	if err := windows.CreatePseudoConsole(windows.Coord{X: int16(cols), Y: int16(rows)}, inR, outW, 0, &hpc); err != nil {
		for _, h := range []windows.Handle{inR, inW, outR, outW} {
			windows.CloseHandle(h)
		}
		return nil, nil, err
	}
	// 伪控制台已经持有这两端的副本
	windows.CloseHandle(inR)
	windows.CloseHandle(outW)
	fail := func(err error) (ptyHandle, termProc, error) {
		windows.ClosePseudoConsole(hpc)
		windows.CloseHandle(inW)
		windows.CloseHandle(outR)
		return nil, nil, err
	}

	attrs, err := windows.NewProcThreadAttributeList(1)
	if err != nil {
		return fail(err)
	}
	defer attrs.Delete()
	// 这个属性的值就是 HPCON 本身（不是指向它的指针）
	if err := attrs.Update(windows.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, *(*unsafe.Pointer)(unsafe.Pointer(&hpc)), unsafe.Sizeof(hpc)); err != nil {
		return fail(err)
	}
	si := &windows.StartupInfoEx{ProcThreadAttributeList: attrs.List()}
	si.Cb = uint32(unsafe.Sizeof(*si))
	si.Flags = windows.STARTF_USESTDHANDLES // 不继承 daemon 的标准句柄，全部走伪控制台

	cmdline, err := windows.UTF16PtrFromString(windows.ComposeCommandLine(append([]string{shell}, args...)))
	if err != nil {
		return fail(err)
	}
	wdir, err := windows.UTF16PtrFromString(dir)
	if err != nil {
		return fail(err)
	}
	block := envBlock(env)
	var pi windows.ProcessInformation
	flags := uint32(windows.EXTENDED_STARTUPINFO_PRESENT | windows.CREATE_UNICODE_ENVIRONMENT | windows.CREATE_SUSPENDED)
	if err := windows.CreateProcess(nil, cmdline, nil, nil, false, flags, &block[0], wdir, &si.StartupInfo, &pi); err != nil {
		return fail(err)
	}
	windows.CloseHandle(pi.Thread)
	pid := int(pi.ProcessId)
	proc.StartedPID(pid) // 放进 Job 后恢复运行
	p := &conPTY{hpc: hpc, in: os.NewFile(uintptr(inW), "conpty-in"), out: os.NewFile(uintptr(outR), "conpty-out"), pid: pid}
	return p, &conProc{h: pi.Process, pid: pid}, nil
}

// envBlock 组装 CreateProcess 的 UTF-16 环境块（按名字不分大小写排序，以两个 0 结尾）。
func envBlock(env []string) []uint16 {
	list := append([]string(nil), env...)
	sort.Slice(list, func(i, j int) bool { return strings.ToUpper(list[i]) < strings.ToUpper(list[j]) })
	var b []uint16
	for _, kv := range list {
		if strings.IndexByte(kv, 0) >= 0 || !strings.Contains(kv, "=") {
			continue
		}
		b = append(b, utf16.Encode([]rune(kv))...)
		b = append(b, 0)
	}
	if len(b) == 0 {
		b = append(b, 0)
	}
	return append(b, 0)
}

func (c *conPTY) Read(b []byte) (int, error)  { return c.out.Read(b) }
func (c *conPTY) Write(b []byte) (int, error) { return c.in.Write(b) }

func (c *conPTY) Resize(cols, rows int) error {
	return windows.ResizePseudoConsole(c.hpc, windows.Coord{X: int16(cols), Y: int16(rows)})
}

// Close 关闭伪控制台（读协程随后读到 EOF），再关掉两端管道。
func (c *conPTY) Close() error {
	c.closeOnce.Do(func() {
		windows.ClosePseudoConsole(c.hpc)
		_ = c.in.Close()
		_ = c.out.Close()
	})
	return nil
}

// Hangup：关闭伪控制台会给挂在上面的进程发 CTRL_CLOSE_EVENT，和关掉终端窗口一样。
func (c *conPTY) Hangup() { _ = c.Close() }

// Foreground：shell 有子进程就算有命令在前台运行，返回子进程号。
func (c *conPTY) Foreground() int {
	if child, _ := proc.ChildProcess(c.pid); child > 0 {
		return child
	}
	return c.pid
}

var conptyAvailable = func() bool {
	return windows.NewLazySystemDLL("kernel32.dll").NewProc("CreatePseudoConsole").Find() == nil
}()

func terminalSupported() bool { return conptyAvailable }

// defaultShell：PowerShell 7（pwsh）→ Windows PowerShell → cmd。
func defaultShell(env map[string]string) string {
	path := ""
	for k, v := range env {
		if strings.EqualFold(k, "PATH") {
			path = v
		}
	}
	for _, name := range []string{"pwsh.exe", "powershell.exe"} {
		for _, d := range filepath.SplitList(path) {
			if d == "" {
				continue
			}
			if c := filepath.Join(d, name); isExecutable(c) {
				return c
			}
		}
	}
	if root := os.Getenv("SystemRoot"); root != "" {
		if c := filepath.Join(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"); isExecutable(c) {
			return c
		}
	}
	if c := os.Getenv("ComSpec"); c != "" {
		return c
	}
	return "cmd.exe"
}

// shellArgs：PowerShell 不显示版权横幅。
func shellArgs(shell string) []string {
	switch strings.ToLower(strings.TrimSuffix(filepath.Base(shell), filepath.Ext(shell))) {
	case "pwsh", "powershell":
		return []string{"-NoLogo"}
	}
	return nil
}
