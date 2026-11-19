//go:build windows

package i18n

import "golang.org/x/sys/windows"

// platformLocale 按 Windows 显示语言（不是区域格式）决定：中文界面用 zh-Hans，其余 en。
func platformLocale() string {
	r, _, _ := windows.NewLazySystemDLL("kernel32.dll").NewProc("GetUserDefaultUILanguage").Call()
	if r == 0 {
		return ""
	}
	if r&0x3ff == 0x04 { // LANG_CHINESE
		return "zh-Hans"
	}
	return "en"
}
