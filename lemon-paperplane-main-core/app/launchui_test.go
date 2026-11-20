package app

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestUIExecutable(t *testing.T) {
	dir := t.TempDir()
	switch runtime.GOOS {
	case "darwin":
		macos := filepath.Join(dir, "Lemon Paperplane.app", "Contents", "MacOS")
		_ = os.MkdirAll(macos, 0o755)
		for _, n := range []string{"paperplane", "paperplane-plugins", "Lemon Paperplane"} {
			_ = os.WriteFile(filepath.Join(macos, n), nil, 0o755)
		}
		if got := uiExecutable(filepath.Join(macos, "paperplane")); got != filepath.Join(macos, "Lemon Paperplane") {
			t.Fatalf("ui: %q", got)
		}
		// 开发时 go build 出来的内核旁边没有界面
		if got := uiExecutable(filepath.Join(dir, "paperplane")); got != "" {
			t.Fatalf("dev build: %q", got)
		}
	case "windows":
		_ = os.WriteFile(filepath.Join(dir, "LemonPaperplane.exe"), nil, 0o755)
		if got := uiExecutable(filepath.Join(dir, "paperplane.exe")); got != filepath.Join(dir, "LemonPaperplane.exe") {
			t.Fatalf("ui: %q", got)
		}
	default:
		if got := uiExecutable(filepath.Join(dir, "paperplane")); got != "" {
			t.Fatalf("linux should never launch a UI: %q", got)
		}
	}
}

func TestUIRunning(t *testing.T) {
	if runtime.GOOS != "darwin" {
		t.Skip("darwin only")
	}
	self, _ := os.Executable()
	if !uiRunning(self) {
		t.Fatal("own process not found")
	}
	if uiRunning("/Applications/No Such App.app/Contents/MacOS/No Such App") {
		t.Fatal("nonexistent app reported running")
	}
}
