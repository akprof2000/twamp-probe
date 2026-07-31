//go:build linux

// Учёт лимитов ОС при выборе потолка одновременных запусков (Linux/Unix).
//
// Зачем: каждый запущенный зонд стоит пробе не только памяти. Пока идёт замер,
// его ждёт cmd.Wait() — блокирующий системный вызов, который занимает
// **отдельный поток ОС**. Замер это подтверждает: на 1500 процессов зонда у
// пробы 1523 потока. Значит один зонд — это две учётные единицы ядра: дочерний
// процесс и поток.
//
// Ядро считает и то и другое в одном лимите RLIMIT_NPROC (ulimit -u). Когда он
// исчерпан, Go не может создать поток и падает с fatal error — это не паника,
// перехватить её нельзя, служба просто умирает, напечатав дамп всех горутин.
// Своя граница есть и у самого Go: жёсткий предел в 10000 потоков.
//
// Поэтому потолок Probe:MaxParallel — это пожелание, а физическую верхнюю
// границу задаёт ОС. Её и вычисляем.
package main

import (
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

// systemRunCap возвращает физически допустимое число одновременных зондов
// и пояснение, чем оно ограничено. Ноль означает «ограничений не нашли».
//
// Побочный эффект: мягкий лимит RLIMIT_NPROC поднимается до жёсткого — так же,
// как Go сам поступает с лимитом дескрипторов. Часто мягкий занижен (4096 при
// жёстком в десятки тысяч), и это единственная причина, по которой проба
// упирается в потолок раньше времени.
func systemRunCap() (int, string) {
	// Границ несколько, и держит нас самая тесная из них.
	caps := []struct {
		limit  int
		reason string
	}{
		{goThreadBudget, "предел потоков Go (10000)"},
		{nprocRunCap(raiseNprocLimit()), "лимит процессов и потоков пользователя (ulimit -u)"},
		{nprocRunCap(readProcInt("/proc/sys/kernel/pid_max")), "число идентификаторов задач (kernel.pid_max)"},
		{nprocRunCap(readProcInt("/proc/sys/kernel/threads-max")), "число потоков в системе (kernel.threads-max)"},
	}

	best, reason := goThreadBudget, "предел потоков Go (10000)"
	for _, c := range caps {
		if c.limit > 0 && c.limit < best {
			best, reason = c.limit, c.reason
		}
	}
	return best, reason
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
