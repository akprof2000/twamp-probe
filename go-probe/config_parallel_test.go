package main

import (
	"runtime"
	"testing"
)

// Автоподбор числа воркеров: явное значение — как есть; 0 — по формуле с границами.
func TestResolveParallel(t *testing.T) {
	cases := []struct {
		name     string
		in, want int
	}{
		{"явное 1024", 1024, 1024},
		{"явное 50", 50, 50},
		{"ноль → авто по числу ядер (пол 16, потолок 10000)", 0, min(max(runtime.NumCPU()*16, 16), 10000)},
		{"отрицательное → авто", -1, min(max(runtime.NumCPU()*16, 16), 10000)},
	}
	for _, c := range cases {
		if got := resolveParallel(c.in); got != c.want {
			t.Errorf("%s: resolveParallel(%d)=%d, ожидалось %d", c.name, c.in, got, c.want)
		}
	}
	// Границы не нарушаются ни при каком числе ядер.
	if got := resolveParallel(0); got < 16 || got > 10000 {
		t.Errorf("авто вне диапазона [16..10000]: %d", got)
	}
}
