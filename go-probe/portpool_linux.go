//go:build linux

// Проверка, не пересекается ли пул портов пробы с эфемерным диапазоном ядра.
//
// Пул раздаёт номера сам и считает свободным всё, чего не выдавал. Но если его
// диапазон совпадает с net.ipv4.ip_local_port_range, ядро раздаёт те же номера
// исходящим соединениям — и «свободный» по мнению пула порт оказывается занят
// чужим сокетом. Именно из-за этого замеры падают с «address already in use»,
// сколько бы портов в пуле ни оставалось.
package main

import (
	"fmt"
	"os"
	"strings"
)

// checkPortRangeOverlap возвращает текст предупреждения, если пул пересекается
// с эфемерным диапазоном ядра, и пустую строку, если всё разведено.
func checkPortRangeOverlap(from, to int) string {
	if from <= 0 {
		return "" // пул выключен — порты и так выбирает ядро
	}

	low, high := ephemeralRange()
	if low <= 0 || high < low {
		return "" // прочитать не удалось — догадки хуже молчания
	}
	if to < low || from > high {
		return ""
	}
	return fmt.Sprintf(
		"пул портов пробы %d-%d пересекается с эфемерным диапазоном ядра %d-%d: "+
			"ядро раздаёт те же номера исходящим соединениям, поэтому замеры будут "+
			"время от времени падать с «address already in use». Разведите диапазоны — "+
			"sysctl net.ipv4.ip_local_port_range или net.ipv4.ip_local_reserved_ports "+
			"(готовые значения есть в 99-twamp-probe.conf из пакета)",
		from, to, low, high)
}

// ephemeralRange читает net.ipv4.ip_local_port_range (0, 0 — прочитать не удалось).
func ephemeralRange() (int, int) {
	data, err := os.ReadFile("/proc/sys/net/ipv4/ip_local_port_range")
	if err != nil {
		return 0, 0
	}

	var low, high int
	if _, err := fmt.Sscan(strings.TrimSpace(string(data)), &low, &high); err != nil {
		return 0, 0
	}
	return low, high
}
