//go:build windows

package console

import (
	"unicode/utf8"

	"golang.org/x/sys/windows"
)

const cpOEM = 1 // CP_OEMCP

// decodeLine：Windows 上 cmd.exe 的提示、老程序的输出是控制台代码页（中文系统是 GBK），
// 不是合法 UTF-8 的行按系统 OEM 代码页转码，避免日志里出现乱码。合法 UTF-8 原样保留。
func decodeLine(b []byte) string {
	if utf8.Valid(b) || len(b) == 0 {
		return string(b)
	}
	n, err := windows.MultiByteToWideChar(cpOEM, 0, &b[0], int32(len(b)), nil, 0)
	if err != nil || n <= 0 {
		return string(b)
	}
	w := make([]uint16, n)
	if _, err := windows.MultiByteToWideChar(cpOEM, 0, &b[0], int32(len(b)), &w[0], n); err != nil {
		return string(b)
	}
	return windows.UTF16ToString(w)
}
