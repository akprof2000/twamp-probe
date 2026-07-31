package main

import (
	"context"
	"sync"
	"testing"
	"time"
)

const gb = uint64(1) << 30

// guard — типовые пороги: аварийное сжатие от 95%, разгон ниже 80%.
func guard() MemoryGuardConfig {
	return MemoryGuardConfig{HighPercent: 95, LowPercent: 80, Interval: time.Second}
}

// mem собирает состояние памяти: сколько свободно из 64 ГБ.
func mem(freeGB float64) MemoryStatus {
	total := 64 * gb
	free := uint64(freeGB * float64(gb))
	return MemoryStatus{
		UsedPercent:    (1 - float64(free)/float64(total)) * 100,
		AvailableBytes: free,
		TotalBytes:     total,
	}
}

// --- границы предела ---

func TestLimiter_StartsSmallNotAtCeiling(t *testing.T) {
	// После запуска предел маленький: иначе тысячи задач по расписанию срываются
	// одновременно и съедают память раньше первой её проверки.
	l := NewAdaptiveLimiter(4096, 16, 64)
	if l.Limit() != 64 {
		t.Errorf("стартовый предел = %d, ожидалось 64 (не потолок 4096)", l.Limit())
	}
}

func TestLimiter_NeverExceedsConfiguredMax(t *testing.T) {
	// Настройка — жёсткий потолок, выше не поднимаемся никогда.
	l := NewAdaptiveLimiter(100, 16, 100)
	if got := l.SetLimit(5000, 0); got != 100 {
		t.Errorf("предел поднялся до %d выше потолка 100", got)
	}
	for range 50 {
		adjustLimit(l, guard(), mem(60)) // памяти вдоволь
	}
	if l.Limit() != 100 {
		t.Errorf("после роста предел = %d, ожидался потолок 100", l.Limit())
	}
}

func TestLimiter_NeverDropsBelowMin(t *testing.T) {
	// Даже при полной нехватке памяти замеры должны продолжаться.
	l := NewAdaptiveLimiter(1000, 16, 1000)
	for range 50 {
		adjustLimit(l, guard(), mem(1)) // 63 из 64 ГБ занято
	}
	if l.Limit() != 16 {
		t.Errorf("предел упал до %d, ожидался пол 16", l.Limit())
	}
}

// --- решение по расходу памяти (главная логика) ---

func TestAdjust_HalvesWhenStepAteMoreThanHalfOfFree(t *testing.T) {
	// Сценарий из практики: свободно 40 ГБ, шаг съел 24 ГБ (больше половины) —
	// предел обязан упасть вдвое, хотя занятость памяти всего 62%
	// и по одним лишь порогам казалось бы, что запас ещё есть.
	l := NewAdaptiveLimiter(4096, 16, 128)
	l.SetLimit(128, 40*gb) // на момент шага было свободно 40 ГБ

	adjustLimit(l, guard(), mem(16)) // осталось 16 ГБ → съедено 24 ГБ

	if l.Limit() != 64 {
		t.Errorf("предел = %d, ожидалось уменьшение вдвое до 64", l.Limit())
	}
}

func TestAdjust_GrowsWhenStepWasCheap(t *testing.T) {
	// Свободно 64 ГБ, шаг съел всего 2 ГБ — можно разгоняться дальше.
	l := NewAdaptiveLimiter(4096, 16, 16)
	l.SetLimit(16, 64*gb)

	adjustLimit(l, guard(), mem(62)) // съедено 2 ГБ из 64

	if l.Limit() != 32 {
		t.Errorf("предел = %d, ожидалось удвоение до 32", l.Limit())
	}
}

func TestAdjust_FullScenarioFromProduction(t *testing.T) {
	// Полный сценарий: 64 ГБ памяти, разгон 16 → 32 → 64 → 128, затем шаг
	// съедает больше половины свободной — и вместо гибельного 256 предел уходит вниз.
	l := NewAdaptiveLimiter(4096, 16, 16)
	l.SetLimit(16, 64*gb)

	steps := []struct {
		freeLeftGB float64 // сколько осталось свободно после шага
		wantLimit  int     // каким должен стать предел
		comment    string
	}{
		{62, 32, "съело 2 ГБ из 64 — дёшево, разгон"},
		{48, 64, "съело 14 ГБ из 62 — меньше половины, разгон"},
		{40, 128, "съело 8 ГБ из 48 — дёшево, разгон"},
		{16, 64, "съело 24 ГБ из 40 — больше половины, назад вдвое"},
	}

	for i, step := range steps {
		adjustLimit(l, guard(), mem(step.freeLeftGB))
		if l.Limit() != step.wantLimit {
			t.Fatalf("шаг %d (%s): предел = %d, ожидался %d",
				i+1, step.comment, l.Limit(), step.wantLimit)
		}
	}

	// Ключевое: до опасного 256 дело не дошло — вместо роста был откат.
	if l.Limit() >= 256 {
		t.Errorf("предел дорос до %d — именно этот скачок и роняет пробу по памяти", l.Limit())
	}
}

func TestAdjust_EmergencyHalvingAtHighUsage(t *testing.T) {
	// Аварийный порог: памяти почти нет — режем вдвое, не разбираясь в расходе.
	l := NewAdaptiveLimiter(4096, 16, 1000)
	adjustLimit(l, guard(), mem(1)) // 98% занято

	if l.Limit() != 500 {
		t.Errorf("предел = %d, ожидалось аварийное сжатие вдвое до 500", l.Limit())
	}
}

func TestAdjust_GrowsGentlyAfterPressure(t *testing.T) {
	// После упора в память рост осторожный (+четверть), а не удвоение —
	// иначе проба прыгнет обратно в тот же потолок.
	l := NewAdaptiveLimiter(4096, 16, 1000)
	adjustLimit(l, guard(), mem(1)) // упор → 500, включается пауза остывания
	if l.Limit() != 500 {
		t.Fatalf("после упора предел = %d, ожидалось 500", l.Limit())
	}

	adjustLimit(l, guard(), mem(60)) // пауза остывания
	adjustLimit(l, guard(), mem(60)) // пауза остывания
	adjustLimit(l, guard(), mem(60)) // рост

	if l.Limit() != 625 { // 500 + четверть
		t.Errorf("предел = %d, ожидался осторожный рост до 625", l.Limit())
	}
}

func TestAdjust_HoldsBetweenThresholds(t *testing.T) {
	// Между порогами и при умеренном расходе предел не трогаем.
	l := NewAdaptiveLimiter(4096, 16, 500)
	l.SetLimit(500, 12*gb)

	adjustLimit(l, guard(), mem(11)) // занято ~83% — между порогами, расход мал
	if l.Limit() != 500 {
		t.Errorf("предел изменился на %d, ожидалось 500", l.Limit())
	}
}

// --- механика слотов ---

func TestLimiter_BlocksAboveLimit(t *testing.T) {
	l := NewAdaptiveLimiter(2, 1, 2)
	ctx := context.Background()

	if !l.Acquire(ctx) || !l.Acquire(ctx) {
		t.Fatal("не удалось занять два слота из двух")
	}

	blocked := make(chan struct{})
	go func() {
		l.Acquire(ctx)
		close(blocked)
	}()

	select {
	case <-blocked:
		t.Fatal("третий слот выдан сверх предела")
	case <-time.After(100 * time.Millisecond):
	}

	l.Release()
	select {
	case <-blocked:
	case <-time.After(2 * time.Second):
		t.Fatal("освобождение слота не разбудило ожидающего")
	}
}

func TestLimiter_GrowthWakesWaiters(t *testing.T) {
	l := NewAdaptiveLimiter(10, 1, 10)
	l.SetLimit(1, 0)
	ctx := context.Background()
	if !l.Acquire(ctx) {
		t.Fatal("не удалось занять единственный слот")
	}

	woke := make(chan struct{})
	go func() {
		l.Acquire(ctx)
		close(woke)
	}()
	time.Sleep(50 * time.Millisecond)

	l.SetLimit(5, 0)
	select {
	case <-woke:
	case <-time.After(2 * time.Second):
		t.Fatal("рост предела не разбудил ожидающего")
	}
}

func TestLimiter_CloseReleasesWaiters(t *testing.T) {
	l := NewAdaptiveLimiter(1, 1, 1)
	ctx := context.Background()
	if !l.Acquire(ctx) {
		t.Fatal("не удалось занять слот")
	}

	done := make(chan bool)
	go func() { done <- l.Acquire(ctx) }()
	time.Sleep(50 * time.Millisecond)

	l.Close()
	select {
	case ok := <-done:
		if ok {
			t.Error("после остановки слот выдан, ожидался отказ")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("остановка не отпустила ожидающего")
	}
}

func TestLimiter_ConcurrentAcquireRespectsLimit(t *testing.T) {
	l := NewAdaptiveLimiter(8, 1, 8)
	ctx := context.Background()

	var mu sync.Mutex
	current, peak := 0, 0
	var wg sync.WaitGroup

	for range 100 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if !l.Acquire(ctx) {
				return
			}
			mu.Lock()
			current++
			peak = max(peak, current)
			mu.Unlock()

			time.Sleep(time.Millisecond)

			mu.Lock()
			current--
			mu.Unlock()
			l.Release()
		}()
	}
	wg.Wait()

	if peak > 8 {
		t.Errorf("одновременно выполнялось %d запусков при пределе 8", peak)
	}
	if l.InFlight() != 0 {
		t.Errorf("после завершения занято слотов: %d", l.InFlight())
	}
}

func TestMemoryStatus_ReturnsSaneValues(t *testing.T) {
	st, err := memoryStatus()
	if err != nil {
		t.Fatalf("не удалось прочитать состояние памяти: %v", err)
	}
	if st.UsedPercent <= 0 || st.UsedPercent > 100 {
		t.Errorf("занятость = %.1f%%, ожидалось 0..100", st.UsedPercent)
	}
	if st.TotalBytes == 0 || st.AvailableBytes == 0 || st.AvailableBytes > st.TotalBytes {
		t.Errorf("объёмы неправдоподобны: свободно %d из %d", st.AvailableBytes, st.TotalBytes)
	}
	t.Logf("память: занято %.1f%%, свободно %s из %s",
		st.UsedPercent, humanBytes(st.AvailableBytes), humanBytes(st.TotalBytes))
}

func TestHumanBytes(t *testing.T) {
	cases := map[uint64]string{
		512:             "512 Б",
		2 * 1024:        "2.0 КБ",
		5 * 1024 * 1024: "5.0 МБ",
		3 * gb:          "3.0 ГБ",
	}
	for value, want := range cases {
		if got := humanBytes(value); got != want {
			t.Errorf("humanBytes(%d) = %q, ожидалось %q", value, got, want)
		}
	}
}
