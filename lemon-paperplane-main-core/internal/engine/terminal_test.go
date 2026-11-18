//go:build !windows

package engine

import (
	"strings"
	"testing"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/paths"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
)

func newTermEngine(t *testing.T) *Engine {
	t.Helper()
	l, err := paths.New(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	e, err := New(Config{Version: "test", Layout: l})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { e.Close(false) })
	return e
}

func waitOutput(t *testing.T, e *Engine, id, want string) string {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		s, _ := e.TerminalRead(id, 0)
		if strings.Contains(s, want) {
			return s
		}
		time.Sleep(50 * time.Millisecond)
	}
	s, _ := e.TerminalRead(id, 0)
	t.Fatalf("没等到 %q，当前输出：\n%s", want, s)
	return ""
}

func TestTerminalLifecycle(t *testing.T) {
	e := newTermEngine(t)
	dir := t.TempDir()
	term, err := e.TerminalCreate(TerminalCreateParams{Cwd: dir, Name: "测试", Cols: 80, Rows: 24})
	if err != nil {
		t.Fatal(err)
	}
	if term.PID == 0 || term.Name != "测试" {
		t.Fatalf("终端信息不对：%+v", term)
	}
	// 订阅后能收到实时输出
	_, _, _, ch, cancel, err := e.TerminalSubscribe(term.ID, 0)
	if err != nil {
		t.Fatal(err)
	}
	defer cancel()
	if err := e.TerminalInput(term.ID, "echo 纸飞机-$((40+2)); pwd\r"); err != nil {
		t.Fatal(err)
	}
	out := waitOutput(t, e, term.ID, "纸飞机-42")
	if !strings.Contains(out, strings.TrimPrefix(dir, "/private")) {
		t.Fatalf("起始目录不对：\n%s", out)
	}
	got := false
	for !got {
		select {
		case c := <-ch:
			got = len(c.Data) > 0
		case <-time.After(5 * time.Second):
			t.Fatal("订阅没收到输出")
		}
	}
	// 空闲时不忙；跑着 sleep 时忙，名字是 sleep
	if busy, _, _ := e.TerminalBusy(term.ID); busy {
		t.Fatal("空闲的终端被判成忙")
	}
	_ = e.TerminalInput(term.ID, "sleep 30\r")
	deadline0 := time.Now().Add(5 * time.Second)
	busyName := ""
	for time.Now().Before(deadline0) {
		if b, n, _ := e.TerminalBusy(term.ID); b {
			busyName = n
			break
		}
		time.Sleep(50 * time.Millisecond)
	}
	if busyName != "sleep" {
		t.Fatalf("前台命令应为 sleep，实际 %q", busyName)
	}
	_ = e.TerminalInput(term.ID, "\x03") // Ctrl-C
	deadline0 = time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline0) {
		if b, _, _ := e.TerminalBusy(term.ID); !b {
			break
		}
		time.Sleep(50 * time.Millisecond)
	}
	if b, _, _ := e.TerminalBusy(term.ID); b {
		t.Fatal("Ctrl-C 后仍然判成忙")
	}
	// 调整大小后 stty 能看到新尺寸
	if err := e.TerminalResize(term.ID, 120, 40); err != nil {
		t.Fatal(err)
	}
	_ = e.TerminalInput(term.ID, "stty size\r")
	waitOutput(t, e, term.ID, "40 120")
	// 同名自动加序号
	t2, err := e.TerminalCreate(TerminalCreateParams{Cwd: dir, Name: "测试"})
	if err != nil {
		t.Fatal(err)
	}
	if t2.Name != "测试 2" {
		t.Fatalf("重名没加序号：%s", t2.Name)
	}
	if n := len(e.TerminalList("")); n != 2 {
		t.Fatalf("应有 2 个终端，实际 %d", n)
	}
	// 输入 exit 正常退出：自动关闭
	_ = e.TerminalInput(t2.ID, "exit\r")
	deadline := time.Now().Add(10 * time.Second)
	for e.TerminalExists(t2.ID) && time.Now().Before(deadline) {
		time.Sleep(50 * time.Millisecond)
	}
	if e.TerminalExists(t2.ID) {
		t.Fatal("exit 后终端没有自动关闭")
	}
	// 非 0 退出：保留并标记已退出，可以重新启动
	_ = e.TerminalInput(term.ID, "exit 3\r")
	deadline = time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		if l := e.TerminalList(""); len(l) == 1 && l[0].Exited {
			break
		}
		time.Sleep(50 * time.Millisecond)
	}
	l := e.TerminalList("")
	if len(l) != 1 || !l[0].Exited || l[0].ExitCode != 3 {
		t.Fatalf("非 0 退出状态不对：%+v", l)
	}
	if _, err := e.TerminalRestart(term.ID); err != nil {
		t.Fatal(err)
	}
	_ = e.TerminalInput(term.ID, "echo again-$((1+1))\r")
	waitOutput(t, e, term.ID, "again-2")
	// 关闭：进程组收到挂断
	pid := e.TerminalList("")[0].PID
	if err := e.TerminalClose(term.ID); err != nil {
		t.Fatal(err)
	}
	if e.TerminalExists(term.ID) || len(e.TerminalList("")) != 0 {
		t.Fatal("关闭后还在列表里")
	}
	deadline = time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) && proc.Alive(pid) {
		time.Sleep(50 * time.Millisecond)
	}
	if proc.Alive(pid) {
		t.Fatalf("shell 进程 %d 没有结束", pid)
	}
}

func TestStripTerminal(t *testing.T) {
	in := "\x1b]0;title\x07\x1b[1;32mok\x1b[0m done\r\nabc\rX\n\x1b[2Kprogress 10%\rprogress 100%\n"
	got := stripTerminal([]byte(in))
	want := "ok done\nXbc\nprogress 100%"
	if got != want {
		t.Fatalf("got %q want %q", got, want)
	}
}
