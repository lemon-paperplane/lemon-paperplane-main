//go:build windows

package main

import (
	"os/exec"
	"strconv"
)

func setPgid(cmd *exec.Cmd) {}

func killGroup(cmd *exec.Cmd) {
	if cmd.Process == nil {
		return
	}
	_ = exec.Command("taskkill", "/T", "/F", "/PID", strconv.Itoa(cmd.Process.Pid)).Run()
}
