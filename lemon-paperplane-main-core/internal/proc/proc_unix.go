//go:build !windows

package proc

import (
	"bufio"
	"bytes"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"syscall"
)

// Prepare 让子进程成为新进程组的组长，便于整组结束。
func Prepare(cmd *exec.Cmd) {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.Setpgid = true
}

// PrepareDetached 让子进程脱离当前会话（daemon --detach 用）。
func PrepareDetached(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
}

func sigOf(name string) syscall.Signal {
	switch name {
	case "SIGINT":
		return syscall.SIGINT
	case "SIGKILL":
		return syscall.SIGKILL
	case "SIGHUP":
		return syscall.SIGHUP
	default:
		return syscall.SIGTERM
	}
}

// SignalGroup 给进程组发信号（pid 为组长）。
func SignalGroup(pid int, sig string) error {
	if pid <= 0 {
		return nil
	}
	if err := syscall.Kill(-pid, sigOf(sig)); err != nil {
		return syscall.Kill(pid, sigOf(sig))
	}
	return nil
}

// KillGroup 强制结束整组。
func KillGroup(pid int) { _ = SignalGroup(pid, "SIGKILL") }

// Kill 结束单个进程。
func Kill(pid int) error { return syscall.Kill(pid, syscall.SIGKILL) }

// Alive 判断进程是否存在。
func Alive(pid int) bool { return pid > 0 && syscall.Kill(pid, 0) == nil }

type psRow struct {
	pid, ppid, pgid int
	rssKB           float64
	cpu             float64
	comm            string
}

func psAll() []psRow {
	out, err := exec.Command("ps", "-axo", "pid=,ppid=,pgid=,rss=,%cpu=,comm=").Output()
	if err != nil {
		return nil
	}
	var rows []psRow
	sc := bufio.NewScanner(bytes.NewReader(out))
	for sc.Scan() {
		f := strings.Fields(sc.Text())
		if len(f) < 5 {
			continue
		}
		var r psRow
		r.pid, _ = strconv.Atoi(f[0])
		r.ppid, _ = strconv.Atoi(f[1])
		r.pgid, _ = strconv.Atoi(f[2])
		r.rssKB, _ = strconv.ParseFloat(f[3], 64)
		r.cpu, _ = strconv.ParseFloat(strings.ReplaceAll(f[4], ",", "."), 64)
		if len(f) > 5 {
			r.comm = filepath.Base(strings.Join(f[5:], " "))
		}
		rows = append(rows, r)
	}
	return rows
}

// TreeStats 统计每个根进程（含后代与同进程组成员）的资源占用。一次 ps 调用。
func TreeStats(roots []int) map[int]Stat {
	res := map[int]Stat{}
	if len(roots) == 0 {
		return res
	}
	rows := psAll()
	children := map[int][]int{}
	byPID := map[int]psRow{}
	for _, r := range rows {
		children[r.ppid] = append(children[r.ppid], r.pid)
		byPID[r.pid] = r
	}
	for _, root := range roots {
		seen := map[int]bool{}
		var walk func(p int)
		walk = func(p int) {
			if seen[p] {
				return
			}
			seen[p] = true
			for _, c := range children[p] {
				walk(c)
			}
		}
		walk(root)
		// 进程组成员（父进程已退出被收养的子进程也算）
		for _, r := range rows {
			if r.pgid == root {
				walk(r.pid)
			}
		}
		var st Stat
		for p := range seen {
			r, ok := byPID[p]
			if !ok {
				continue
			}
			st.CPU += r.cpu
			st.RSSMB += r.rssKB / 1024
			st.PIDs = append(st.PIDs, p)
			st.Procs = append(st.Procs, ProcInfo{PID: r.pid, PPID: r.ppid, Name: r.comm, CPU: r.cpu, RSSMB: r.rssKB / 1024})
		}
		sort.Slice(st.Procs, func(i, j int) bool { return st.Procs[i].RSSMB > st.Procs[j].RSSMB })
		res[root] = st
	}
	return res
}

// SelfRSSMB 返回给定进程的常驻内存。
func RSSMB(pids ...int) float64 {
	if len(pids) == 0 {
		return 0
	}
	var args []string
	for _, p := range pids {
		args = append(args, strconv.Itoa(p))
	}
	out, err := exec.Command("ps", "-o", "rss=", "-p", strings.Join(args, ",")).Output()
	if err != nil {
		return 0
	}
	var total float64
	for _, l := range strings.Fields(string(out)) {
		v, _ := strconv.ParseFloat(l, 64)
		total += v
	}
	return total / 1024
}

// ListeningPorts 返回这些进程监听的 TCP 端口。
func ListeningPorts(pids []int) []int {
	if len(pids) == 0 {
		return nil
	}
	var ps []string
	for _, p := range pids {
		ps = append(ps, strconv.Itoa(p))
	}
	out, _ := exec.Command("lsof", "-nP", "-iTCP", "-sTCP:LISTEN", "-a", "-p", strings.Join(ps, ","), "-Fn").Output()
	seen := map[int]bool{}
	var ports []int
	for _, line := range strings.Split(string(out), "\n") {
		if !strings.HasPrefix(line, "n") {
			continue
		}
		i := strings.LastIndex(line, ":")
		if i < 0 {
			continue
		}
		p, err := strconv.Atoi(line[i+1:])
		if err == nil && !seen[p] {
			seen[p] = true
			ports = append(ports, p)
		}
	}
	return ports
}

// PIDsOnPort 返回监听该端口的进程。
func PIDsOnPort(port int) []int {
	out, _ := exec.Command("lsof", "-nP", "-iTCP:"+strconv.Itoa(port), "-sTCP:LISTEN", "-t").Output()
	var pids []int
	for _, l := range strings.Fields(string(out)) {
		if p, err := strconv.Atoi(l); err == nil {
			pids = append(pids, p)
		}
	}
	return pids
}

// ProcessName 返回进程命令名。
func ProcessName(pid int) string {
	out, _ := exec.Command("ps", "-o", "comm=", "-p", strconv.Itoa(pid)).Output()
	return strings.TrimSpace(string(out))
}

// IsSystemProcess 判断进程是否属于操作系统（root 拥有或可执行文件在系统目录）。
func IsSystemProcess(pid int) bool {
	out, err := exec.Command("ps", "-o", "uid=,comm=", "-p", strconv.Itoa(pid)).Output()
	if err != nil {
		return false
	}
	f := strings.Fields(string(out))
	if len(f) < 2 {
		return false
	}
	if f[0] == "0" {
		return true
	}
	comm := strings.Join(f[1:], " ")
	for _, pre := range []string{"/System/", "/usr/libexec/", "/usr/sbin/", "/sbin/"} {
		if strings.HasPrefix(comm, pre) {
			return true
		}
	}
	return false
}

// Started 在 Unix 上无事可做（进程组在 Prepare 里已经设好）。
func Started(cmd *exec.Cmd) {}

// StartedPID 在 Unix 上无事可做。
func StartedPID(pid int) {}

// StartDetached 启动 PrepareDetached 准备好的进程。
func StartDetached(cmd *exec.Cmd) error { return cmd.Start() }

// PrepareDaemon 在 Unix 上无事可做。
func PrepareDaemon() {}

// RunHelper 只有 Windows 有辅助进程。
func RunHelper(args []string) bool { return false }
