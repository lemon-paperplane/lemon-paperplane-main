package engine

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestRunnableKind(t *testing.T) {
	dir := t.TempDir()
	mk := func(name string, body []byte, mode os.FileMode) string {
		p := filepath.Join(dir, name)
		_ = os.MkdirAll(filepath.Dir(p), 0o755)
		if err := os.WriteFile(p, body, mode); err != nil {
			t.Fatal(err)
		}
		return p
	}
	cases := []struct {
		name string
		body []byte
		mode os.FileMode
		want string
	}{
		{"deploy.sh", []byte("echo hi\n"), 0o644, RunnableScript},             // shell 扩展名，没有执行权限也算
		{"gradlew", []byte("#!/bin/sh\n"), 0o755, RunnableScript},             // 没有扩展名但有 #!
		{"tool", []byte{0xcf, 0xfa, 0xed, 0xfe, 0, 0}, 0o755, RunnableBinary}, // Mach-O + 执行权限
		{"blob", []byte{0xcf, 0xfa, 0xed, 0xfe}, 0o644, ""},                   // 没有执行权限的程序文件不算
		{"README.md", []byte("#!/bin/sh\n# doc\n"), 0o755, ""},                // Markdown 带执行权限、甚至 #! 也不算
		{"config.json", []byte("{}"), 0o755, ""},
		{"logo.png", []byte{0x89, 'P', 'N', 'G'}, 0o755, ""},
		{"notes", []byte("plain text"), 0o755, ""},                      // 有执行权限但既不是 #! 也不是程序
		{"scripts/job.py", []byte("print(1)\n"), 0o644, RunnableSource}, // 源码：选择框默认不列
		{"scripts/run.py", []byte("#!/usr/bin/env python3\n"), 0o644, RunnableScript},
		{"vite.config.ts", []byte("export default {}"), 0o644, RunnableSource},
		{"env.d.ts", []byte("declare"), 0o644, ""},
		{"app.jar", []byte("PK"), 0o644, RunnableSource},
	}
	if runtime.GOOS == "windows" {
		// Windows 没有执行权限位：程序看扩展名
		for i := range cases {
			if cases[i].name == "tool" {
				cases[i].want = ""
			}
		}
		cases = append(cases, struct {
			name string
			body []byte
			mode os.FileMode
			want string
		}{"tool.exe", []byte("MZ"), 0o644, RunnableBinary}, struct {
			name string
			body []byte
			mode os.FileMode
			want string
		}{"build.bat", []byte("@echo off"), 0o644, RunnableScript})
	}
	for _, c := range cases {
		p := mk(c.name, c.body, c.mode)
		fi, _ := os.Stat(p)
		if got := runnableKind(p, fi); got != c.want {
			t.Errorf("%s: got %q want %q", c.name, got, c.want)
		}
	}
}
