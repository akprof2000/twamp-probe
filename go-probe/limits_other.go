//go:build !windows && !linux

// Прочие системы (macOS, BSD): лимит процессов пользователя устроен иначе,
// и проба на них не эксплуатируется — проверку не делаем.
package main

// checkSystemLimits на прочих системах всегда сообщает, что всё в порядке.
func checkSystemLimits(_ int) string { return "" }
