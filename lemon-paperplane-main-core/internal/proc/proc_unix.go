//go:build !windows

package proc

import (
	"os/exec"
	"sort"
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
