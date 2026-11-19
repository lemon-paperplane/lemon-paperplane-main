//go:build windows

package engine

import (
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"syscall"
)

const createNewConsole = 0x00000010

// openWithWindows 在资源管理器 / 终端 / IDE 中打开目录。
// terminal 设置：""（自动：Windows Terminal → PowerShell）、"wt"、"pwsh"、"powershell"、"cmd"。
func openWithWindows(terminal, with, dir string) *exec.Cmd {
	switch with {
	case "terminal":
		return windowsTerminal(terminal, dir)
	case "vscode":
		local := os.Getenv("LOCALAPPDATA")
		return windowsApp([]string{
			filepath.Join(local, "Programs", "Microsoft VS Code", "Code.exe"),
			filepath.Join(os.Getenv("ProgramFiles"), "Microsoft VS Code", "Code.exe"),
			filepath.Join(local, "Programs", "cursor", "Cursor.exe"),
			filepath.Join(local, "Programs", "VSCodium", "VSCodium.exe"),
		}, "code", dir)
	case "idea":
		var cands []string
		for _, root := range []string{filepath.Join(os.Getenv("LOCALAPPDATA"), "Programs"), filepath.Join(os.Getenv("ProgramFiles"), "JetBrains")} {
			m, _ := filepath.Glob(filepath.Join(root, "IntelliJ IDEA*", "bin", "idea64.exe"))
			sort.Sort(sort.Reverse(sort.StringSlice(m))) // 新版本在前
			cands = append(cands, m...)
		}
		return windowsApp(cands, "idea64", dir)
	default:
		return exec.Command("explorer", dir)
	}
}

func windowsApp(cands []string, fallback, dir string) *exec.Cmd {
	for _, c := range cands {
		if st, err := os.Stat(c); err == nil && !st.IsDir() {
			return exec.Command(c, dir)
		}
	}
	// 命令行入口（code.cmd 等）经 cmd 启动；daemon 的控制台是隐藏的，不会闪黑窗
	return exec.Command("cmd", "/c", fallback, dir)
}

func windowsTerminal(app, dir string) *exec.Cmd {
	app = strings.ToLower(strings.TrimSpace(app))
	if app == "" || app == "wt" {
		if wt, err := exec.LookPath("wt.exe"); err == nil {
			return exec.Command(wt, "-d", dir)
		}
		if wt := filepath.Join(os.Getenv("LOCALAPPDATA"), "Microsoft", "WindowsApps", "wt.exe"); fileExistsAny(wt) {
			return exec.Command(wt, "-d", dir)
		}
		app = ""
	}
	var exe string
	var args []string
	switch app {
	case "cmd":
		exe = os.Getenv("ComSpec")
		if exe == "" {
			exe = "cmd.exe"
		}
	case "pwsh":
		exe, args = "pwsh.exe", []string{"-NoLogo"}
	default:
		exe, args = "powershell.exe", []string{"-NoLogo"}
		if p, err := exec.LookPath("pwsh.exe"); err == nil && app == "" {
			exe = p
		}
	}
	cmd := exec.Command(exe, args...)
	cmd.Dir = dir
	// 新开一个可见的控制台窗口
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: createNewConsole}
	return cmd
}

func fileExistsAny(p string) bool {
	_, err := os.Lstat(p)
	return err == nil
}
