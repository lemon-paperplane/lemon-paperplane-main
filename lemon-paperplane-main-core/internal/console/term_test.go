package console

import (
	"fmt"
	"strings"
	"testing"
	"time"
)

type fakeClock struct{ t time.Time }

func (f *fakeClock) now() time.Time      { return f.t }
func (f *fakeClock) add(d time.Duration) { f.t = f.t.Add(d) }
func newTerm(t *testing.T) (*Console, *TermWriter, *fakeClock) {
	t.Helper()
	c := New("t", 1000, "")
	w := c.Terminal(nil)
	clk := &fakeClock{t: time.Unix(1700000000, 0)}
	w.now = clk.now
	t.Cleanup(func() {
		if w.timer != nil {
			w.timer.Stop()
		}
	})
	return c, w, clk
}

func committed(c *Console) []string {
	var out []string
	for _, l := range c.Since(0) {
		out = append(out, StripANSI(l.Text))
	}
	return out
}

func liveText(c *Console) []string {
	live, _, _ := c.Live()
	var out []string
	for _, l := range live {
		out = append(out, StripANSI(l.Text))
	}
	return out
}

func eq(t *testing.T, what string, got, want []string) {
	t.Helper()
	if strings.Join(got, "\n") != strings.Join(want, "\n") {
		t.Fatalf("%s:\n got  %q\n want %q", what, got, want)
	}
}

// 伪终端会把 \n 转成 \r\n（ONLCR）
func crlf(s string) string { return strings.ReplaceAll(s, "\n", "\r\n") }

func TestTermPlainLinesCommitAfterGrace(t *testing.T) {
	c, w, clk := newTerm(t)
	w.Write([]byte(crlf("hello\nworld\npartial")))
	eq(t, "committed before grace", committed(c), nil)
	eq(t, "live before grace", liveText(c), []string{"hello", "world", "partial"})
	clk.add(250 * time.Millisecond)
	w.tick()
	eq(t, "committed after grace", committed(c), []string{"hello", "world"})
	eq(t, "live after grace", liveText(c), []string{"partial"})
	w.Close()
	eq(t, "committed after close", committed(c), []string{"hello", "world", "partial"})
	eq(t, "live after close", liveText(c), nil)
}

func TestTermCarriageReturnProgress(t *testing.T) {
	c, w, clk := newTerm(t)
	for i := 0; i <= 100; i += 25 {
		w.Write([]byte(fmt.Sprintf("\rDownloading %3d%%", i)))
		eq(t, "live", liveText(c), []string{fmt.Sprintf("Downloading %3d%%", i)})
	}
	w.Write([]byte("\r\n"))
	clk.add(time.Second)
	w.tick()
	eq(t, "committed", committed(c), []string{"Downloading 100%"})
	eq(t, "live", liveText(c), nil)
}

// docker push 在终端里：每层一行，新层先换行，再上移 diff 行、清行、写状态、下移回来。
func dockerPush(w *TermWriter, clk *fakeClock, c *Console, t *testing.T) {
	ids := map[string]int{}
	var order []string
	write := func(s string) { w.Write([]byte(s)) }
	update := func(id, status string) {
		line, ok := ids[id]
		if !ok {
			line = len(ids)
			ids[id] = line
			order = append(order, id)
			write("\r\n")
		}
		diff := len(ids) - line
		write(fmt.Sprintf("\x1b[%dA", diff))
		write("\x1b[2K\r" + id + ": " + status + "\r")
		write(fmt.Sprintf("\x1b[%dB", diff))
		clk.add(50 * time.Millisecond)
		w.tick()
	}
	write("The push refers to repository [ccr.example.com/app]\r\n")
	clk.add(300 * time.Millisecond)
	w.tick()
	for _, id := range []string{"aaa", "bbb", "ccc"} {
		update(id, "Preparing")
	}
	for _, id := range []string{"aaa", "bbb", "ccc"} {
		update(id, "Waiting")
	}
	for i := 1; i <= 5; i++ {
		update("aaa", fmt.Sprintf("Pushing [%s>%s] %dMB/5MB", strings.Repeat("=", i*4), strings.Repeat(" ", 20-i*4), i))
	}
	eq(t, "live while pushing", liveText(c), []string{"aaa: Pushing [====================>] 5MB/5MB", "bbb: Waiting", "ccc: Waiting"})
	update("aaa", "Pushed")
	update("bbb", "Layer already exists")
	update("ccc", "Pushed")
	write("latest: digest: sha256:abc size: 856\r\n")
}

func TestTermDockerPush(t *testing.T) {
	c, w, clk := newTerm(t)
	dockerPush(w, clk, c, t)
	// 刷新刚停：还在等安静期，进度行不进日志
	eq(t, "committed right after", committed(c), []string{"The push refers to repository [ccr.example.com/app]"})
	clk.add(4 * time.Second)
	w.tick()
	eq(t, "committed after quiet", committed(c), []string{
		"The push refers to repository [ccr.example.com/app]",
		"aaa: Pushed",
		"bbb: Layer already exists",
		"ccc: Pushed",
		"latest: digest: sha256:abc size: 856",
	})
	eq(t, "live", liveText(c), nil)
}

func TestTermDockerPushCloseImmediately(t *testing.T) {
	c, w, clk := newTerm(t)
	dockerPush(w, clk, c, t)
	w.Close()
	eq(t, "committed", committed(c), []string{
		"The push refers to repository [ccr.example.com/app]",
		"aaa: Pushed",
		"bbb: Layer already exists",
		"ccc: Pushed",
		"latest: digest: sha256:abc size: 856",
	})
}

// buildkit 终端模式：每帧上移（上一帧行数 + 1），第一帧先上一下再下一下；行用空格补到终端宽度。
func TestTermBuildkitFrames(t *testing.T) {
	c, w, clk := newTerm(t)
	w.Write([]byte("BUILD SUCCESSFUL in 39s\r\n"))
	clk.add(time.Second)
	w.tick()
	pad := func(s string) string { return s + strings.Repeat(" ", TermCols-len(s)) }
	lineCount, repeated := 0, false
	frame := func(header string, jobs []string) {
		var b strings.Builder
		up := lineCount + 1
		b.WriteString(fmt.Sprintf("\x1b[%dA", up))
		if !repeated {
			b.WriteString("\x1b[1B")
		}
		repeated = true
		b.WriteString("\x1b[0G\x1b[?25l")
		b.WriteString(pad(header) + "\r\n")
		for _, j := range jobs {
			b.WriteString(pad(j) + "\r\n")
		}
		if diff := lineCount - len(jobs); diff > 0 {
			for i := 0; i < diff; i++ {
				b.WriteString(pad("") + "\r\n")
			}
			b.WriteString(fmt.Sprintf("\x1b[%dA\x1b[0G", diff))
		}
		b.WriteString("\x1b[?25h")
		lineCount = len(jobs)
		w.Write([]byte(b.String()))
		clk.add(100 * time.Millisecond)
		w.tick()
	}
	frame("[+] Building 0.1s (1/4)", []string{" => [internal] load build definition", " => [1/4] FROM eclipse-temurin"})
	frame("[+] Building 0.2s (2/4)", []string{" => [internal] load build definition", " => [1/4] FROM eclipse-temurin", " => [2/4] ADD app.jar"})
	eq(t, "live", liveText(c), []string{"[+] Building 0.2s (2/4)", " => [internal] load build definition", " => [1/4] FROM eclipse-temurin", " => [2/4] ADD app.jar"})
	frame("[+] Building 1.0s (4/4) FINISHED", []string{" => [internal] load build definition", " => exporting to image"})
	w.Write([]byte(crlf("1 warning found\n")))
	w.Close()
	eq(t, "committed", committed(c), []string{
		"BUILD SUCCESSFUL in 39s",
		"[+] Building 1.0s (4/4) FINISHED",
		" => [internal] load build definition",
		" => exporting to image",
		"1 warning found",
	})
}

// Vite 清屏：先打 rows-2 个空行，再光标回左上角、清到屏幕末尾，再打横幅。
func TestTermClearScreen(t *testing.T) {
	c, w, clk := newTerm(t)
	w.Write([]byte(crlf("\n> app@0.0.0 dev\n> vite\n\n")))
	clk.add(time.Second)
	w.tick()
	w.Write([]byte(crlf(strings.Repeat("\n", TermRows-2) + "\n")))
	w.Write([]byte("\x1b[1;1H\x1b[0J"))
	w.Write([]byte(crlf("  VITE v5.4.0  ready in 300 ms\n\n  ➜  Local:   http://localhost:5173/\n")))
	clk.add(4 * time.Second)
	w.tick()
	got := committed(c)
	blank := 0
	for _, l := range got {
		if l == "" {
			blank++
		}
	}
	if blank > 3 {
		t.Fatalf("too many blank lines (%d): %q", blank, got)
	}
	if got[len(got)-1] != "  ➜  Local:   http://localhost:5173/" || !strings.Contains(strings.Join(got, "\n"), "VITE v5.4.0") || !strings.Contains(strings.Join(got, "\n"), "> vite") {
		t.Fatalf("unexpected: %q", got)
	}
}

// log-update 一类：先写一帧带换行，下一帧（宽限期内）擦掉重画，第一帧不该残留在日志里。
func TestTermEraseLinesWithinGrace(t *testing.T) {
	c, w, clk := newTerm(t)
	frames := []string{"⠋ Installing", "⠙ Installing", "⠹ Installing", "✔ Installed"}
	for i, f := range frames {
		if i > 0 {
			w.Write([]byte("\x1b[2K\x1b[1A\x1b[2K\x1b[G"))
		}
		w.Write([]byte(f + "\r\n"))
		clk.add(80 * time.Millisecond)
		w.tick()
	}
	clk.add(4 * time.Second)
	w.tick()
	eq(t, "committed", committed(c), []string{"✔ Installed"})
}

func TestTermStyleAndWide(t *testing.T) {
	c, w, _ := newTerm(t)
	w.Write([]byte("\x1b[1;31m错误\x1b[0m: 构建失败 \x1b[38;5;208mwarn\x1b[m\r\n"))
	w.Close()
	lines := c.Since(0)
	if len(lines) != 1 {
		t.Fatalf("lines: %q", committed(c))
	}
	want := "\x1b[0;1;31m错误\x1b[0m: 构建失败 \x1b[0;38;5;208mwarn\x1b[0m"
	if lines[0].Text != want {
		t.Fatalf("text:\n got  %q\n want %q", lines[0].Text, want)
	}
	// 宽字符占两格：光标回退两格覆盖第二个字
	c2, w2, _ := newTerm(t)
	w2.Write([]byte("中文\x1b[2D字\r\n"))
	w2.Close()
	eq(t, "wide", committed(c2), []string{"中字"})
}

func TestTermCursorPositionReply(t *testing.T) {
	c := New("t", 100, "")
	got := make(chan string, 1)
	w := c.Terminal(func(b []byte) { got <- string(b) })
	w.Write([]byte("abc\x1b[6n"))
	select {
	case s := <-got:
		if s != "\x1b[1;4R" {
			t.Fatalf("reply %q", s)
		}
	case <-time.After(time.Second):
		t.Fatal("no reply")
	}
	w.Close()
}

func TestTermSplitUTF8AndEscapes(t *testing.T) {
	c, w, _ := newTerm(t)
	data := []byte("\x1b[32m你好\x1b[0m世界\r\n")
	for _, b := range data {
		w.Write([]byte{b})
	}
	w.Close()
	eq(t, "committed", committed(c), []string{"你好世界"})
}

func TestTermScrollOffCommitsInOrder(t *testing.T) {
	c, w, _ := newTerm(t)
	var b strings.Builder
	for i := 1; i <= TermRows*3; i++ {
		fmt.Fprintf(&b, "line %d\r\n", i)
	}
	w.Write([]byte(b.String()))
	got := committed(c)
	if len(got) != TermRows*3-TermRows+1 || got[0] != "line 1" {
		t.Fatalf("scrolled %d lines, first %q", len(got), got[0])
	}
	w.Close()
	got = committed(c)
	if len(got) != TermRows*3 || got[len(got)-1] != fmt.Sprintf("line %d", TermRows*3) {
		t.Fatalf("after close %d lines", len(got))
	}
	for i, l := range got {
		if l != fmt.Sprintf("line %d", i+1) {
			t.Fatalf("order broken at %d: %q", i, l)
		}
	}
}

func TestTermAltScreenNotLogged(t *testing.T) {
	c, w, _ := newTerm(t)
	w.Write([]byte("before\r\n\x1b[?1049h\x1b[Hpager screen\x1b[?1049l"))
	w.Write([]byte("after\r\n"))
	w.Close()
	eq(t, "committed", committed(c), []string{"before", "after"})
}
