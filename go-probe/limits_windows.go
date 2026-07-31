//go:build windows

// На Windows нет лимита вида RLIMIT_NPROC: число процессов ограничено только
// памятью, а ожидание процесса не занимает отдельный поток так, как на Unix.
// Остаётся общая граница самого Go — предел в 10000 потоков.
package main

// systemRunCap возвращает физически допустимое число одновременных зондов.
func systemRunCap() (int, string) {
	return goThreadBudget, "предел потоков Go (10000)"
}
