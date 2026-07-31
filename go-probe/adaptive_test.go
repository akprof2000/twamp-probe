package main

import (
	"context"
	"sync"
	"testing"
	"time"
)

// guard — типовые пороги: сжимаем от 95%, возвращаем до 80%.
func guard() MemoryGuardConfig {
	return MemoryGuardConfig{HighPercent: 95, LowPercent: 80, Interval: time.Second}
}

func TestLimiter_StartsAtConfiguredMax(t *testing.T) {
	l := NewAdaptiveLimiter(1000, 16, 1000)
	if l.Limit() != 1000 {
		t.Errorf("начальный предел = %d, ожидался потолок 1000", l.Limit())
	}
}

func TestLimiter_NeverExceedsConfiguredMax(t *testing.T) {
	// Главное требование: настройка — жёсткий потолок, выше не поднимаемся никогда.
	l := NewAdaptiveLimiter(100, 16, 100)
	if got := l.SetLimit(5000); got != 100 {
		t.Errorf("предел поднялся до %d выше потолка 100", got)
	}

	// Даже длинная череда «памяти много» не пробивает потолок.
	for range 50 {
		adjustLimit(l, guard(), 10, 10)
	}
	if l.Limit() != 100 {
		t.Errorf("после роста предел = %d, ожидался потолок 100", l.Limit())
	}
}

func TestLimiter_NeverDropsBelowMin(t *testing.T) {
	// Даже при полной нехватке памяти замеры должны продолжаться.
	l := NewAdaptiveLimiter(1000, 16, 1000)
	for range 50 {
		adjustLimit(l, guard(), 99, 100)
	}
	if l.Limit() != 16 {
		t.Errorf("предел упал до %d, ожидался пол 16", l.Limit())
	}
}

func TestAdjust_ShrinksWhenMemoryHigh(t *testing.T) {
	l := NewAdaptiveLimiter(1000, 16, 1000)
	adjustLimit(l, guard(), 96, 100) // 96% — выше порога сжатия

	if l.Limit() != 750 {
		t.Errorf("предел = %d, ожидалось сжатие на четверть (750)", l.Limit())
	}
}

func TestAdjust_GrowsWhenMemoryLow(t *testing.T) {
	// Упора в память ещё не было, значит идёт разгон — предел удваивается.
	l := NewAdaptiveLimiter(1000, 16, 1000)
	l.SetLimit(400)
	adjustLimit(l, guard(), 50, 100) // 50% — ниже порога роста

	if l.Limit() != 800 {
		t.Errorf("предел = %d, ожидалось удвоение при разгоне (800)", l.Limit())
	}
}

func TestAdjust_HoldsBetweenThresholds(t *testing.T) {
	// Между порогами предел не трогаем — иначе он дёргался бы туда-сюда.
	l := NewAdaptiveLimiter(1000, 16, 1000)
	l.SetLimit(500)

	for _, used := range []float64{81, 85, 90, 94.9} {
		adjustLimit(l, guard(), used, 100)
		if l.Limit() != 500 {
			t.Errorf("при памяти %.1f%% предел изменился на %d, ожидалось 500", used, l.Limit())
		}
	}
}

func TestAdjust_RecoversAfterPressure(t *testing.T) {
	// Сценарий аварии: память кончилась, потом освободилась.
	l := NewAdaptiveLimiter(1000, 16, 1000)

	for range 5 { // давление памяти
		adjustLimit(l, guard(), 97, 100)
	}
	underPressure := l.Limit()
	if underPressure >= 1000 {
		t.Fatalf("предел не сжался под давлением: %d", underPressure)
	}

	for range 20 { // память освободилась
		adjustLimit(l, guard(), 40, 100)
	}
	if l.Limit() != 1000 {
		t.Errorf("предел не вернулся к потолку: %d", l.Limit())
	}
}

func TestLimiter_BlocksAboveLimit(t *testing.T) {
	// Слотов ровно столько, сколько разрешает предел: лишний вызов ждёт.
	l := NewAdaptiveLimiter(2, 1, 2)
	ctx := context.Background()

	if !l.Acquire(ctx) || !l.Acquire(ctx) {
		t.Fatal("не удалось занять два слота из двух")
	}
	if l.InFlight() != 2 {
		t.Errorf("занято слотов: %d, ожидалось 2", l.InFlight())
	}

	blocked := make(chan struct{})
	go func() {
		l.Acquire(ctx) // должен ждать освобождения
		close(blocked)
	}()

	select {
	case <-blocked:
		t.Fatal("третий слот выдан сверх предела")
	case <-time.After(100 * time.Millisecond):
	}

	l.Release() // освобождаем — ожидающий обязан проснуться
	select {
	case <-blocked:
	case <-time.After(2 * time.Second):
		t.Fatal("освобождение слота не разбудило ожидающего")
	}
}

func TestLimiter_GrowthWakesWaiters(t *testing.T) {
	// Память освободилась, предел вырос — ждущие запуски должны пойти сразу,
	// не дожидаясь завершения текущих.
	l := NewAdaptiveLimiter(10, 1, 10)
	l.SetLimit(1)
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

	l.SetLimit(5) // предел вырос
	select {
	case <-woke:
	case <-time.After(2 * time.Second):
		t.Fatal("рост предела не разбудил ожидающего")
	}
}

func TestLimiter_CloseReleasesWaiters(t *testing.T) {
	// При остановке службы ожидающие не должны висеть вечно.
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
	// Под конкурентной нагрузкой предел не должен превышаться ни на мгновение.
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

func TestMemoryUsedPercent_ReturnsSaneValue(t *testing.T) {
	// Чтение памяти платформозависимо — проверяем, что оно вообще работает
	// и отдаёт правдоподобное значение.
	used, err := memoryUsedPercent()
	if err != nil {
		t.Fatalf("не удалось прочитать занятость памяти: %v", err)
	}
	if used <= 0 || used > 100 {
		t.Errorf("занятость памяти = %.1f%%, ожидалось значение в диапазоне 0..100", used)
	}
	t.Logf("занятость памяти на этой машине: %.1f%%", used)
}

func TestLimiter_StartsSmallNotAtCeiling(t *testing.T) {
	// Ключевое: после запуска предел маленький. Иначе тысячи задач по расписанию
	// стартуют одновременно и съедают память раньше первой её проверки.
	l := NewAdaptiveLimiter(4096, 16, 64)
	if l.Limit() != 64 {
		t.Errorf("стартовый предел = %d, ожидалось 64 (не потолок 4096)", l.Limit())
	}
}

func TestLimiter_StartClampedToBounds(t *testing.T) {
	if l := NewAdaptiveLimiter(100, 16, 5000); l.Limit() != 100 {
		t.Errorf("стартовый предел выше потолка: %d", l.Limit())
	}
	if l := NewAdaptiveLimiter(100, 16, 1); l.Limit() != 16 {
		t.Errorf("стартовый предел ниже пола: %d", l.Limit())
	}
}

func TestAdjust_SlowStartDoublesUntilCeiling(t *testing.T) {
	// Пока памяти вдоволь и упора не было — разгоняемся удвоением.
	l := NewAdaptiveLimiter(4096, 16, 64)
	for _, want := range []int{128, 256, 512, 1024, 2048, 4096, 4096} {
		adjustLimit(l, guard(), 40, 409)
		if l.Limit() != want {
			t.Fatalf("предел = %d, ожидался %d", l.Limit(), want)
		}
	}
}

func TestAdjust_GrowsGentlyAfterPressure(t *testing.T) {
	// После первого упора в память рост становится осторожным, а не удвоением:
	// иначе проба снова прыгнет в тот же потолок памяти.
	l := NewAdaptiveLimiter(4000, 16, 1000)
	adjustLimit(l, guard(), 97, 400) // упор → 750, включается пауза остывания
	if l.Limit() != 750 {
		t.Fatalf("после сжатия предел = %d, ожидалось 750", l.Limit())
	}

	// Пауза: две ближайшие проверки роста пропускаются.
	adjustLimit(l, guard(), 40, 400)
	adjustLimit(l, guard(), 40, 400)
	if l.Limit() != 750 {
		t.Errorf("предел вырос во время паузы остывания: %d", l.Limit())
	}

	adjustLimit(l, guard(), 40, 400)
	if l.Limit() != 1150 {
		t.Errorf("предел = %d, ожидался плавный рост на шаг (1150)", l.Limit())
	}
}

func TestAdjust_SlowStartStopsAtMemoryPressure(t *testing.T) {
	// Разгон обязан остановиться, как только память подошла к порогу,
	// а не долететь до потолка настройки.
	l := NewAdaptiveLimiter(4096, 16, 64)
	adjustLimit(l, guard(), 40, 409) // 128
	adjustLimit(l, guard(), 40, 409) // 256
	reached := l.Limit()

	adjustLimit(l, guard(), 96, 409) // упёрлись в память
	if l.Limit() >= reached {
		t.Errorf("предел не сжался при нехватке памяти: было %d, стало %d", reached, l.Limit())
	}
	if l.Limit() >= 4096 {
		t.Error("предел дошёл до потолка вопреки нехватке памяти")
	}
}
