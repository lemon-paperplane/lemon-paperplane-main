package sdk

import (
	"os"
	"path/filepath"
	"testing"
)

func TestIsGeneratedDir(t *testing.T) {
	root := t.TempDir()
	unity := filepath.Join(root, "game")
	for _, d := range []string{"Assets", "ProjectSettings", "Library/PackageCache/com.x", "Temp"} {
		_ = os.MkdirAll(filepath.Join(unity, d), 0o755)
	}
	_ = os.MkdirAll(filepath.Join(root, "mac", "Library"), 0o755)
	if !IsGeneratedDir(unity, "Library") || !IsGeneratedDir(unity, "Temp") {
		t.Fatal("unity generated dirs not skipped")
	}
	if IsGeneratedDir(unity, "Assets") {
		t.Fatal("Assets must not be skipped")
	}
	if IsGeneratedDir(filepath.Join(root, "mac"), "Library") {
		t.Fatal("Library outside a Unity project must not be skipped")
	}
	if !IsGeneratedDir(root, "node_modules") {
		t.Fatal("default skip list")
	}
	_ = os.WriteFile(filepath.Join(unity, "Library", "PackageCache", "com.x", "package.json"), []byte("{}"), 0o644)
	_ = os.WriteFile(filepath.Join(unity, "Assets", "package.json"), []byte("{}"), 0o644)
	got := FindFiles(root, FindOptions{MaxDepth: 6, Names: []string{"package.json"}})
	if len(got) != 1 || filepath.Base(filepath.Dir(got[0])) != "Assets" {
		t.Fatalf("FindFiles should skip Unity Library: %v", got)
	}
}
