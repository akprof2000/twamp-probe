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

// saturate занимает слоты, пока предел не выбран до конца, — имитация того,
// что зонды действительно запущены и упираются в предел.
func saturate(l *AdaptiveLimiter) {
	for l.InFlight() < l.Limit() {
		if !l.Acquire(context.Background()) {
			return
		}
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
	if got := l.SetLimit(5000); got != 100 {
		t.Errorf("предел поднялся до %d выше потолка 100", got)
	}
	for range 50 {
		saturate(l)
		adjustLimit(l, guard(), mem(60)) // памяти вдоволь, предел выбран
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

func TestAdjust_DoesNotGrowWhileNoProbesAreRunning(t *testing.T) {
	// Главное правило: предел — это разрешение, память тратят процессы.
	// Пока зонды не запущены, расход мерить не на чем, и рост предела —
	// «эфемерный»: проба разгонялась бы вхолостую до значения, которое
	// первая же настоящая пачка задач обратит в отказ по памяти.
	l := NewAdaptiveLimiter(4096, 16, 64)

	for range 20 {
		adjustLimit(l, guard(), mem(60)) // памяти вдоволь, но зондов ноль
	}

	if l.Limit() != 64 {
		t.Errorf("предел вырос до %d при нуле работающих зондов, ожидалось 64", l.Limit())
	}
}

func TestAdjust_DoesNotGrowWhileLimitIsNotExhausted(t *testing.T) {
	// Предел выбран лишь наполовину — свободные слоты есть,
	// поднимать потолок незачем: нагрузки это не добавит.
	l := NewAdaptiveLimiter(4096, 16, 64)
	for range 32 {
		l.Acquire(context.Background())
	}

	for range 20 {
		adjustLimit(l, guard(), mem(60))
	}

	if l.Limit() != 64 {
		t.Errorf("предел вырос до %d, хотя занята лишь половина слотов", l.Limit())
	}
}

func TestAdjust_GrowsWhenLimitIsExhaustedAndMemoryIsCheap(t *testing.T) {
	// Зонды упёрлись в предел, память дешёвая — разгон разрешён.
	l := NewAdaptiveLimiter(4096, 16, 16)
	saturate(l)

	adjustLimit(l, guard(), mem(64)) // первая проверка задаёт базу замера

	if l.Limit() != 32 {
		t.Errorf("предел = %d, ожидалось удвоение до 32", l.Limit())
	}
}

func TestAdjust_HalvesWhenRunningProbesAteMoreThanHalfOfFree(t *testing.T) {
	// Сценарий из практики: свободно было 40 ГБ, запущенные зонды съели 24 ГБ
	// (больше половины) — предел обязан упасть вдвое, хотя занятость памяти
	// всего 62% и по одним лишь порогам казалось бы, что запас ещё есть.
	l := NewAdaptiveLimiter(4096, 16, 128)
	saturate(l)
	adjustLimit(l, guard(), mem(40)) // база: свободно 40 ГБ при 128 зондах
	before := l.Limit()
	saturate(l) // предел мог вырасти — догружаем зонды

	adjustLimit(l, guard(), mem(16)) // осталось 16 ГБ → зонды съели 24 ГБ

	if l.Limit() >= before {
		t.Errorf("предел = %d, ожидалось снижение относительно %d", l.Limit(), before)
	}
}

func TestAdjust_FullScenarioFromProduction(t *testing.T) {
	// Честная симуляция вместо заранее заданных чисел: 64 ГБ памяти, каждый
	// зонд стоит 0.25 ГБ, задач всегда больше предела. Проба обязана разогнаться
	// и остановиться сама, не доведя систему до нехватки памяти.
	const totalGB, costGB = 64.0, 0.25
	l := NewAdaptiveLimiter(4096, 16, 16)

	minFree, grew := totalGB, 0
	for range 60 {
		saturate(l) // все слоты заняты работающими зондами
		free := totalGB - float64(l.InFlight())*costGB
		minFree = min(minFree, free)

		before := l.Limit()
		adjustLimit(l, guard(), mem(free))
		if l.Limit() > before {
			grew++
		}
	}

	// Физический потолок — 256 зондов (64 ГБ / 0.25 ГБ), и проба обязана
	// остановиться заметно раньше, оставив системе запас.
	if l.InFlight() > 256 {
		t.Errorf("запущено %d зондов при физическом пределе 256 — память бы кончилась", l.InFlight())
	}
	// Разгон обязан остановиться сам по порогу MemoryLowPercent (80%), не доводя
	// до аварийного порога 95%: запас свободного должен остаться заметным.
	if minFree < totalGB*0.05 {
		t.Errorf("свободной памяти оставалось %.1f ГБ — проба дошла до нехватки", minFree)
	}
	if l.Limit() <= 16 {
		t.Errorf("предел = %d, разгон не состоялся", l.Limit())
	}
	if grew < 2 {
		t.Errorf("предел повышался %d раз — разгон практически не работает", grew)
	}
	if cost := l.CostPerRun(); cost == 0 {
		t.Error("цена зонда не измерена, хотя зонды запускались")
	}
	t.Logf("итог: предел %d, работает %d зондов, минимум свободного %.1f ГБ, цена зонда %s",
		l.Limit(), l.InFlight(), minFree, humanBytes(l.CostPerRun()))
}

func TestAdjust_WaitsWhileBatchIsStillStarting(t *testing.T) {
	// После повышения предела пачка запускается не мгновенно. Пока зонды
	// продолжают прибавляться, решение принимать рано: расход посчитан
	// не полностью, и проба удвоилась бы «на дешёвом» шаге ещё раз.
	l := NewAdaptiveLimiter(4096, 16, 16)
	saturate(l)
	adjustLimit(l, guard(), mem(64)) // 16 → 32, дальше ждём освоения
	if l.Limit() != 32 {
		t.Fatalf("предел = %d, ожидалось 32", l.Limit())
	}

	// Зонды поднимаются постепенно: слоты занимаются по одному.
	for range 3 {
		l.Acquire(context.Background())
		adjustLimit(l, guard(), mem(63))
		if l.Limit() != 32 {
			t.Fatalf("предел = %d — повышен, пока пачка ещё запускается", l.Limit())
		}
	}
}

func TestAdjust_WaitsWhileMemoryIsStillBeingEaten(t *testing.T) {
	// Зонды уже все запущены, но потребление продолжает заметно расти —
	// процессы отбирают память с задержкой. Ждём ещё шаг.
	l := NewAdaptiveLimiter(4096, 16, 16)
	saturate(l)
	adjustLimit(l, guard(), mem(64)) // 16 → 32
	saturate(l)                      // пачка запустилась целиком

	free := 60.0
	for range 3 {
		free -= 10 // каждый шаг память заметно убывает
		adjustLimit(l, guard(), mem(free))
		if l.Limit() != 32 {
			t.Fatalf("предел = %d — повышен, пока потребление ещё растёт", l.Limit())
		}
	}
}

func TestAdjust_GrowsOnceConsumptionSettles(t *testing.T) {
	// Как только зонды перестали прибавляться и потребление выросло
	// незначительно — можно делать следующий шаг.
	l := NewAdaptiveLimiter(4096, 16, 16)
	saturate(l)
	adjustLimit(l, guard(), mem(64)) // 16 → 32
	saturate(l)
	adjustLimit(l, guard(), mem(62)) // пачка запустилась, память ещё убывала

	adjustLimit(l, guard(), mem(61.9)) // убыль незначительна — освоились

	if l.Limit() != 64 {
		t.Errorf("предел = %d, ожидался следующий шаг до 64", l.Limit())
	}
}

func TestAdjust_EmergencyHalvingIgnoresSettling(t *testing.T) {
	// Ожидание освоения не должно мешать защите: памяти почти нет — режем сразу.
	l := NewAdaptiveLimiter(4096, 16, 1000)
	saturate(l)
	adjustLimit(l, guard(), mem(60)) // предел изменился → идёт ожидание

	before := l.Limit()
	adjustLimit(l, guard(), mem(1)) // 98% занято

	if l.Limit() != before/2 {
		t.Errorf("предел = %d, ожидалось аварийное сжатие до %d", l.Limit(), before/2)
	}
}

func TestAffordableTarget_CapsGrowthByMeasuredCost(t *testing.T) {
	// Рост ограничен тем, что физически влезает: при цене зонда 256 МБ
	// и 8 ГБ свободных под прибавку отводится половина — это 16 зондов.
	got, capped := affordableTarget(64, 128, 256*1024*1024, 8*gb)
	if got != 80 || !capped {
		t.Errorf("affordableTarget = %d (ограничен=%v), ожидалось 80 (ограничен=true)", got, capped)
	}

	// Цена ещё не измерена — доверяем желаемому, иначе мерить будет нечего.
	if got, capped := affordableTarget(64, 128, 0, 8*gb); got != 128 || capped {
		t.Errorf("без измеренной цены = %d (ограничен=%v), ожидалось 128", got, capped)
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

	for range 3 { // две проверки — пауза остывания, третья — рост
		saturate(l)
		adjustLimit(l, guard(), mem(60))
	}

	if l.Limit() != 625 { // 500 + четверть
		t.Errorf("предел = %d, ожидался осторожный рост до 625", l.Limit())
	}
}

func TestAdjust_HoldsBetweenThresholds(t *testing.T) {
	// Между порогами и при умеренном расходе предел не трогаем.
	l := NewAdaptiveLimiter(4096, 16, 500)
	l.SetLimit(500)

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

	l.SetLimit(5)
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
