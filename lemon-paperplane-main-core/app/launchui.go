package app

import (
	"bufio"
	"bytes"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
)

// 原则：内核在桌面系统上跑着，菜单栏（Windows 托盘）就一定有图标，用户随时知道、随时能停。
// 内核不是由界面拉起的（开机自动启动、命令行、AI 的 MCP 桥…）时，把界面以「只有菜单栏图标」的方式拉起来。
//
// 不拉起的情况：Linux 等没有界面的系统、paperplane serve（服务器前台运行）、自定义数据目录（开发与测试）、
// PAPERPLANE_NO_UI、可执行文件旁边没有界面程序（开发时 go build 出来的内核）。

// uiExecutable 返回和内核同一安装里的界面程序；没有时返回空。
func uiExecutable(exe string) string {
	dir := filepath.Dir(exe)
	switch runtime.GOOS {
	case "darwin":
		// <App>.app/Contents/MacOS/paperplane → 同目录下的界面主程序（CFBundleExecutable）
		if filepath.Base(filepath.Dir(dir)) != "Contents" {
			return ""
		}
		ents, _ := os.ReadDir(dir)
		for _, e := range ents {
			n := e.Name()
			if e.IsDir() || n == filepath.Base(exe) || strings.HasPrefix(n, "paperplane") {
				continue
			}
			return filepath.Join(dir, n)
		}
	case "windows":
		if p := filepath.Join(dir, "LemonPaperplane.exe"); fileExists(p) {
			return p
		}
	}
	return ""
}

func fileExists(p string) bool {
	st, err := os.Stat(p)
	return err == nil && !st.IsDir()
}

// uiRunning 判断界面是否已经在运行。
func uiRunning(ui string) bool {
	switch runtime.GOOS {
	case "darwin":
		out, err := exec.Command("ps", "-axo", "args=").Output()
		if err != nil {
			return true // 判断不了就当作在运行，宁可不拉起也别拉起两个
		}
		sc := bufio.NewScanner(bytes.NewReader(out))
		for sc.Scan() {
			if strings.HasPrefix(strings.TrimSpace(sc.Text()), ui) {
				return true
			}
		}
		return false
	case "windows":
		out, err := exec.Command("tasklist", "/FI", "IMAGENAME eq "+filepath.Base(ui), "/FO", "CSV", "/NH").Output()
		if err != nil {
			return true
		}
		return bytes.Contains(bytes.ToLower(out), []byte(strings.ToLower(filepath.Base(ui))))
	}
	return true
}

// ensureUI：内核启动后，界面没在运行就以菜单栏模式（--background）拉起来。
func ensureUI(exe string, serve bool, logger *log.Logger) {
	if serve || os.Getenv("PAPERPLANE_NO_UI") != "" || os.Getenv("PAPERPLANE_DATA_DIR") != "" {
		return
	}
	ui := uiExecutable(exe)
	if ui == "" || uiRunning(ui) {
		return
	}
	var cmd *exec.Cmd
	if runtime.GOOS == "darwin" {
		// 经 LaunchServices 打开（不是内核的子进程）；-g 不抢前台，-j 启动时不显示窗口
		bundle := filepath.Dir(filepath.Dir(filepath.Dir(ui)))
		cmd = exec.Command("open", "-g", "-j", "-a", bundle, "--args", "--background")
	} else {
		cmd = exec.Command(ui, "--background")
		proc.PrepareDetached(cmd)
	}
	var err error
	if runtime.GOOS == "darwin" {
		err = cmd.Run()
	} else {
		err = proc.StartDetached(cmd)
	}
	if err != nil {
		logger.Printf("launch menu bar UI: %v", err)
		return
	}
	logger.Printf("launched menu bar UI (%s)", ui)
}
