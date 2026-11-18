//go:build windows

package engine

import (
	"errors"
	"os/exec"
)

// Windows 版终端走 ConPTY，留到 Windows 端一起做。
func startPTY(cmd *exec.Cmd, cols, rows int) (ptyHandle, error) {
	return nil, errors.New("terminal not supported on windows yet")
}

func terminalSupported() bool { return false }
