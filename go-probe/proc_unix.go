//go:build !windows

// Завершение всего дерева процессов зонда по таймауту (Linux/Unix):
// процесс стартует в собственной группе, и сигнал уходит группе целиком.
package main

import (
	"os/exec"
	"syscall"
)

// configureProcessGroup выделяет зонду собственную группу процессов и настраивает
// отмену так, чтобы SIGKILL получала вся группа (включая дочерние процессы).
func configureProcessGroup(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	cmd.Cancel = func() error {
		// Отрицательный PID — сигнал всей группе процессов.
		return syscall.Kill(-cmd.Process.Pid, syscall.SIGKILL)
	}
}
