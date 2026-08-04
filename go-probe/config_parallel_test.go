package main

import "testing"

// Число одновременных замеров берётся из настройки как есть. Своего потолка
// проба не навязывает — ни по числу ядер, ни по лимитам ОС: подгонять значение
// под систему значит молча работать вполсилы.
func TestResolveParallel(t *testing.T) {
	cases := []struct {
		name     string
		in, want int
	}{
		{"явное 1024", 1024, 1024},
		{"явное 50", 50, 50},
		{"больше прежнего потолка — не урезается", 250000, 250000},
		{"ноль → значение по умолчанию", 0, defaultMaxParallel},
		{"отрицательное → значение по умолчанию", -1, defaultMaxParallel},
	}
	for _, c := range cases {
		if got := resolveParallel(c.in); got != c.want {
			t.Errorf("%s: resolveParallel(%d)=%d, ожидалось %d", c.name, c.in, got, c.want)
		}
	}
}
