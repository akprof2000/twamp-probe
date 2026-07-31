//go:build !windows && !linux

// Прочие системы (macOS, BSD): лимит процессов пользователя устроен иначе и
// проба на них не эксплуатируется — остаётся общая граница самого Go.
package main

// systemRunCap возвращает физически допустимое число одновременных зондов.
func systemRunCap() (int, string) {
	return goThreadBudget, "предел потоков Go (10000)"
}
