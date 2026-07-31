//go:build windows

// Чтение занятости оперативной памяти (Windows) — через GlobalMemoryStatusEx.
// Функция сразу отдаёт процент использования физической памяти (dwMemoryLoad),
// поэтому считать ничего не нужно.
package main

import (
	"fmt"
	"syscall"
	"unsafe"
)

// memoryStatusEx — структура MEMORYSTATUSEX из Windows API.
type memoryStatusEx struct {
	dwLength                uint32
	dwMemoryLoad            uint32 // занятость физической памяти, проценты
	ullTotalPhys            uint64
	ullAvailPhys            uint64
	ullTotalPageFile        uint64
	ullAvailPageFile        uint64
	ullTotalVirtual         uint64
	ullAvailVirtual         uint64
	ullAvailExtendedVirtual uint64
}

var (
	kernel32                 = syscall.NewLazyDLL("kernel32.dll")
	procGlobalMemoryStatusEx = kernel32.NewProc("GlobalMemoryStatusEx")
)

// memoryUsedPercent возвращает долю занятой памяти в процентах (0..100).
func memoryUsedPercent() (float64, error) {
	status := memoryStatusEx{}
	status.dwLength = uint32(unsafe.Sizeof(status))

	ret, _, err := procGlobalMemoryStatusEx.Call(uintptr(unsafe.Pointer(&status)))
	if ret == 0 {
		return 0, fmt.Errorf("GlobalMemoryStatusEx не отработал: %w", err)
	}
	return float64(status.dwMemoryLoad), nil
}
