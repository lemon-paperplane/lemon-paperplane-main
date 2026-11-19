//go:build !darwin && !windows

package remote

import "errors"

// Linux 无头版用 systemd 管理开机启动（见 lemon-paperplane-main-linux-headless/README.md）。
const autostartSupported = false

func setAutostart(exe string, on bool) error {
	if on {
		return errors.New("not supported")
	}
	return nil
}
