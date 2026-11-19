//go:build !windows

package engine

import "os/exec"

func openWithWindows(terminal, with, dir string) *exec.Cmd { return exec.Command("true") }
