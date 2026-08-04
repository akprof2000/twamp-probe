//go:build windows

// На Windows нет лимита вида RLIMIT_NPROC: число процессов ограничено памятью,
// а ожидание процесса не занимает отдельный поток так, как на Unix. Проверять
// нечего — предупреждать не о чем.
package main

// checkSystemLimits на Windows всегда сообщает, что всё в порядке.
func checkSystemLimits(_ int) string { return "" }
