//go:build windows

package ops

import (
	"path/filepath"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// installCLIWindows：Windows 上「安装命令行工具」= 把 paperplane.exe 所在目录加到用户 PATH（HKCU\Environment），
// 并广播环境变量变化，新打开的终端就能直接用 paperplane。已经在 PATH 里就什么也不改。
func installCLIWindows(exe string) (any, error) {
	dir := filepath.Dir(exe)
	k, err := registry.OpenKey(registry.CURRENT_USER, `Environment`, registry.QUERY_VALUE|registry.SET_VALUE)
	if err != nil {
		return nil, err
	}
	defer k.Close()
	cur, typ, err := k.GetStringValue("Path")
	if err != nil && err != registry.ErrNotExist {
		return nil, err
	}
	for _, p := range filepath.SplitList(cur) {
		if strings.EqualFold(strings.TrimRight(strings.TrimSpace(p), `\`), strings.TrimRight(dir, `\`)) {
			return map[string]any{"path": filepath.Join(dir, "paperplane.exe"), "inPath": true}, nil
		}
	}
	next := dir
	if strings.TrimSpace(cur) != "" {
		next = strings.TrimRight(cur, ";") + ";" + dir
	}
	if typ == registry.SZ {
		err = k.SetStringValue("Path", next)
	} else {
		err = k.SetExpandStringValue("Path", next)
	}
	if err != nil {
		return nil, err
	}
	broadcastEnvChange()
	return map[string]any{"path": filepath.Join(dir, "paperplane.exe"), "inPath": true}, nil
}

// broadcastEnvChange 通知资源管理器等重新读取环境变量（WM_SETTINGCHANGE "Environment"）。
func broadcastEnvChange() {
	const hwndBroadcast = 0xffff
	const wmSettingChange = 0x001A
	const smtoAbortIfHung = 0x0002
	env, _ := windows.UTF16PtrFromString("Environment")
	var result uintptr
	_, _, _ = windows.NewLazySystemDLL("user32.dll").NewProc("SendMessageTimeoutW").Call(
		hwndBroadcast, wmSettingChange, 0, uintptr(unsafe.Pointer(env)), smtoAbortIfHung, 3000, uintptr(unsafe.Pointer(&result)))
}
