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
	st, err := memoryStatus()
	if err != nil {
		return 0, err
	}
	return st.UsedPercent, nil
}

// MemoryStatus — снимок состояния памяти.
type MemoryStatus struct {
	UsedPercent    float64 // занято, проценты
	AvailableBytes uint64  // доступно под новые процессы
	TotalBytes     uint64  // всего физической памяти
}

// memoryStatus читает состояние памяти целиком.
func memoryStatus() (MemoryStatus, error) {
	status := memoryStatusEx{}
	status.dwLength = uint32(unsafe.Sizeof(status))

	ret, _, err := procGlobalMemoryStatusEx.Call(uintptr(unsafe.Pointer(&status)))
	if ret == 0 {
		return MemoryStatus{}, fmt.Errorf("GlobalMemoryStatusEx не отработал: %w", err)
	}
	return MemoryStatus{
		UsedPercent:    float64(status.dwMemoryLoad),
		AvailableBytes: status.ullAvailPhys,
		TotalBytes:     status.ullTotalPhys,
	}, nil
}
