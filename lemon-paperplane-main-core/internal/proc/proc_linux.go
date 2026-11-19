package proc

import (
	"bufio"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// Linux：直接读 /proc，不依赖 ps / lsof（Alpine、容器镜像等精简系统里经常没有）。

const clkTck = 100 // Linux 各架构的 USER_HZ 都是 100

type psRow struct {
	pid, ppid, pgid int
	rssKB           float64
	cpu             float64
	comm            string
}

var pageKB = float64(os.Getpagesize()) / 1024

func uptimeSeconds() float64 {
	b, err := os.ReadFile("/proc/uptime")
	if err != nil {
		return 0
	}
	f := strings.Fields(string(b))
	if len(f) == 0 {
		return 0
	}
	v, _ := strconv.ParseFloat(f[0], 64)
	return v
}

// readStat 读 /proc/<pid>/stat（comm 可能含空格和括号，按最后一个 ")" 切）。
func readStat(pid int, uptime float64) (psRow, bool) {
	b, err := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/stat")
	if err != nil {
		return psRow{}, false
	}
	s := string(b)
	l, r := strings.IndexByte(s, '('), strings.LastIndexByte(s, ')')
	if l < 0 || r < l {
		return psRow{}, false
	}
	f := strings.Fields(s[r+1:])
	if len(f) < 22 {
		return psRow{}, false
	}
	row := psRow{pid: pid, comm: s[l+1 : r]}
	row.ppid, _ = strconv.Atoi(f[1])
	row.pgid, _ = strconv.Atoi(f[2])
	ut, _ := strconv.ParseFloat(f[11], 64)
	st, _ := strconv.ParseFloat(f[12], 64)
	start, _ := strconv.ParseFloat(f[19], 64)
	rss, _ := strconv.ParseFloat(f[21], 64)
	row.rssKB = rss * pageKB
	// 和 ps 的 %cpu 口径一致：进程存活期间的平均占用
	if el := uptime - start/clkTck; el > 0 {
		row.cpu = (ut + st) / clkTck / el * 100
	}
	return row, true
}

func allPIDs() []int {
	ents, _ := os.ReadDir("/proc")
	var out []int
	for _, e := range ents {
		if p, err := strconv.Atoi(e.Name()); err == nil {
			out = append(out, p)
		}
	}
	return out
}

func psAll() []psRow {
	up := uptimeSeconds()
	var rows []psRow
	for _, p := range allPIDs() {
		if r, ok := readStat(p, up); ok {
			rows = append(rows, r)
		}
	}
	return rows
}

// RSSMB 返回给定进程的常驻内存。
func RSSMB(pids ...int) float64 {
	up := uptimeSeconds()
	var total float64
	for _, p := range pids {
		if r, ok := readStat(p, up); ok {
			total += r.rssKB
		}
	}
	return total / 1024
}

// listenInodes：/proc/net/tcp{,6} 里处于 LISTEN 的套接字 inode → 端口。
func listenInodes() map[string]int {
	out := map[string]int{}
	for _, f := range []string{"/proc/net/tcp", "/proc/net/tcp6"} {
		fh, err := os.Open(f)
		if err != nil {
			continue
		}
		sc := bufio.NewScanner(fh)
		sc.Scan() // 表头
		for sc.Scan() {
			fs := strings.Fields(sc.Text())
			if len(fs) < 10 || fs[3] != "0A" {
				continue
			}
			i := strings.LastIndexByte(fs[1], ':')
			if i < 0 {
				continue
			}
			port, err := strconv.ParseInt(fs[1][i+1:], 16, 32)
			if err == nil && fs[9] != "0" {
				out[fs[9]] = int(port)
			}
		}
		fh.Close()
	}
	return out
}

// socketInodes 返回进程打开的套接字 inode。
func socketInodes(pid int) []string {
	dir := "/proc/" + strconv.Itoa(pid) + "/fd"
	ents, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var out []string
	for _, e := range ents {
		l, err := os.Readlink(filepath.Join(dir, e.Name()))
		if err == nil && strings.HasPrefix(l, "socket:[") {
			out = append(out, strings.TrimSuffix(strings.TrimPrefix(l, "socket:["), "]"))
		}
	}
	return out
}

// ListeningPorts 返回这些进程监听的 TCP 端口。
func ListeningPorts(pids []int) []int {
	if len(pids) == 0 {
		return nil
	}
	inodes := listenInodes()
	seen := map[int]bool{}
	var ports []int
	for _, p := range pids {
		for _, in := range socketInodes(p) {
			if port, ok := inodes[in]; ok && !seen[port] {
				seen[port] = true
				ports = append(ports, port)
			}
		}
	}
	return ports
}

// PIDsOnPort 返回监听该端口的进程（只看得到有权限的进程，和 lsof 不加 sudo 时一样）。
func PIDsOnPort(port int) []int {
	want := map[string]bool{}
	for in, p := range listenInodes() {
		if p == port {
			want[in] = true
		}
	}
	if len(want) == 0 {
		return nil
	}
	var out []int
	for _, pid := range allPIDs() {
		for _, in := range socketInodes(pid) {
			if want[in] {
				out = append(out, pid)
				break
			}
		}
	}
	return out
}

// ProcessName 返回进程命令名。
func ProcessName(pid int) string {
	b, _ := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/comm")
	return strings.TrimSpace(string(b))
}

// IsSystemProcess 判断进程是否属于操作系统（root 拥有或可执行文件在系统目录）。
func IsSystemProcess(pid int) bool {
	b, err := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/status")
	if err != nil {
		return false
	}
	for _, l := range strings.Split(string(b), "\n") {
		if strings.HasPrefix(l, "Uid:") {
			if f := strings.Fields(l); len(f) > 1 && f[1] == "0" {
				return true
			}
		}
	}
	exe, _ := os.Readlink("/proc/" + strconv.Itoa(pid) + "/exe")
	for _, pre := range []string{"/usr/sbin/", "/sbin/", "/usr/lib/systemd/", "/lib/systemd/"} {
		if strings.HasPrefix(exe, pre) {
			return true
		}
	}
	return false
}
