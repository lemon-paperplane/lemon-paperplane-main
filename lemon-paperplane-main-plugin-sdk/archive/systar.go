package archive

import (
	"fmt"
	"os/exec"
)

// tar.xz 用系统 tar 解（macOS/Linux/Windows 10 1803+ 自带 bsdtar），避免引入 xz 依赖。
func extractWithSystemTar(file, dest string, strip int) error {
	args := []string{"-xf", file, "-C", dest}
	if strip > 0 {
		args = append(args, fmt.Sprintf("--strip-components=%d", strip))
	}
	out, err := exec.Command("tar", args...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("tar: %v: %s", err, out)
	}
	return nil
}
