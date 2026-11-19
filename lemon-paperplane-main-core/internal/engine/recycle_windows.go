//go:build windows

package engine

import (
	"fmt"
	"path/filepath"
	"unsafe"

	"golang.org/x/sys/windows"
)

// recycleWindows 把文件或文件夹移到回收站（SHFileOperationW + FOF_ALLOWUNDO），不弹任何确认或错误框。
func recycleWindows(abs string) error {
	const (
		foDelete          = 0x0003
		fofSilent         = 0x0004
		fofNoConfirmation = 0x0010
		fofAllowUndo      = 0x0040
		fofNoErrorUI      = 0x0400
	)
	// pFrom 是以两个 \0 结尾的路径列表
	from, err := windows.UTF16FromString(filepath.Clean(abs))
	if err != nil {
		return err
	}
	from = append(from, 0)
	op := struct {
		hwnd                  uintptr
		wFunc                 uint32
		pFrom                 *uint16
		pTo                   *uint16
		fFlags                uint16
		fAnyOperationsAborted int32
		hNameMappings         uintptr
		lpszProgressTitle     *uint16
	}{wFunc: foDelete, pFrom: &from[0], fFlags: fofAllowUndo | fofNoConfirmation | fofNoErrorUI | fofSilent}
	r, _, _ := windows.NewLazySystemDLL("shell32.dll").NewProc("SHFileOperationW").Call(uintptr(unsafe.Pointer(&op)))
	if r != 0 {
		return fmt.Errorf("SHFileOperation failed: 0x%x", r)
	}
	if op.fAnyOperationsAborted != 0 {
		return fmt.Errorf("move to Recycle Bin was aborted")
	}
	return nil
}
