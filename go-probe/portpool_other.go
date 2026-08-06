//go:build !linux

// Проверка пересечения пула портов с эфемерным диапазоном ядра — заглушка.
//
// Настройка эфемерного диапазона есть и на других системах, но читается она
// иначе, а рабочие пробы живут на Linux. Молчим, а не гадаем.
package main

// checkPortRangeOverlap на не-Linux ничего не проверяет.
func checkPortRangeOverlap(from, to int) string { return "" }
