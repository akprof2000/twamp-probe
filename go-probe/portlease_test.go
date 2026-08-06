package main

// Проверки аренды портов: два встроенных зонда — twping (TWamp) и отправитель
// twampy — берут номера из одного пула и не должны за них конкурировать.
//
// Проверяется не намерение, а факт: порт, который зонд занял на самом деле,
// берётся не из аргументов вызова, а с другой стороны провода — рефлектор
// сообщает, с какого адреса пришли пакеты.

import (
	"context"
	"fmt"
	"net"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"
)

// leasePool — пул на время теста, с коротким карантином и остановкой по
// завершении. Диапазон берётся заведомо в стороне от эфемерного диапазона ядра,
// чтобы посторонние соединения в проверку не вмешивались.
func leasePool(t *testing.T, from, to int) *PortPool {
	t.Helper()
	pool, err := NewPortPool(from, to)
	if err != nil {
		t.Fatalf("пул не создался: %v", err)
	}
	t.Cleanup(pool.Close)
	return pool
}

func TestPool_ConcurrentLeasesNeverOverlap(t *testing.T) {
	// Главное свойство пула: пока порт в аренде, второй раз его не выдадут.
	// Проверяем не только учётом, но и делом — каждая аренда реально открывает
	// UDP-сокет на выданном номере. Коллизия учёта сразу дала бы отказ ядра.
	const (
		workers = 40
		rounds  = 15
	)
	pool := leasePool(t, 21100, 21149) // 50 портов на 40 одновременных аренд
	pool.cooldown = 5 * time.Millisecond

	var mu sync.Mutex
	inUse := map[int]bool{}
	var overlaps, bindFails int
	seen := map[int]int{}

	var wg sync.WaitGroup
	for range workers {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for range rounds {
				port, ok := pool.Acquire(context.Background())
				if !ok {
					t.Error("пул отказал, хотя портов достаточно")
					return
				}

				mu.Lock()
				if inUse[port] {
					overlaps++
				}
				inUse[port] = true
				seen[port]++
				mu.Unlock()

				// Настоящая проверка: ядро обязано отдать этот порт.
				conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: port})
				if err != nil {
					mu.Lock()
					bindFails++
					mu.Unlock()
				} else {
					time.Sleep(time.Millisecond)
					conn.Close()
				}

				mu.Lock()
				delete(inUse, port)
				mu.Unlock()
				pool.Release(port)
			}
		}()
	}
	wg.Wait()

	if overlaps != 0 {
		t.Errorf("один порт выдан двум арендаторам одновременно %d раз", overlaps)
	}
	if bindFails != 0 {
		t.Errorf("ядро отказало в привязке %d раз из %d — номера пересекаются с чужими",
			bindFails, workers*rounds)
	}
	if stats := pool.Stats(); stats.Taken != 0 {
		t.Errorf("после теста в аренде осталось %d портов, ожидалось 0", stats.Taken)
	}

	// Побочно: нагрузка должна размазываться по всему диапазону, а не крутиться
	// на нескольких номерах.
	if len(seen) < 40 {
		t.Errorf("задействовано лишь %d портов из 50 — выдача не идёт по кругу", len(seen))
	}
}

func TestPool_ReleasedPortCoolsDownBeforeReuse(t *testing.T) {
	// Возвращённый порт не должен уходить следующему замеру сразу: хвосты
	// предыдущего замера (досланные ответы рефлектора) попали бы в новый.
	pool := leasePool(t, 21200, 21203)
	pool.cooldown = 300 * time.Millisecond

	first, _ := pool.Acquire(context.Background())
	pool.Release(first)

	// Пока порт отлёживается, выдаются другие номера.
	for range 3 {
		port, ok := pool.Acquire(context.Background())
		if !ok {
			t.Fatal("пул отказал, хотя свободные порты есть")
		}
		if port == first {
			t.Fatalf("порт %d выдан снова, не отлежав cooldown", first)
		}
		defer pool.Release(port)
	}

	if stats := pool.Stats(); stats.Cooling != 1 {
		t.Errorf("отлёживается %d портов, ожидался 1", stats.Cooling)
	}

	// А после cooldown он возвращается в очередь.
	time.Sleep(400 * time.Millisecond)
	again, ok := pool.Acquire(context.Background())
	if !ok || again != first {
		t.Errorf("после cooldown выдан порт %d (ok=%v), ожидался вернувшийся %d", again, ok, first)
	}
}

func TestPool_CooldownYieldsUnderPressure(t *testing.T) {
	// Отлежаться — не догма: когда готовых портов нет, замеру отдаётся самый
	// долго лежащий. Очередь замеров хуже, чем недолежавший порт.
	pool := leasePool(t, 21210, 21210) // ровно один порт
	pool.cooldown = time.Hour          // отлежаться заведомо не успеет

	port, _ := pool.Acquire(context.Background())
	pool.Release(port)

	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	again, ok := pool.Acquire(ctx)
	if !ok {
		t.Fatal("пул заставил замер ждать вместо того, чтобы выдать недолежавший порт")
	}
	if again != port {
		t.Fatalf("выдан порт %d, а в пуле есть только %d", again, port)
	}
	if stats := pool.Stats(); stats.Hurried != 1 {
		t.Errorf("счётчик выданных досрочно = %d, ожидался 1", stats.Hurried)
	}
}

// embeddedMixRunner собирает исполнитель, у которого оба режима идут встроенными
// зондами и берут порты из общего пула.
func embeddedMixRunner(t *testing.T, pool *PortPool) (*ProbeRunner, *ResultStore) {
	t.Helper()
	cfg := &Config{
		MaxPendingResults: 1000,
		TwampEmbedded:     true,
		TwampyEmbedded:    true,
		Twamp:             ProbeToolConfig{Name: "twping-не-существует"},
		Twampy:            ProbeToolConfig{Name: "python-не-существует"},
	}
	results := NewResultStore(cfg.MaxPendingResults, 0)
	return NewProbeRunner(cfg, results, NewRunRegistry(), NewRunCancelRegistry(), pool), results
}

func mixTask(id, title string, mode TaskMode, node, args string) *TaskInfo {
	return &TaskInfo{
		Id: id, Title: title, Mode: mode, EndNode: node,
		Circles: 1, Repeats: 1, TimeoutSec: 30,
		Parameters: map[string]string{"args": args},
	}
}

func TestMixedEmbeddedProbes_DoNotCompeteForPorts(t *testing.T) {
	// Сквозная проверка: twping и twampy работают одновременно на общем пуле.
	// Оба зонда настоящие, оба доходят до реального обмена пакетами, а порты,
	// которые они заняли, наблюдаются с другой стороны провода.
	if testing.Short() {
		t.Skip("сквозной замер занимает секунды — пропускаем в коротком режиме")
	}

	const each = 6 // задач каждого режима
	twamp := startTwampReflector(t)
	twampy := startTwampyReflector(t)
	pool := leasePool(t, 21300, 21399)
	runner, results := embeddedMixRunner(t, pool)

	var wg sync.WaitGroup
	for i := range each {
		wg.Add(1)
		go func() {
			defer wg.Done()
			task := mixTask(fmt.Sprintf("twamp-%d", i), "TWamp "+itoa(i), ModeTWamp,
				twamp.Addr(), "-c 5 -i 0.02")
			runner.RunForNodes(context.Background(), task)
		}()

		wg.Add(1)
		go func() {
			defer wg.Done()
			node := "127.0.0.1:" + itoa(twampy.port)
			task := mixTask(fmt.Sprintf("twampy-%d", i), "TWampy "+itoa(i), ModeTWampy,
				node, "-c 5 -i 20")
			runner.RunForNodes(context.Background(), task)
		}()
	}
	wg.Wait()

	// --- Все замеры обязаны состояться --------------------------------------
	batch := results.TakeBatch(time.Second).Items
	if len(batch) != 2*each {
		t.Fatalf("получено %d результатов, ожидалось %d", len(batch), 2*each)
	}
	for _, item := range batch {
		if item.Outcome != string(OutcomeSuccess) {
			t.Errorf("замер %s (%s) завершился как %s: %s",
				item.TaskId, item.Mode, item.Outcome, firstLine(item.ErrorConsole))
		}
	}

	// --- Ни одного повторного выбора порта ----------------------------------
	stats := pool.Stats()
	if stats.Banned != 0 {
		t.Errorf("в карантине %d портов — значит, зонды столкнулись на одном номере", stats.Banned)
	}
	if stats.Taken != 0 {
		t.Errorf("после прогона в аренде осталось %d портов, ожидалось 0", stats.Taken)
	}

	// --- Порты разные, и все из пула ----------------------------------------
	twampPorts := twamp.SenderPorts()
	twampyPorts := twampy.SenderPorts()
	if len(twampPorts) != each {
		t.Errorf("twping отработал с %d портов, ожидалось %d: %v", len(twampPorts), each, twampPorts)
	}
	if len(twampyPorts) != each {
		t.Errorf("twampy отработал с %d портов, ожидалось %d: %v", len(twampyPorts), each, twampyPorts)
	}

	all := append(append([]int(nil), twampPorts...), twampyPorts...)
	for _, port := range all {
		if port < 21300 || port > 21399 {
			t.Errorf("зонд занял порт %d вне пула 21300-21399 — номер выбрало ядро, а не проба", port)
		}
	}

	slices.Sort(all)
	for i := 1; i < len(all); i++ {
		if all[i] == all[i-1] {
			t.Errorf("порт %d достался двум замерам — режимы конкурируют за номера", all[i])
		}
	}

	// Пересечение множеств режимов — та самая конкуренция, ради которой всё
	// затевалось: проверяем отдельно, чтобы в отчёте было видно, кто с кем.
	for _, port := range twampPorts {
		if slices.Contains(twampyPorts, port) {
			t.Errorf("порт %d занимали и twping, и twampy", port)
		}
	}
}

func TestMixedEmbeddedProbes_SmallPoolStillCompletes(t *testing.T) {
	// Тот же смешанный прогон, но портов меньше, чем замеров: аренда обязана
	// переиспользовать номера по кругу, а не терять замеры. Заодно видно, что
	// cooldown под давлением уступает — иначе замеры встали бы в очередь.
	if testing.Short() {
		t.Skip("сквозной замер занимает секунды — пропускаем в коротком режиме")
	}

	const each = 4
	twamp := startTwampReflector(t)
	twampy := startTwampyReflector(t)
	pool := leasePool(t, 21400, 21402) // три порта на восемь замеров
	runner, results := embeddedMixRunner(t, pool)

	var wg sync.WaitGroup
	for i := range each {
		wg.Add(1)
		go func() {
			defer wg.Done()
			task := mixTask(fmt.Sprintf("small-twamp-%d", i), "TWamp "+itoa(i), ModeTWamp,
				twamp.Addr(), "-c 3 -i 0.02")
			runner.RunForNodes(context.Background(), task)
		}()

		wg.Add(1)
		go func() {
			defer wg.Done()
			task := mixTask(fmt.Sprintf("small-twampy-%d", i), "TWampy "+itoa(i), ModeTWampy,
				"127.0.0.1:"+itoa(twampy.port), "-c 3 -i 20")
			runner.RunForNodes(context.Background(), task)
		}()
	}
	wg.Wait()

	batch := results.TakeBatch(time.Second).Items
	if len(batch) != 2*each {
		t.Fatalf("получено %d результатов, ожидалось %d — замеры теряются на малом пуле",
			len(batch), 2*each)
	}
	for _, item := range batch {
		if item.Outcome != string(OutcomeSuccess) {
			t.Errorf("замер %s (%s) завершился как %s: %s",
				item.TaskId, item.Mode, item.Outcome, firstLine(item.ErrorConsole))
		}
	}
	if banned := pool.Stats().Banned; banned != 0 {
		t.Errorf("в карантине %d портов — зонды столкнулись на одном номере", banned)
	}
}

func TestEmbeddedProbesBindExactlyThePooledPort(t *testing.T) {
	// Обе утилиты обязаны занять именно тот номер, который выдал пул, — иначе
	// учёт пула расходится с действительностью и столкновения неизбежны.
	if testing.Short() {
		t.Skip("сквозной замер занимает секунды — пропускаем в коротком режиме")
	}

	t.Run("twping", func(t *testing.T) {
		refl := startTwampReflector(t)
		const port = 21501
		args := strings.Fields(fmt.Sprintf("-c 3 -i 0.02 -P %d-%d %s", port, port, refl.Addr()))

		_, errText := runEmbeddedTwping(context.Background(), args, time.Now().Add(20*time.Second))
		if errText != "" {
			t.Fatalf("замер не удался: %s", errText)
		}
		if got := refl.SenderPorts(); !slices.Contains(got, port) {
			t.Errorf("рефлектор увидел порты %v, ожидался выданный пулом %d", got, port)
		}
	})

	t.Run("twampy", func(t *testing.T) {
		refl := startTwampyReflector(t)
		const port = 21502
		args := strings.Fields(fmt.Sprintf("sender 127.0.0.1:%d :%d -c 3 -i 20", refl.port, port))

		_, errText := runEmbeddedTwampy(context.Background(), args, time.Now().Add(20*time.Second))
		if errText != "" {
			t.Fatalf("замер не удался: %s", errText)
		}
		if got := refl.SenderPorts(); !slices.Contains(got, port) {
			t.Errorf("рефлектор увидел порты %v, ожидался выданный пулом %d", got, port)
		}
	})
}
