//go:build windows

// Заглушка для Windows (Go-проба целевая под Linux, но собирается и здесь
// для локальной отладки): стандартная отмена CommandContext убивает процесс.
package main

import "os/exec"

// configureProcessGroup на Windows ничего не настраивает — Kill процессу шлёт сам Go.
func configureProcessGroup(_ *exec.Cmd) {}
