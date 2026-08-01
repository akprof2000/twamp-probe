//go:build !windows

// Остановка на Unix — обычным сигналом (SIGTERM от systemd, Ctrl+C в консоли).
package main

import (
	"context"
	"os"
	"os/signal"
	"syscall"
)

// shutdownContext возвращает контекст, отменяемый по сигналу остановки.
func shutdownContext() (context.Context, context.CancelFunc) {
	return signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
}

// prepareServiceEnvironment на Unix не нужен: рабочий каталог службы задаётся
// в юните systemd (WorkingDirectory), а из консоли он и так верный.
func prepareServiceEnvironment() {}

// markProbeFinished на Unix не нужен: о завершении сообщает сам выход процесса.
func markProbeFinished() {}
