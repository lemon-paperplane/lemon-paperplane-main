//go:build windows

package proc

import (
	"fmt"
	"net"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"strconv"
	"testing"
	"time"
)

// 测试二进制自己当子进程：PP_TEST_CHILD=tree 时再起一个孙进程并等 Ctrl+C；=sleep 时睡；=listen 时监听端口。
func TestMain(m *testing.M) {
	if RunHelper(os.Args) {
		return
	}
	PrepareDaemon() // 和 daemon 一样清掉继承来的「忽略 Ctrl+C」
	switch os.Getenv("PP_TEST_CHILD") {
	case "tree":
		gc := exec.Command(os.Args[0])
		gc.Env = append(os.Environ(), "PP_TEST_CHILD=sleep")
		_ = gc.Start()
		_ = os.WriteFile(os.Getenv("PP_TEST_OUT")+".gc", []byte(strconv.Itoa(gc.Process.Pid)), 0o644)
		ch := make(chan os.Signal, 1)
		signal.Notify(ch, os.Interrupt)
		<-ch
		_ = os.WriteFile(os.Getenv("PP_TEST_OUT"), []byte("graceful"), 0o644)
		os.Exit(0)
	case "sleep":
		time.Sleep(10 * time.Minute)
		os.Exit(0)
	case "listen":
		l, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			os.Exit(3)
		}
		_ = os.WriteFile(os.Getenv("PP_TEST_OUT"), []byte(strconv.Itoa(l.Addr().(*net.TCPAddr).Port)), 0o644)
		time.Sleep(10 * time.Minute)
		os.Exit(0)
	}
	os.Exit(m.Run())
}

func startChild(t *testing.T, mode string) (*exec.Cmd, string) {
	t.Helper()
	out := filepath.Join(t.TempDir(), "out")
	cmd := exec.Command(os.Args[0])
	cmd.Env = append(os.Environ(), "PP_TEST_CHILD="+mode, "PP_TEST_OUT="+out)
	Prepare(cmd)
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	Started(cmd)
	return cmd, out
}

func waitFile(t *testing.T, p string) string {
	t.Helper()
	for i := 0; i < 100; i++ {
		if b, err := os.ReadFile(p); err == nil && len(b) > 0 {
			return string(b)
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("没等到 %s", p)
	return ""
}

func TestCtrlCStopsTreeGracefully(t *testing.T) {
	cmd, out := startChild(t, "tree")
	gc, _ := strconv.Atoi(waitFile(t, out+".gc"))
	time.Sleep(300 * time.Millisecond)
	st := TreeStats([]int{cmd.Process.Pid})[cmd.Process.Pid]
	if len(st.PIDs) < 2 || st.RSSMB <= 0 {
		t.Fatalf("进程树统计不对：%+v", st)
	}
	time.Sleep(2100 * time.Millisecond)
	st = TreeStats([]int{cmd.Process.Pid})[cmd.Process.Pid]
	t.Logf("树：%d 个进程，%.1f MB，CPU %.1f%%，%+v", len(st.PIDs), st.RSSMB, st.CPU, st.Procs)
	if err := SignalGroup(cmd.Process.Pid, "SIGTERM"); err != nil {
		t.Fatalf("发 Ctrl+C 失败：%v", err)
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		KillGroup(cmd.Process.Pid)
		t.Fatal("Ctrl+C 后没退出")
	}
	if got := waitFile(t, out); got != "graceful" {
		t.Fatalf("不是正常退出：%q", got)
	}
	// 孙进程（也收到 Ctrl+C 或随 Job 结束）
	KillGroup(cmd.Process.Pid)
	time.Sleep(200 * time.Millisecond)
	if Alive(gc) {
		t.Fatalf("孙进程 %d 还活着", gc)
	}
}

func TestKillGroupKillsDescendants(t *testing.T) {
	cmd, out := startChild(t, "tree")
	gc, _ := strconv.Atoi(waitFile(t, out+".gc"))
	if !Alive(gc) || !Alive(cmd.Process.Pid) {
		t.Fatal("进程没起来")
	}
	KillGroup(cmd.Process.Pid)
	_ = cmd.Wait()
	time.Sleep(200 * time.Millisecond)
	if Alive(gc) || Alive(cmd.Process.Pid) {
		t.Fatal("KillGroup 后还有进程活着")
	}
	if Alive(0) || !IsSystemProcess(4) || IsSystemProcess(os.Getpid()) {
		t.Fatal("Alive / IsSystemProcess 判断不对")
	}
}

func TestListeningPorts(t *testing.T) {
	cmd, out := startChild(t, "listen")
	defer func() { KillGroup(cmd.Process.Pid); _ = cmd.Wait() }()
	port, _ := strconv.Atoi(waitFile(t, out))
	ports := ListeningPorts([]int{cmd.Process.Pid})
	if fmt.Sprint(ports) != fmt.Sprint([]int{port}) {
		t.Fatalf("监听端口 %v，期望 [%d]", ports, port)
	}
	pids := PIDsOnPort(port)
	if len(pids) != 1 || pids[0] != cmd.Process.Pid {
		t.Fatalf("端口 %d 上的进程 %v", port, pids)
	}
	if n := ProcessName(cmd.Process.Pid); n == "" {
		t.Fatal("取不到进程名")
	}
}
