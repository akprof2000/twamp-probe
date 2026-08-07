//go:build linux

package main

// Нагрузочная проверка аренды портов на Linux: занят ли выданный пулом порт
// кем-то посторонним — и если да, то кем именно.
//
// Вопрос, ради которого написано: на боевой пробе замеры массово падали
// с «address already in use» на портах из пула, хотя машина чистая и кроме
// пробы на ней ничего нет. Значит либо пул выдаёт занятый номер (ошибка
// учёта), либо порт занимает сама проба мимо пула. Тест различает эти случаи
// фактами из /proc/net/udp, а не рассуждениями.
//
// Запускается только по требованию — он тяжёлый и меняет настройку ядра:
//
//	PROBE_LOADTEST=1 go test -run TestLoad_ -v
//
// Менять ip_local_port_range можно из-под root в своём сетевом пространстве
// имён (контейнер), поэтому обычный прогон тест пропускает.

import (
	"context"
	"encoding/hex"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// udpSocket — строка из /proc/net/udp: чей сокет и на каком адресе.
type udpSocket struct {
	addr string // «0.0.0.0» или конкретный адрес
	port int
	uid  string
	ino  string
}

// listUDPSockets читает все UDP-сокеты системы. Именно так видно, кто на самом
// деле держит порт: ядру безразлично, что об этом думает наш пул.
func listUDPSockets(t *testing.T) map[int][]udpSocket {
	t.Helper()
	sockets := map[int][]udpSocket{}

	for _, path := range []string{"/proc/net/udp", "/proc/net/udp6"} {
		data, err := os.ReadFile(path)
		if err != nil {
			continue // udp6 может отсутствовать
		}
		for i, line := range strings.Split(string(data), "\n") {
			fields := strings.Fields(line)
			if i == 0 || len(fields) < 8 {
				continue // заголовок либо хвост файла
			}
			local := strings.Split(fields[1], ":")
			if len(local) != 2 {
				continue
			}
			port, err := strconv.ParseInt(local[1], 16, 32)
			if err != nil {
				continue
			}
			sockets[int(port)] = append(sockets[int(port)], udpSocket{
				addr: decodeProcAddr(local[0]),
				port: int(port),
				uid:  fields[7],
				ino:  fields[len(fields)-6],
			})
		}
	}
	return sockets
}

// decodeProcAddr переводит адрес из /proc (hex, обратный порядок байт) в точки.
func decodeProcAddr(value string) string {
	raw, err := hex.DecodeString(value)
	if err != nil || len(raw) != 4 {
		return value
	}
	return fmt.Sprintf("%d.%d.%d.%d", raw[3], raw[2], raw[1], raw[0])
}

// setEphemeralRange задаёт диапазон эфемерных портов ядра и возвращает прежний.
func setEphemeralRange(t *testing.T, low, high int) {
	t.Helper()
	const path = "/proc/sys/net/ipv4/ip_local_port_range"

	before, err := os.ReadFile(path)
	if err != nil {
		t.Skipf("не прочитать %s: %v", path, err)
	}
	if err := os.WriteFile(path, []byte(fmt.Sprintf("%d %d", low, high)), 0644); err != nil {
		t.Skipf("не изменить %s (нужен root в своём netns): %v", path, err)
	}
	t.Cleanup(func() { _ = os.WriteFile(path, before, 0644) })
}

func requireLoadTest(t *testing.T) {
	t.Helper()
	if os.Getenv("PROBE_LOADTEST") == "" {
		t.Skip("нагрузочный тест: запускается при PROBE_LOADTEST=1")
	}
}

// TestLoad_PoolAgainstKernelEphemeral — главный эксперимент.
//
// Пул раздаёт номера сам, а ядро из своего диапазона раздаёт эфемерные порты
// всему, что открывает сокет без явного номера, — в том числе самой пробе
// (резолвер DNS и любой исходящий UDP). Проверяем, что происходит с арендой,
// когда эти диапазоны совмещены и когда разведены.
func TestLoad_PoolAgainstKernelEphemeral(t *testing.T) {
	requireLoadTest(t)

	const (
		poolFrom = 20000
		poolTo   = 25000
		leases   = 3000 // столько аренд с настоящей привязкой сокета
		noise    = 1500 // столько посторонних сокетов с портом от ядра
	)

	for _, scenario := range []struct {
		name              string
		ephFrom, ephTo    int
		collisionsAllowed bool
	}{
		{"диапазоны совмещены", poolFrom, poolTo, true},
		{"диапазоны разведены", 10000, 15000, false},
	} {
		t.Run(scenario.name, func(t *testing.T) {
			setEphemeralRange(t, scenario.ephFrom, scenario.ephTo)

			// Посторонние сокеты — имитация того, что делает сама проба помимо
			// пула: резолвер DNS и прочие исходящие UDP. Номера им выбирает ядро.
			var noiseConns []*net.UDPConn
			for range noise {
				conn, err := net.ListenUDP("udp4", &net.UDPAddr{})
				if err != nil {
					break // эфемерные кончились — для опыта это тоже результат
				}
				noiseConns = append(noiseConns, conn)
			}
			t.Cleanup(func() {
				for _, c := range noiseConns {
					c.Close()
				}
			})
			t.Logf("посторонних UDP-сокетов открыто: %d (порты выбрало ядро из %d-%d)",
				len(noiseConns), scenario.ephFrom, scenario.ephTo)

			pool, err := NewPortPool(poolFrom, poolTo)
			if err != nil {
				t.Fatalf("пул не создался: %v", err)
			}
			defer pool.Close()
			pool.cooldown = 10 * time.Millisecond

			var collisions, doubleIssued atomic.Int64
			var mu sync.Mutex
			held := map[int]bool{}      // порты, занятые прямо сейчас
			foreign := map[int]string{} // порт → кто его держал в момент отказа

			var wg sync.WaitGroup
			for range 50 {
				wg.Add(1)
				go func() {
					defer wg.Done()
					for range leases / 50 {
						port, ok := pool.Acquire(context.Background())
						if !ok {
							return
						}

						mu.Lock()
						if held[port] {
							doubleIssued.Add(1)
						}
						held[port] = true
						mu.Unlock()

						conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: port})
						if err != nil {
							collisions.Add(1)
							// Кто держит порт на самом деле — вопрос всей проверки.
							mu.Lock()
							if _, seen := foreign[port]; !seen {
								foreign[port] = err.Error()
							}
							mu.Unlock()
						} else {
							conn.Close()
						}

						mu.Lock()
						delete(held, port)
						mu.Unlock()
						pool.Release(port)
					}
				}()
			}
			wg.Wait()

			// Кто на самом деле держал порты, на которых мы споткнулись.
			sockets := listUDPSockets(t)
			reported := 0
			for port := range foreign {
				if owners, busy := sockets[port]; busy && reported < 5 {
					for _, owner := range owners {
						t.Logf("порт %d в момент отказа держал сокет %s:%d (uid=%s, inode=%s)",
							port, owner.addr, owner.port, owner.uid, owner.ino)
					}
					reported++
				}
			}

			stats := pool.Stats()
			t.Logf("аренд=%d коллизий=%d (%.1f%%) выдано_дважды=%d; пул: свободно=%d в_карантине=%d досрочно=%d",
				leases, collisions.Load(), 100*float64(collisions.Load())/float64(leases),
				doubleIssued.Load(), stats.Free, stats.Banned, stats.Hurried)

			// Учёт пула обязан быть точным при любом раскладе: один номер —
			// одному арендатору. Это его собственная работа, и ядро тут ни при чём.
			if doubleIssued.Load() != 0 {
				t.Errorf("пул выдал занятый номер %d раз — ошибка учёта аренды", doubleIssued.Load())
			}

			// А вот столкновения с чужими сокетами — следствие настройки ядра.
			if !scenario.collisionsAllowed && collisions.Load() != 0 {
				t.Errorf("при разведённых диапазонах %d столкновений — их быть не должно",
					collisions.Load())
			}
		})
	}
}

// TestLoad_EverySocketInPoolRangeIsRegistered отвечает на главный вопрос:
// все ли порты, которые проба реально держит, учтены пулом.
//
// Берём срез посреди нагрузки: список настоящих UDP-сокетов процесса из
// /proc/net/udp против списка арендованных номеров. Каждый сокет внутри
// диапазона пула, которого нет в аренде, — это порт, занятый мимо учёта:
// ровно то, из-за чего зонд получает «address already in use» на номере,
// который пул считает свободным.
func TestLoad_EverySocketInPoolRangeIsRegistered(t *testing.T) {
	requireLoadTest(t)

	const (
		measurements = 300
		poolFrom     = 30000
		poolTo       = 31000
	)
	// Ядру — участок в стороне: всё, что окажется в диапазоне пула, окажется
	// там не случайно.
	setEphemeralRange(t, 40000, 45000)

	refl := startTwampReflector(t)
	pool, err := NewPortPool(poolFrom, poolTo)
	if err != nil {
		t.Fatalf("пул не создался: %v", err)
	}
	defer pool.Close()
	runner, _ := embeddedMixRunner(t, pool)

	done := make(chan struct{})
	var wg sync.WaitGroup
	for i := range measurements {
		wg.Add(1)
		go func() {
			defer wg.Done()
			runner.RunForNodes(context.Background(), mixTask(fmt.Sprintf("reg-%d", i),
				"TWamp", ModeTWamp, refl.Addr(), "-c 20 -i 0.05"))
		}()
	}
	go func() { wg.Wait(); close(done) }()

	// Несколько срезов посреди работы: один мог бы попасть в затишье.
	worst := 0
	for shot := range 5 {
		select {
		case <-done:
		case <-time.After(400 * time.Millisecond):
		}

		sockets := listUDPSockets(t)
		pool.mu.Lock()
		leased := make(map[int]bool, len(pool.taken))
		for port := range pool.taken {
			leased[port] = true
		}
		pool.mu.Unlock()

		unregistered := []udpSocket{}
		inRange := 0
		for port, owners := range sockets {
			if port < poolFrom || port > poolTo {
				continue
			}
			inRange += len(owners)
			if !leased[port] {
				unregistered = append(unregistered, owners...)
			}
		}

		t.Logf("срез %d: сокетов в диапазоне пула=%d, в аренде=%d, вне учёта=%d",
			shot+1, inRange, len(leased), len(unregistered))
		for i, socket := range unregistered {
			if i == 3 {
				t.Logf("  …ещё %d", len(unregistered)-3)
				break
			}
			t.Logf("  вне учёта: %s:%d (uid=%s, inode=%s)",
				socket.addr, socket.port, socket.uid, socket.ino)
		}
		if len(unregistered) > worst {
			worst = len(unregistered)
		}
	}
	<-done

	if worst != 0 {
		t.Errorf("до %d UDP-сокетов в диапазоне пула открыты мимо аренды — "+
			"пул считает эти номера свободными и выдаст их следующему замеру", worst)
	}
}

// TestLoad_EmbeddedTwpingUnderPressure гоняет настоящие встроенные замеры TWamp
// массово: так же, как это происходит на боевой пробе.
func TestLoad_EmbeddedTwpingUnderPressure(t *testing.T) {
	requireLoadTest(t)

	const (
		measurements = 400
		poolFrom     = 30000
		poolTo       = 31000
	)
	// Эфемерные порты ядра — в стороне от пула: столкновений быть не должно,
	// и всё, что случится, будет виной учёта, а не настройки.
	setEphemeralRange(t, 40000, 45000)

	refl := startTwampReflector(t)
	pool, err := NewPortPool(poolFrom, poolTo)
	if err != nil {
		t.Fatalf("пул не создался: %v", err)
	}
	defer pool.Close()
	runner, results := embeddedMixRunner(t, pool)

	started := time.Now()
	var wg sync.WaitGroup
	for i := range measurements {
		wg.Add(1)
		go func() {
			defer wg.Done()
			runner.RunForNodes(context.Background(), mixTask(fmt.Sprintf("load-%d", i),
				"TWamp", ModeTWamp, refl.Addr(), "-c 3 -i 0.02"))
		}()
	}
	wg.Wait()

	batch := results.TakeBatch(3 * time.Second).Items
	failed := 0
	for _, item := range batch {
		if item.Outcome != string(OutcomeSuccess) {
			failed++
			if failed <= 3 {
				t.Errorf("замер %s: %s", item.TaskId, firstLine(item.ErrorConsole))
			}
		}
	}

	stats := pool.Stats()
	t.Logf("замеров=%d неудач=%d за %v; пул: свободно=%d в_аренде=%d в_карантине=%d ожиданий=%d досрочно=%d",
		len(batch), failed, time.Since(started).Round(time.Millisecond),
		stats.Free, stats.Taken, stats.Banned, stats.Waited, stats.Hurried)

	if stats.Taken != 0 {
		t.Errorf("после прогона в аренде осталось %d портов — аренда утекает", stats.Taken)
	}
	if stats.Banned != 0 {
		t.Errorf("в карантине %d портов, хотя диапазоны разведены — порт занимает сама проба",
			stats.Banned)
	}
	if len(batch) != measurements {
		t.Errorf("получено %d результатов из %d", len(batch), measurements)
	}
}
