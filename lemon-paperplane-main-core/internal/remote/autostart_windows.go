package remote

import (
	"errors"
	"github.com/lemon-paperplane/lemon-paperplane-main/lemon-paperplane-main-core/internal/i18n"
	"os"

	"golang.org/x/sys/windows/registry"
)

const autostartSupported = true

const runValue = "LemonPaperplaneService"

// setAutostart：HKCU\…\Run，登录时以 `paperplane.exe daemon --detach` 启动后台服务（不需要管理员权限）。
func setAutostart(exe string, on bool) error {
	k, err := registry.OpenKey(registry.CURRENT_USER, `Software\Microsoft\Windows\CurrentVersion\Run`, registry.SET_VALUE|registry.QUERY_VALUE)
	if err != nil {
		return err
	}
	defer k.Close()
	if !on {
		if err := k.DeleteValue(runValue); err != nil && err != registry.ErrNotExist {
			return err
		}
		return nil
	}
	if exe == "" {
		return errors.New(i18n.T("remote.noExe"))
	}
	if os.Getenv("PAPERPLANE_DATA_DIR") != "" {
		// Run 键不能带环境变量：数据目录不是默认值（测试环境）时不支持
		return errors.New(i18n.T("remote.autostartDataDir"))
	}
	return k.SetStringValue(runValue, `"`+exe+`" daemon --detach`)
}
