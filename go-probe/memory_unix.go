//go:build !windows

// Чтение занятости оперативной памяти (Linux/Unix) — по /proc/meminfo.
//
// Берём MemAvailable, а не MemFree: ядро считает доступной ещё и память,
// занятую кешем и буферами, которую можно освободить под новые процессы.
// Именно она определяет, получится ли запустить очередной зонд.
package main

import (
	"bufio"
	"fmt"
	"os"
	"strconv"
	"strings"
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
	file, err := os.Open("/proc/meminfo")
	if err != nil {
		return MemoryStatus{}, fmt.Errorf("не удалось открыть /proc/meminfo: %w", err)
	}
	defer file.Close()

	var total, available float64
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		key, value, ok := strings.Cut(scanner.Text(), ":")
		if !ok {
			continue
		}
		switch key {
		case "MemTotal":
			total = parseMeminfoKB(value)
		case "MemAvailable":
			available = parseMeminfoKB(value)
		}
		if total > 0 && available > 0 {
			break
		}
	}

	if total <= 0 {
		return MemoryStatus{}, fmt.Errorf("в /proc/meminfo нет MemTotal")
	}
	// На очень старых ядрах (до 3.14) MemAvailable отсутствует — считать нечего.
	if available <= 0 {
		return MemoryStatus{}, fmt.Errorf("в /proc/meminfo нет MemAvailable")
	}

	return MemoryStatus{
		UsedPercent:    (1 - available/total) * 100,
		AvailableBytes: uint64(available) * 1024, // /proc/meminfo отдаёт килобайты
		TotalBytes:     uint64(total) * 1024,
	}, nil
}

// parseMeminfoKB разбирает значение вида «  16384000 kB» в число килобайт.
func parseMeminfoKB(value string) float64 {
	fields := strings.Fields(value)
	if len(fields) == 0 {
		return 0
	}
	kb, err := strconv.ParseFloat(fields[0], 64)
	if err != nil {
		return 0
	}
	return kb
}
