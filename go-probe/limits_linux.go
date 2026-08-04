//go:build linux

// Системные лимиты, влияющие на число одновременных замеров (Linux).
//
// Проба поднимает свой мягкий лимит процессов до жёсткого и сверяет настройку
// с тем, что позволяет система. Урезать настройку она не будет: значение задаёт
// администратор, а её дело — предупредить, если машина к нему не готова.
package main

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"syscall"
)

// nprocResource — лимит «процессы и потоки пользователя» (RLIMIT_NPROC).
// В пакете syscall для Linux константы нет, значение фиксировано в ABI ядра.
const nprocResource = 6

// unlimitedRlimit — значение, которым ядро обозначает «без ограничений».
const unlimitedRlimit = ^uint64(0)

// checkSystemLimits поднимает мягкий лимит процессов до жёсткого и проверяет,
// хватает ли системных лимитов на maxParallel внешних замеров. Возвращает текст
// предупреждения или пустую строку, если всё в порядке.
//
// Считаем по внешним замерам — они дороже всего: процесс зонда плюс поток
// ожидания. Встроенным зондам эти лимиты не нужны, поэтому предупреждение для
// них избыточно; это оговорено в самом тексте.
func checkSystemLimits(maxParallel int) string {
	need := maxParallel * unitsPerRun

	limits := []struct {
		have int
		what string
		fix  string
	}{
		{raiseNprocLimit(), "лимит процессов и потоков пользователя (ulimit -u)",
			"LimitNPROC в юните systemd или /etc/security/limits.d"},
		{readProcInt("/proc/sys/kernel/pid_max"), "число идентификаторов задач (kernel.pid_max)",
			"sysctl kernel.pid_max"},
		{readProcInt("/proc/sys/kernel/threads-max"), "число потоков в системе (kernel.threads-max)",
			"sysctl kernel.threads-max"},
	}

	for _, l := range limits {
		if l.have > 0 && l.have < need {
			return fmt.Sprintf(
				"%s = %d, а на %d одновременных замеров внешними утилитами нужно ~%d "+
					"(каждый занимает процесс и поток). Поднимите: %s. "+
					"Настройка не изменена — на встроенные зонды это ограничение не действует",
				l.what, l.have, maxParallel, need, l.fix)
		}
	}
	return ""
}

// raiseNprocLimit поднимает мягкий лимит процессов до жёсткого и возвращает его.
//
// Так же поступает сам Go с лимитом дескрипторов. Мягкий лимит часто занижен
// (4096 при жёстком в десятки тысяч) — и это единственная причина, по которой
// проба упиралась бы в потолок гораздо раньше, чем позволяет машина.
func raiseNprocLimit() int {
	var limit syscall.Rlimit
	if err := syscall.Getrlimit(nprocResource, &limit); err != nil {
		return 0
	}
	if limit.Cur < limit.Max {
		raised := limit
		raised.Cur = raised.Max
		if err := syscall.Setrlimit(nprocResource, &raised); err == nil {
			limit = raised
		}
	}
	if limit.Cur == unlimitedRlimit {
		return 0 // ограничений нет — эта граница не в счёт
	}
	return int(limit.Cur)
}

// readProcInt читает целое из файла /proc/sys (0 — прочитать не удалось).
func readProcInt(path string) int {
	data, err := os.ReadFile(path)
	if err != nil {
		return 0
	}
	value, err := strconv.Atoi(strings.TrimSpace(string(data)))
	if err != nil {
		return 0
	}
	return value
}
