package console

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
	"unicode/utf8"
)

func TestRingAndWriter(t *testing.T) {
	c := New("x", 3, "")
	w := c.Writer("stdout")
	_, _ = w.Write([]byte("a\nb\r\nprogress 10%\rprogress 100%\nc"))
	w.Flush()
	lines := c.Tail(10)
	if len(lines) != 3 {
		t.Fatalf("ring cap: %d", len(lines))
	}
	if lines[0].Text != "b" || lines[1].Text != "progress 100%" || lines[2].Text != "c" {
		t.Fatalf("lines: %+v", lines)
	}
	if got := c.Since(lines[1].Seq); len(got) != 1 || got[0].Text != "c" {
		t.Fatalf("since: %+v", got)
	}
	if StripANSI("\x1b[32mok\x1b[0m \x1b]8;;http://x\x07link\x1b]8;;\x07") != "ok link" {
		t.Fatalf("strip: %q", StripANSI("\x1b[32mok\x1b[0m"))
	}
}

func TestLongLineTruncatedForDisplayOnly(t *testing.T) {
	dir := t.TempDir()
	logFile := filepath.Join(dir, "run.log")
	c := New("x", 10, logFile)
	long := strings.Repeat("中", 3000) // 9000 字节
	c.Append("stdout", long)
	c.Close()
	got := c.Tail(1)[0].Text
	if len(got) > maxDisplayBytes+300 || !strings.HasPrefix(got, strings.Repeat("中", 1333)) || !utf8.ValidString(got) {
		t.Fatalf("display not truncated well: %d bytes", len(got))
	}
	b, _ := os.ReadFile(logFile)
	if !strings.Contains(string(b), long) {
		t.Fatal("log file must keep the full line")
	}
}
