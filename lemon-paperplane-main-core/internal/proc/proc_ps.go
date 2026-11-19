//go:build !windows && !linux

package proc

import (
	"bufio"
	"bytes"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
)

// macOS 等：用 ps / lsof（系统自带）。Linux 读 /proc（proc_linux.go），不依赖精简系统里可能没有的 lsof。

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
