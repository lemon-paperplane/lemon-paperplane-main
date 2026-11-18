//go:build windows

package proc

import (
	"os/exec"
	"strconv"
	"strings"
	"syscall"
)

const createNewProcessGroup = 0x00000200
const detachedProcess = 0x00000008
const createNoWindow = 0x08000000

func Prepare(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: createNewProcessGroup | createNoWindow, HideWindow: true}
}

func PrepareDetached(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: detachedProcess | createNewProcessGroup | createNoWindow, HideWindow: true}
}

func SignalGroup(pid int, sig string) error {
	if pid <= 0 {
		return nil
	}
	args := []string{"/T", "/PID", strconv.Itoa(pid)}
	if sig == "SIGKILL" {
		args = append([]string{"/F"}, args...)
	}
	return exec.Command("taskkill", args...).Run()
}

func KillGroup(pid int) { _ = SignalGroup(pid, "SIGKILL") }

func Kill(pid int) error {
	return exec.Command("taskkill", "/F", "/PID", strconv.Itoa(pid)).Run()
}

func Alive(pid int) bool {
	out, _ := exec.Command("tasklist", "/FI", "PID eq "+strconv.Itoa(pid), "/NH").Output()
	return strings.Contains(string(out), strconv.Itoa(pid))
}

// TreeStats 在 Windows 上暂只统计根进程（下一轮用 toolhelp32 完善）。
func TreeStats(roots []int) map[int]Stat {
	res := map[int]Stat{}
	for _, r := range roots {
		res[r] = Stat{PIDs: []int{r}, Procs: []ProcInfo{{PID: r}}}
	}
	return res
}

func RSSMB(pids ...int) float64 { return 0 }

func ListeningPorts(pids []int) []int {
	out, _ := exec.Command("netstat", "-ano", "-p", "TCP").Output()
	want := map[string]bool{}
	for _, p := range pids {
		want[strconv.Itoa(p)] = true
	}
	var ports []int
	seen := map[int]bool{}
	for _, line := range strings.Split(string(out), "\n") {
		f := strings.Fields(line)
		if len(f) < 5 || f[3] != "LISTENING" || !want[f[4]] {
			continue
		}
		i := strings.LastIndex(f[1], ":")
		if p, err := strconv.Atoi(f[1][i+1:]); err == nil && !seen[p] {
			seen[p] = true
			ports = append(ports, p)
		}
	}
	return ports
}

func PIDsOnPort(port int) []int {
	out, _ := exec.Command("netstat", "-ano", "-p", "TCP").Output()
	var pids []int
	suffix := ":" + strconv.Itoa(port)
	for _, line := range strings.Split(string(out), "\n") {
		f := strings.Fields(line)
		if len(f) >= 5 && f[3] == "LISTENING" && strings.HasSuffix(f[1], suffix) {
			if p, err := strconv.Atoi(f[4]); err == nil {
				pids = append(pids, p)
			}
		}
	}
	return pids
}

func ProcessName(pid int) string { return "" }

// IsSystemProcess 在 Windows 上把 PID 4（System）与 0 视为系统进程。
func IsSystemProcess(pid int) bool { return pid == 0 || pid == 4 }
