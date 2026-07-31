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

import "syscall"

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
	byThreads, byProc := goThreadBudget, 0

	var limit syscall.Rlimit
	if err := syscall.Getrlimit(nprocResource, &limit); err == nil {
		if limit.Cur < limit.Max {
			raised := limit
			raised.Cur = raised.Max
			if err := syscall.Setrlimit(nprocResource, &raised); err == nil {
				limit = raised
			}
		}
		if limit.Cur > 0 && limit.Cur != unlimitedRlimit {
			byProc = nprocRunCap(int(limit.Cur))
		}
	}

	if byProc > 0 && byProc < byThreads {
		return byProc, "лимит процессов и потоков ОС (ulimit -u)"
	}
	return byThreads, "предел потоков Go (10000)"
}
