//go:build windows

package engine

import (
	"os"
	"strings"
	"testing"
	"time"

	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/paths"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/proc"
)

func TestMain(m *testing.M) {
	if proc.RunHelper(os.Args) {
		return
	}
	proc.PrepareDaemon()
	os.Exit(m.Run())
}

func newWinTermEngine(t *testing.T) *Engine {
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

func waitTermOutput(t *testing.T, e *Engine, id, want string) string {
	t.Helper()
	deadline := time.Now().Add(20 * time.Second)
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

func TestConPTYTerminal(t *testing.T) {
	e := newWinTermEngine(t)
	dir := t.TempDir()
	term, err := e.TerminalCreate(TerminalCreateParams{Cwd: dir, Name: "测试", Cols: 100, Rows: 30})
	if err != nil {
		t.Fatal(err)
	}
	t.Logf("shell: %s pid %d", term.Shell, term.PID)
	if err := e.TerminalInput(term.ID, "echo ('纸飞机-' + (40+2)); (Get-Location).Path\r"); err != nil {
		t.Fatal(err)
	}
	out := waitTermOutput(t, e, term.ID, "纸飞机-42")
	if !strings.Contains(strings.ToLower(out), strings.ToLower(dir)) {
		t.Fatalf("起始目录不对：\n%s", out)
	}
	if busy, _, _ := e.TerminalBusy(term.ID); busy {
		t.Fatal("空闲的终端被判成忙")
	}
	_ = e.TerminalInput(term.ID, "ping -n 30 127.0.0.1\r")
	deadline := time.Now().Add(10 * time.Second)
	busy, name := false, ""
	for time.Now().Before(deadline) && !busy {
		busy, name, _ = e.TerminalBusy(term.ID)
		time.Sleep(100 * time.Millisecond)
	}
	if !busy || !strings.EqualFold(name, "ping") {
		t.Fatalf("跑着 ping 时应该忙：busy=%v name=%q", busy, name)
	}
	if err := e.TerminalResize(term.ID, 120, 40); err != nil {
		t.Fatal(err)
	}
	pid := term.PID
	if err := e.TerminalClose(term.ID); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 50 && proc.Alive(pid); i++ {
		time.Sleep(100 * time.Millisecond)
	}
	if proc.Alive(pid) {
		t.Fatal("关闭后 shell 还活着")
	}
}

func TestConPTYExitClosesTerminal(t *testing.T) {
	e := newWinTermEngine(t)
	term, err := e.TerminalCreate(TerminalCreateParams{Cwd: t.TempDir(), Cols: 80, Rows: 24})
	if err != nil {
		t.Fatal(err)
	}
	waitTermOutput(t, e, term.ID, ">")
	_ = e.TerminalInput(term.ID, "exit 3\r")
	deadline := time.Now().Add(10 * time.Second)
	for time.Now().Before(deadline) {
		for _, x := range e.TerminalList("") {
			if x.ID == term.ID && x.Exited {
				if x.ExitCode != 3 {
					t.Fatalf("退出码 %d", x.ExitCode)
				}
				return
			}
		}
		time.Sleep(100 * time.Millisecond)
	}
	t.Fatal("exit 3 后终端没标记退出")
}
