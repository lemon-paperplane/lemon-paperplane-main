//go:build windows

package proc

import (
	"encoding/binary"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Windows 上的进程托管：
//   - 每个运行（以及插件执行的前置命令）放进一个 Job Object（关闭即结束整棵树），对应 Unix 的进程组。
//     进程以挂起状态创建，放进 Job 后再恢复，子孙进程一定在 Job 里，不会漏网。
//   - 每个运行有自己的隐藏控制台（CREATE_NO_WINDOW），「停止」= 给这个控制台发 Ctrl+C（Java 走关闭钩子、
//     Node/Python 正常退出），超时再结束整个 Job。发 Ctrl+C 要先挂到对方控制台上，所以交给一个短命的
//     辅助进程（本程序加 __ctrlc 参数）去做，daemon 自己的控制台不受影响。
//   - 资源统计用 toolhelp32 快照 + GetProcessTimes/GetProcessMemoryInfo；CPU 是两次采样之间的增量。
//   - 监听端口用 GetExtendedTcpTable（不调 netstat）。

const (
	createSuspended   = 0x00000004
	createNoWindow    = 0x08000000
	createBreakaway   = 0x01000000
	ctrlCEvent        = 0
	stillActive       = 259
	processQueryLtd   = windows.PROCESS_QUERY_LIMITED_INFORMATION
	ctrlHelperArg     = "__ctrlc"
	ctrlHelperTimeout = 3 * time.Second
)

var (
	kernel32                 = windows.NewLazySystemDLL("kernel32.dll")
	procAttachConsole        = kernel32.NewProc("AttachConsole")
	procFreeConsole          = kernel32.NewProc("FreeConsole")
	procSetConsoleCtrlHandle = kernel32.NewProc("SetConsoleCtrlHandler")
	procGetProcessMemoryInfo = kernel32.NewProc("K32GetProcessMemoryInfo")
	iphlpapi                 = windows.NewLazySystemDLL("iphlpapi.dll")
	procGetExtendedTcpTable  = iphlpapi.NewProc("GetExtendedTcpTable")
)

var (
	jobsMu sync.Mutex
	jobs   = map[int]windows.Handle{} // 根进程 pid → Job
)

// Prepare：隐藏控制台 + 挂起创建。调用方必须在 cmd.Start() 之后调用 Started(cmd)，否则进程一直挂起。
func Prepare(cmd *exec.Cmd) {
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.HideWindow = true
	cmd.SysProcAttr.CreationFlags |= createNoWindow | createSuspended
}

// Started 把刚启动（挂起中）的进程放进新的 Job，然后恢复运行。
func Started(cmd *exec.Cmd) {
	if cmd == nil || cmd.Process == nil {
		return
	}
	StartedPID(cmd.Process.Pid)
}

// StartedPID 同 Started，用于不是经 exec.Cmd 创建的进程（如伪控制台里的 shell）。
func StartedPID(pid int) {
	defer resumeProcess(pid)
	job, err := windows.CreateJobObject(nil, nil)
	if err != nil {
		return
	}
	info := windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION{}
	info.BasicLimitInformation.LimitFlags = windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
	_, _ = windows.SetInformationJobObject(job, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&info)), uint32(unsafe.Sizeof(info)))
	h, err := windows.OpenProcess(windows.PROCESS_SET_QUOTA|windows.PROCESS_TERMINATE|processQueryLtd, false, uint32(pid))
	if err != nil {
		windows.CloseHandle(job)
		return
	}
	defer windows.CloseHandle(h)
	if err := windows.AssignProcessToJobObject(job, h); err != nil {
		windows.CloseHandle(job)
		return
	}
	jobsMu.Lock()
	if old, ok := jobs[pid]; ok {
		windows.CloseHandle(old)
	}
	jobs[pid] = job
	jobsMu.Unlock()
}

// resumeProcess 恢复进程里所有挂起的线程（Go 的 exec 拿不到主线程句柄，按快照找）。
func resumeProcess(pid int) {
	snap, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPTHREAD, 0)
	if err != nil {
		return
	}
	defer windows.CloseHandle(snap)
	var te windows.ThreadEntry32
	te.Size = uint32(unsafe.Sizeof(te))
	for err = windows.Thread32First(snap, &te); err == nil; err = windows.Thread32Next(snap, &te) {
		if te.OwnerProcessID != uint32(pid) {
			continue
		}
		th, err := windows.OpenThread(windows.THREAD_SUSPEND_RESUME, false, te.ThreadID)
		if err != nil {
			continue
		}
		_, _ = windows.ResumeThread(th)
		windows.CloseHandle(th)
	}
}

// PrepareDetached 用于 daemon --detach：自己的隐藏控制台（子进程继承它，不会弹出黑窗），
// 并尽量脱离启动者所在的 Job（从终端、远程会话启动时，对方关闭不会连带结束 daemon）。
func PrepareDetached(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: createNoWindow | createBreakaway}
}

// StartDetached 启动 PrepareDetached 准备好的进程；所在 Job 不允许脱离时退回普通方式。
func StartDetached(cmd *exec.Cmd) error {
	err := cmd.Start()
	if err != nil && cmd.SysProcAttr != nil && cmd.SysProcAttr.CreationFlags&createBreakaway != 0 {
		cmd.SysProcAttr.CreationFlags &^= createBreakaway
		cmd.Process = nil
		return cmd.Start()
	}
	return err
}

// PrepareDaemon 在 daemon 进程开头调用：清掉可能继承来的「忽略 Ctrl+C」标记，
// 否则运行的工程也会继承它，「停止」时发的 Ctrl+C 不起作用。
func PrepareDaemon() {
	_, _, _ = procSetConsoleCtrlHandle.Call(0, 0)
}

// SignalGroup 「停止」整棵进程树：SIGKILL 直接结束；其余发 Ctrl+C（调用方超时后再 KillGroup）。
func SignalGroup(pid int, sig string) error {
	if pid <= 0 {
		return nil
	}
	if sig == "SIGKILL" {
		KillGroup(pid)
		return nil
	}
	if err := sendCtrlC(pid); err != nil {
		KillGroup(pid)
		return err
	}
	return nil
}

// sendCtrlC 让辅助进程挂到 pid 的控制台上发 Ctrl+C。
func sendCtrlC(pid int) error {
	exe, err := os.Executable()
	if err != nil {
		return err
	}
	cmd := exec.Command(exe, ctrlHelperArg, strconv.Itoa(pid))
	// 辅助进程不能有自己的控制台（AttachConsole 要求先没有控制台）
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: windows.DETACHED_PROCESS}
	done := make(chan error, 1)
	if err := cmd.Start(); err != nil {
		return err
	}
	go func() { done <- cmd.Wait() }()
	select {
	case err := <-done:
		return err
	case <-time.After(ctrlHelperTimeout):
		_ = cmd.Process.Kill()
		return errors.New("ctrl-c helper timed out")
	}
}

// RunHelper 处理本程序作为辅助进程的调用（main / TestMain 开头调用），处理了返回 true。
func RunHelper(args []string) bool {
	if len(args) < 3 || args[1] != ctrlHelperArg {
		return false
	}
	pid, _ := strconv.Atoi(args[2])
	os.Exit(ctrlCHelper(pid))
	return true
}

func ctrlCHelper(pid int) int {
	_, _, _ = procFreeConsole.Call()
	if r, _, _ := procAttachConsole.Call(uintptr(pid)); r == 0 {
		return 1
	}
	// 自己忽略这次 Ctrl+C
	_, _, _ = procSetConsoleCtrlHandle.Call(0, 1)
	if err := windows.GenerateConsoleCtrlEvent(ctrlCEvent, 0); err != nil {
		return 2
	}
	// 给控制台一点时间把事件派发出去再断开
	time.Sleep(50 * time.Millisecond)
	_, _, _ = procFreeConsole.Call()
	return 0
}

// KillGroup 结束整棵树：有 Job 就结束 Job，否则按父子关系逐个结束。
func KillGroup(pid int) {
	if pid <= 0 {
		return
	}
	jobsMu.Lock()
	job, ok := jobs[pid]
	delete(jobs, pid)
	jobsMu.Unlock()
	if ok {
		_ = windows.TerminateJobObject(job, 1)
		windows.CloseHandle(job)
		return
	}
	snap := snapshot()
	for _, p := range descendants(snap, pid) {
		_ = Kill(p)
	}
}

// Kill 结束单个进程。
func Kill(pid int) error {
	h, err := windows.OpenProcess(windows.PROCESS_TERMINATE, false, uint32(pid))
	if err != nil {
		return err
	}
	defer windows.CloseHandle(h)
	return windows.TerminateProcess(h, 1)
}

// Alive 判断进程是否还在运行。
func Alive(pid int) bool {
	if pid <= 0 {
		return false
	}
	h, err := windows.OpenProcess(processQueryLtd, false, uint32(pid))
	if err != nil {
		// 没权限打开说明进程存在（属于别的用户 / 系统）
		return errors.Is(err, windows.ERROR_ACCESS_DENIED)
	}
	defer windows.CloseHandle(h)
	var code uint32
	if windows.GetExitCodeProcess(h, &code) != nil {
		return false
	}
	return code == stillActive
}

// ---------------------------------------------------------------------------
// 快照与资源统计

type snapProc struct {
	pid, ppid int
	name      string
}

type procSnap struct {
	byPID    map[int]snapProc
	children map[int][]int
}

func snapshot() procSnap {
	s := procSnap{byPID: map[int]snapProc{}, children: map[int][]int{}}
	h, err := windows.CreateToolhelp32Snapshot(windows.TH32CS_SNAPPROCESS, 0)
	if err != nil {
		return s
	}
	defer windows.CloseHandle(h)
	var pe windows.ProcessEntry32
	pe.Size = uint32(unsafe.Sizeof(pe))
	for err = windows.Process32First(h, &pe); err == nil; err = windows.Process32Next(h, &pe) {
		p := snapProc{pid: int(pe.ProcessID), ppid: int(pe.ParentProcessID), name: windows.UTF16ToString(pe.ExeFile[:])}
		s.byPID[p.pid] = p
		if p.pid != p.ppid {
			s.children[p.ppid] = append(s.children[p.ppid], p.pid)
		}
	}
	return s
}

// descendants 返回 root 与它的全部后代（父进程号会被复用，用创建时间排除「父进程比子进程晚出生」的假关系）。
func descendants(s procSnap, root int) []int {
	seen := map[int]bool{}
	var out []int
	var walk func(p int, born int64)
	walk = func(p int, born int64) {
		if seen[p] {
			return
		}
		seen[p] = true
		out = append(out, p)
		for _, c := range s.children[p] {
			cb := creationTime(c)
			if born != 0 && cb != 0 && cb < born {
				continue
			}
			walk(c, cb)
		}
	}
	walk(root, creationTime(root))
	return out
}

func creationTime(pid int) int64 {
	h, err := windows.OpenProcess(processQueryLtd, false, uint32(pid))
	if err != nil {
		return 0
	}
	defer windows.CloseHandle(h)
	var c, e, k, u windows.Filetime
	if windows.GetProcessTimes(h, &c, &e, &k, &u) != nil {
		return 0
	}
	return c.Nanoseconds()
}

// jobPIDs 返回 Job 里的全部进程。
func jobPIDs(job windows.Handle) []int {
	// JOBOBJECT_BASIC_PROCESS_ID_LIST：两个 DWORD 计数 + ULONG_PTR 数组
	const max = 1024
	buf := make([]byte, 8+max*int(unsafe.Sizeof(uintptr(0))))
	if err := windows.QueryInformationJobObject(job, windows.JobObjectBasicProcessIdList, uintptr(unsafe.Pointer(&buf[0])), uint32(len(buf)), nil); err != nil {
		return nil
	}
	n := int(binary.LittleEndian.Uint32(buf[4:8]))
	if n > max {
		n = max
	}
	ids := unsafe.Slice((*uintptr)(unsafe.Pointer(&buf[8])), n)
	out := make([]int, 0, n)
	for _, id := range ids {
		out = append(out, int(id))
	}
	return out
}

type processMemoryCounters struct {
	CB                         uint32
	PageFaultCount             uint32
	PeakWorkingSetSize         uintptr
	WorkingSetSize             uintptr
	QuotaPeakPagedPoolUsage    uintptr
	QuotaPagedPoolUsage        uintptr
	QuotaPeakNonPagedPoolUsage uintptr
	QuotaNonPagedPoolUsage     uintptr
	PagefileUsage              uintptr
	PeakPagefileUsage          uintptr
}

type cpuSample struct {
	born   int64
	cpu    int64 // 100ns
	at     time.Time
	seenAt time.Time
}

var (
	cpuMu   sync.Mutex
	cpuPrev = map[int]cpuSample{}
)

type procUsage struct {
	rssMB float64
	cpu   float64
	ok    bool
}

// usage 取一个进程的常驻内存（工作集）与 CPU（和上次采样之间的平均占用，单核 100%）。
func usage(pid int, now time.Time) procUsage {
	h, err := windows.OpenProcess(processQueryLtd, false, uint32(pid))
	if err != nil {
		return procUsage{}
	}
	defer windows.CloseHandle(h)
	var u procUsage
	var mc processMemoryCounters
	mc.CB = uint32(unsafe.Sizeof(mc))
	if r, _, _ := procGetProcessMemoryInfo.Call(uintptr(h), uintptr(unsafe.Pointer(&mc)), uintptr(mc.CB)); r != 0 {
		u.rssMB = float64(mc.WorkingSetSize) / (1 << 20)
		u.ok = true
	}
	var c, e, k, us windows.Filetime
	if windows.GetProcessTimes(h, &c, &e, &k, &us) == nil {
		total := ftTicks(k) + ftTicks(us)
		born := c.Nanoseconds()
		cpuMu.Lock()
		prev, had := cpuPrev[pid]
		if had && prev.born == born {
			if dt := now.Sub(prev.at); dt > 200*time.Millisecond {
				u.cpu = float64(total-prev.cpu) * 100 / float64(dt/100)
				if u.cpu < 0 {
					u.cpu = 0
				}
			}
		}
		if !had || prev.born != born || now.Sub(prev.at) > 200*time.Millisecond {
			cpuPrev[pid] = cpuSample{born: born, cpu: total, at: now, seenAt: now}
		} else {
			prev.seenAt = now
			cpuPrev[pid] = prev
		}
		cpuMu.Unlock()
		u.ok = true
	}
	return u
}

func ftTicks(f windows.Filetime) int64 { return int64(f.HighDateTime)<<32 | int64(f.LowDateTime) }

// pruneCPU 丢掉一分钟没采到的进程。
func pruneCPU(now time.Time) {
	cpuMu.Lock()
	defer cpuMu.Unlock()
	for pid, s := range cpuPrev {
		if now.Sub(s.seenAt) > time.Minute {
			delete(cpuPrev, pid)
		}
	}
}

// TreeStats 统计每个根进程整棵树的资源占用（有 Job 的按 Job 成员算，被收养的子进程也不漏）。
func TreeStats(roots []int) map[int]Stat {
	res := map[int]Stat{}
	if len(roots) == 0 {
		return res
	}
	snap := snapshot()
	now := time.Now()
	for _, root := range roots {
		jobsMu.Lock()
		job, hasJob := jobs[root]
		jobsMu.Unlock()
		var members []int
		if hasJob {
			members = jobPIDs(job)
		}
		if len(members) == 0 {
			members = descendants(snap, root)
		}
		var st Stat
		for _, pid := range members {
			u := usage(pid, now)
			if !u.ok {
				continue
			}
			sp := snap.byPID[pid]
			st.CPU += u.cpu
			st.RSSMB += u.rssMB
			st.PIDs = append(st.PIDs, pid)
			st.Procs = append(st.Procs, ProcInfo{PID: pid, PPID: sp.ppid, Name: strings.TrimSuffix(sp.name, ".exe"), CPU: round1(u.cpu), RSSMB: u.rssMB})
		}
		st.CPU = round1(st.CPU)
		sort.Slice(st.Procs, func(i, j int) bool { return st.Procs[i].RSSMB > st.Procs[j].RSSMB })
		res[root] = st
	}
	pruneCPU(now)
	return res
}

func round1(v float64) float64 { return float64(int64(v*10+0.5)) / 10 }

// RSSMB 返回这些进程的常驻内存之和。
func RSSMB(pids ...int) float64 {
	var total float64
	now := time.Now()
	for _, p := range pids {
		total += usage(p, now).rssMB
	}
	return total
}

// ---------------------------------------------------------------------------
// 监听端口

type listener struct{ port, pid int }

func tcpListeners() []listener {
	var out []listener
	for _, af := range []uint32{windows.AF_INET, windows.AF_INET6} {
		var size uint32
		// TCP_TABLE_OWNER_PID_LISTENER = 3
		_, _, _ = procGetExtendedTcpTable.Call(0, uintptr(unsafe.Pointer(&size)), 0, uintptr(af), 3, 0)
		if size == 0 {
			continue
		}
		buf := make([]byte, size+1024)
		size = uint32(len(buf))
		if r, _, _ := procGetExtendedTcpTable.Call(uintptr(unsafe.Pointer(&buf[0])), uintptr(unsafe.Pointer(&size)), 0, uintptr(af), 3, 0); r != 0 {
			continue
		}
		n := int(binary.LittleEndian.Uint32(buf[0:4]))
		if af == windows.AF_INET {
			// MIB_TCPROW_OWNER_PID：state, localAddr, localPort, remoteAddr, remotePort, pid（各 4 字节）
			const rowSize = 24
			for i := 0; i < n && 4+(i+1)*rowSize <= len(buf); i++ {
				row := buf[4+i*rowSize:]
				port := int(binary.BigEndian.Uint16(row[8:10]))
				pid := int(binary.LittleEndian.Uint32(row[20:24]))
				out = append(out, listener{port, pid})
			}
		} else {
			// MIB_TCP6ROW_OWNER_PID：localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
			const rowSize = 56
			for i := 0; i < n && 4+(i+1)*rowSize <= len(buf); i++ {
				row := buf[4+i*rowSize:]
				port := int(binary.BigEndian.Uint16(row[20:22]))
				pid := int(binary.LittleEndian.Uint32(row[52:56]))
				out = append(out, listener{port, pid})
			}
		}
	}
	return out
}

// ListeningPorts 返回这些进程监听的 TCP 端口。
func ListeningPorts(pids []int) []int {
	if len(pids) == 0 {
		return nil
	}
	want := map[int]bool{}
	for _, p := range pids {
		want[p] = true
	}
	seen := map[int]bool{}
	var ports []int
	for _, l := range tcpListeners() {
		if want[l.pid] && !seen[l.port] {
			seen[l.port] = true
			ports = append(ports, l.port)
		}
	}
	sort.Ints(ports)
	return ports
}

// PIDsOnPort 返回监听该端口的进程。
func PIDsOnPort(port int) []int {
	seen := map[int]bool{}
	var pids []int
	for _, l := range tcpListeners() {
		if l.port == port && l.pid > 0 && !seen[l.pid] {
			seen[l.pid] = true
			pids = append(pids, l.pid)
		}
	}
	return pids
}

// ProcessName 返回进程的可执行文件名（不带 .exe）。
func ProcessName(pid int) string {
	if p := imagePath(pid); p != "" {
		return strings.TrimSuffix(filepath.Base(p), filepath.Ext(p))
	}
	if sp, ok := snapshot().byPID[pid]; ok {
		return strings.TrimSuffix(sp.name, ".exe")
	}
	return ""
}

func imagePath(pid int) string {
	h, err := windows.OpenProcess(processQueryLtd, false, uint32(pid))
	if err != nil {
		return ""
	}
	defer windows.CloseHandle(h)
	buf := make([]uint16, windows.MAX_LONG_PATH)
	n := uint32(len(buf))
	if windows.QueryFullProcessImageName(h, 0, &buf[0], &n) != nil {
		return ""
	}
	return windows.UTF16ToString(buf[:n])
}

// IsSystemProcess：System/Idle、以系统账户（SYSTEM / LocalService / NetworkService）运行的、
// Windows 目录下的程序、打不开的（别的用户）都算系统进程。
func IsSystemProcess(pid int) bool {
	if pid == 0 || pid == 4 {
		return true
	}
	if systemAccount(pid) {
		return true
	}
	p := strings.ToLower(imagePath(pid))
	if p == "" {
		// 打不开 = 没权限 = 别的用户或系统
		return true
	}
	if root := strings.ToLower(os.Getenv("SystemRoot")); root != "" && strings.HasPrefix(p, root+`\`) {
		return true
	}
	return false
}

func systemAccount(pid int) bool {
	h, err := windows.OpenProcess(processQueryLtd, false, uint32(pid))
	if err != nil {
		return true
	}
	defer windows.CloseHandle(h)
	var tok windows.Token
	if windows.OpenProcessToken(h, windows.TOKEN_QUERY, &tok) != nil {
		return true
	}
	defer tok.Close()
	u, err := tok.GetTokenUser()
	if err != nil {
		return true
	}
	for _, w := range []windows.WELL_KNOWN_SID_TYPE{windows.WinLocalSystemSid, windows.WinLocalServiceSid, windows.WinNetworkServiceSid} {
		if u.User.Sid.IsWellKnown(w) {
			return true
		}
	}
	return false
}

// ChildProcess 返回 pid 的一个直接子进程（终端判断「是否有命令在前台运行」用），没有返回 0。
func ChildProcess(pid int) (int, string) {
	s := snapshot()
	born := creationTime(pid)
	for _, c := range s.children[pid] {
		name := strings.ToLower(s.byPID[c].name)
		// 控制台宿主不算
		if name == "conhost.exe" || name == "openconsole.exe" {
			continue
		}
		if cb := creationTime(c); born != 0 && cb != 0 && cb < born {
			continue
		}
		return c, strings.TrimSuffix(s.byPID[c].name, ".exe")
	}
	return 0, ""
}
