package engine

import (
	"errors"
	"os"
	"os/exec"
)

// Windows 上运行进程仍用管道（ConPTY 会重绘整屏、改写输出，先不用于运行）。
func startRunPTY(cmd *exec.Cmd) (*os.File, error) {
	return nil, errors.New("pseudo terminal runs are not supported on Windows")
}

func runPTYSupported() bool { return false }
