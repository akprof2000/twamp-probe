package main

import (
	"context"
	"net"
	"runtime"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestPortPool_GivesDistinctPorts(t *testing.T) {
	// Главное свойство пула: два замера не получают один и тот же порт.
	// Именно на этом и спотыкались зонды, когда порт выбирало ядро.
	pool, err := NewPortPool(20000, 20009)
	if err != nil {
		t.Fatalf("пул не создался: %v", err)
	}

	seen := map[int]bool{}
	for range 10 {
		port, ok := pool.Acquire(context.Background())
		if !ok {
			t.Fatal("пул отказал, хотя свободные порты есть")
		}
		if seen[port] {
			t.Fatalf("порт %d выдан дважды", port)
		}
		if port < 20000 || port > 20009 {
			t.Errorf("порт %d вне диапазона 20000-20009", port)
		}
		seen[port] = true
	}
}

func TestPortPool_WaitsInsteadOfFailing(t *testing.T) {
	// Когда свободных портов нет, замер должен подождать освобождения, а не
	// упасть с «address already in use»: очередь лучше потерянного замера.
	pool, err := NewPortPool(20000, 20000) // ровно один порт
	if err != nil {
		t.Fatalf("пул не создался: %v", err)
	}

	first, ok := pool.Acquire(context.Background())
	if !ok {
		t.Fatal("не удалось занять единственный порт")
	}

	got := make(chan int, 1)
	go func() {
		port, ok := pool.Acquire(context.Background())
		if ok {
			got <- port
		}
	}()

	select {
	case port := <-got:
		t.Fatalf("порт %d выдан дважды — ожидание не сработало", port)
	case <-time.After(200 * time.Millisecond):
		// ожидаемо: второй замер ждёт
	}

	pool.Release(first)

	select {
	case port := <-got:
		if port != first {
			t.Errorf("выдан порт %d, ожидался освободившийся %d", port, first)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("освобождение порта не разбудило ожидающего")
	}

	if _, _, _, waited := pool.Stats(); waited != 1 {
		t.Errorf("ожиданий засчитано %d, ожидалось 1", waited)
	}
}

func TestPortPool_CancelStopsWaiting(t *testing.T) {
	// Снятая задача не должна ждать порт вечно.
	pool, _ := NewPortPool(20000, 20000)
	if _, ok := pool.Acquire(context.Background()); !ok {
		t.Fatal("не удалось занять единственный порт")
	}

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan bool, 1)
	go func() {
		_, ok := pool.Acquire(ctx)
		done <- ok
	}()

	time.Sleep(100 * time.Millisecond)
	cancel()

	select {
	case ok := <-done:
		if ok {
			t.Error("после отмены выдан порт, ожидался отказ")
		}
	case <-time.After(2 * time.Second):
		t.Fatal("отмена не прервала ожидание порта")
	}
}

func TestPortPool_BlacklistRemovesPortFromRotation(t *testing.T) {
	// Порт, занятый посторонним процессом, в оборот больше не идёт: иначе
	// каждый следующий замер налетал бы на ту же ошибку.
	pool, _ := NewPortPool(20000, 20001)

	bad, _ := pool.Acquire(context.Background())
	pool.Blacklist(bad)

	good, ok := pool.Acquire(context.Background())
	if !ok {
		t.Fatal("пул отказал, хотя один порт ещё свободен")
	}
	if good == bad {
		t.Errorf("исключённый порт %d выдан снова", bad)
	}

	// Второй порт занят, исключённый не возвращается — свободных нет.
	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()
	if _, ok := pool.Acquire(ctx); ok {
		t.Error("выдан порт, хотя свободных не осталось")
	}

	if _, _, banned, _ := pool.Stats(); banned != 1 {
		t.Errorf("исключённых портов %d, ожидался 1", banned)
	}
}

func TestPortPool_DisabledPoolIsTransparent(t *testing.T) {
	// Пустая настройка означает прежнее поведение: порт выбирает ядро.
	pool, err := ParsePortPool("")
	if err != nil {
		t.Fatalf("пустая настройка дала ошибку: %v", err)
	}
	if pool != nil {
		t.Fatal("пустая настройка должна выключать пул")
	}

	port, ok := pool.Acquire(context.Background())
	if !ok || port != 0 {
		t.Errorf("выключенный пул вернул порт %d (ok=%v), ожидался 0", port, ok)
	}
	pool.Release(port) // не должно паниковать
	pool.Blacklist(port)
	pool.Close()
}

func TestParsePortPool_Errors(t *testing.T) {
	for _, bad := range []string{"20000", "abc-30000", "20000-abc", "30000-20000", "0-100", "1-70000"} {
		if _, err := ParsePortPool(bad); err == nil {
			t.Errorf("настройка %q принята, ожидалась ошибка", bad)
		}
	}
	pool, err := ParsePortPool(" 20000 - 32767 ")
	if err != nil {
		t.Fatalf("корректная настройка отклонена: %v", err)
	}
	if from, to := pool.Range(); from != 20000 || to != 32767 {
		t.Errorf("разобран диапазон %d-%d, ожидался 20000-32767", from, to)
	}
}

func TestWithLocalPort(t *testing.T) {
	// Каждой утилите порт передаётся её способом.
	twamp := withLocalPort(ModeTWamp, []string{"-c", "10", "10.0.0.1"}, 20005)
	if len(twamp) < 2 || twamp[0] != "-P" || twamp[1] != "20005-20005" {
		t.Errorf("twping получил %v, ожидался флаг -P 20005-20005", twamp)
	}

	// Свой диапазон задачи не перебиваем.
	own := withLocalPort(ModeTWamp, []string{"-P", "9000-9100", "10.0.0.1"}, 20005)
	if own[1] != "9000-9100" {
		t.Errorf("диапазон задачи заменён: %v", own)
	}

	// near-end не задан — добавляем «:порт» сразу за адресом рефлектора.
	twampy := withLocalPort(ModeTWampy,
		[]string{"-m", "twampy", "sender", "10.0.0.1", "-c", "10"}, 20006)
	if len(twampy) < 5 || twampy[4] != ":20006" {
		t.Errorf("twampy получил %v, ожидался near-end :20006 после узла", twampy)
	}

	// near-end задан задачей: порт дописывается к нему, а не вставляется
	// отдельным аргументом — иначе адрес съехал бы на третью позицию и замер
	// пошёл бы не с того интерфейса.
	withNear := withLocalPort(ModeTWampy,
		[]string{"-m", "twampy", "sender", "10.93.47.146:5018", "10.123.20.140",
			"-c", "150", "-i", "1000"}, 20006)
	want := []string{"-m", "twampy", "sender", "10.93.47.146:5018", "10.123.20.140:20006",
		"-c", "150", "-i", "1000"}
	if !slices.Equal(withNear, want) {
		t.Errorf("получено %v, ожидалось %v", withNear, want)
	}

	// Задача указала и адрес, и порт — её выбор важнее.
	ownPort := withLocalPort(ModeTWampy,
		[]string{"-m", "twampy", "sender", "10.0.0.1", "10.123.20.140:5000"}, 20006)
	if ownPort[4] != "10.123.20.140:5000" {
		t.Errorf("порт задачи заменён: %v", ownPort)
	}

	// IPv6 в скобках: двоеточия внутри адреса за порт не считаются.
	v6 := withLocalPort(ModeTWampy,
		[]string{"-m", "twampy", "sender", "[2001:db8::1]:5018", "[2001:db8::2]"}, 20006)
	if v6[4] != "[2001:db8::2]:20006" {
		t.Errorf("IPv6 near-end получил %q, ожидался [2001:db8::2]:20006", v6[4])
	}

	// Для ping локальный порт бессмыслен.
	ping := withLocalPort(ModeWinPing, []string{"10.0.0.1", "-c", "2"}, 20007)
	if len(ping) != 3 {
		t.Errorf("аргументы ping изменены: %v", ping)
	}

	// Выключенный пул ничего не добавляет.
	off := withLocalPort(ModeTWamp, []string{"-c", "10", "10.0.0.1"}, 0)
	if len(off) != 3 {
		t.Errorf("при выключенном пуле аргументы изменены: %v", off)
	}
}

func TestExecute_RetriesOnBusyPort(t *testing.T) {
	// Порт из пула может оказаться занят посторонним процессом. Раньше такой
	// замер просто пропадал; теперь проба берёт другой порт и повторяет.
	// Занимаем часть пула по-настоящему, чтобы отказ пришёл от ядра.
	//
	// Только Linux: в Windows два UDP-сокета уживаются на одном порту, поэтому
	// занятый порт там не отказывает и проверять нечего.
	if runtime.GOOS != "linux" {
		t.Skip("проверка занятого UDP-порта имеет смысл только на Linux")
	}
	pool, err := NewPortPool(21500, 21504)
	if err != nil {
		t.Fatalf("пул не создался: %v", err)
	}

	// Держим все порты пула, кроме одного: попытки будут упираться в занятые
	// номера, а замер обязан состояться на оставшемся. Это детерминировано:
	// каждый занятый порт исключается из пула, поэтому за четыре неудачи выбор
	// сузится до единственного свободного — и попыток на это хватает.
	var held []*net.UDPConn
	free := 0
	for port := 21500; port <= 21504; port++ {
		if port == 21502 {
			free = port
			continue // этот оставляем свободным
		}
		conn, err := net.ListenUDP("udp4", &net.UDPAddr{Port: port})
		if err != nil {
			t.Skipf("порт %d занят посторонним процессом — проверка невозможна: %v", port, err)
		}
		held = append(held, conn)
	}
	t.Cleanup(func() {
		for _, c := range held {
			c.Close()
		}
	})

	cfg := &Config{TwampyEmbedded: true, Twampy: ProbeToolConfig{Name: "python-не-существует"}}
	results := NewResultStore(10, 0)
	runner := NewProbeRunner(cfg, results, NewRunRegistry(), NewRunCancelRegistry(), pool)

	task := &TaskInfo{
		Id: "busy-port", Title: "занятый порт", Mode: ModeTWampy,
		EndNode: "127.0.0.1:20999", Circles: 1, Repeats: 1, TimeoutSec: 20,
		Parameters: map[string]string{"args": "-c 1 -i 100"},
	}
	runner.RunForNodes(context.Background(), task)

	batch := results.TakeBatch(10).Items
	if len(batch) != 1 {
		t.Fatalf("получено результатов: %d, ожидался 1", len(batch))
	}
	if portTaken(batch[0].ErrorConsole) {
		t.Errorf("замер так и не нашёл свободный порт: %s", batch[0].ErrorConsole)
	}
	if !strings.Contains(batch[0].CallLine, strconv.Itoa(free)) {
		t.Errorf("замер выполнен не на свободном порту %d: %s", free, batch[0].CallLine)
	}

	// Занятые порты исключены из оборота — следующий замер их не получит.
	// Сколько именно, зависит от везения (пул перемешан, свободный номер мог
	// выпасть первым), поэтому число не проверяем — только сам факт замера.
	_, _, banned, _ := pool.Stats()
	t.Logf("замер прошёл на порту %d, исключено занятых портов: %d", free, banned)
}
