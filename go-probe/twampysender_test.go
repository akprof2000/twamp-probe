package main

import (
	"context"
	"encoding/binary"
	"math"
	"net"
	"strings"
	"sync"
	"testing"
	"time"
)

// twampyReflector — минимальный TWAMP-Light рефлектор: принимает тестовый пакет
// и отражает его с метками t2/t3 в раскладке, которую разбирает отправитель.
type twampyReflector struct {
	port int
	stop func()

	mu      sync.Mutex
	senders map[int]bool // порты, с которых приходили пакеты
}

// SenderPorts возвращает локальные порты отправителей, увиденные с другой
// стороны провода: по ним видно, какой порт зонд занял на самом деле.
func (r *twampyReflector) SenderPorts() []int {
	r.mu.Lock()
	defer r.mu.Unlock()
	ports := make([]int, 0, len(r.senders))
	for port := range r.senders {
		ports = append(ports, port)
	}
	return ports
}

// startTestReflector поднимает рефлектор. Возвращает порт и функцию остановки —
// её и достаточно тестам, которым сами адреса отправителей не нужны.
func startTestReflector(t *testing.T) (int, func()) {
	r := startTwampyReflector(t)
	return r.port, r.stop
}

// startTwampyReflector поднимает рефлектор и запоминает адреса отправителей.
func startTwampyReflector(t *testing.T) *twampyReflector {
	t.Helper()
	conn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatalf("рефлектор не поднялся: %v", err)
	}
	port := conn.LocalAddr().(*net.UDPAddr).Port
	done := make(chan struct{})
	refl := &twampyReflector{port: port, senders: map[int]bool{}}

	go func() {
		defer close(done)
		buf := make([]byte, 9216)
		var rseq uint32
		for {
			_ = conn.SetReadDeadline(time.Now().Add(50 * time.Millisecond))
			n, addr, rerr := conn.ReadFromUDP(buf)
			if rerr != nil {
				if ne, ok := rerr.(net.Error); ok && ne.Timeout() {
					select {
					case <-time.After(0):
					default:
					}
					// проверяем сигнал остановки через закрытие соединения
					if isClosed(conn) {
						return
					}
					continue
				}
				return
			}
			if n < 14 {
				continue
			}
			refl.mu.Lock()
			refl.senders[addr.Port] = true
			refl.mu.Unlock()

			t2 := twNow()
			sseq := binary.BigEndian.Uint32(buf[0:4])
			senderT1 := make([]byte, 8)
			copy(senderT1, buf[4:12]) // метка отправителя t1 — из data[4:12]
			t3 := twNow()

			reply := make([]byte, 36)
			binary.BigEndian.PutUint32(reply[0:4], rseq)
			rseq++
			writeNtp(reply[4:12], t3)
			writeNtp(reply[16:24], t2)
			binary.BigEndian.PutUint32(reply[24:28], sseq)
			copy(reply[28:36], senderT1)
			_, _ = conn.WriteToUDP(reply, addr)
		}
	}()

	refl.stop = func() { _ = conn.Close(); <-done }
	t.Cleanup(refl.stop)
	return refl
}

// isClosed — грубая проверка, что соединение закрыто (для завершения рефлектора).
func isClosed(conn *net.UDPConn) bool {
	_ = conn.SetReadDeadline(time.Now().Add(time.Millisecond))
	_, _, err := conn.ReadFromUDP(make([]byte, 1))
	if err == nil {
		return false
	}
	return strings.Contains(err.Error(), "closed")
}

// writeNtp записывает секунды Unix в 8-байтную метку NTP (big-endian).
func writeNtp(target []byte, unixSeconds float64) {
	binary.BigEndian.PutUint32(target[0:4], uint32(twTimeOffset+math.Floor(unixSeconds)))
	binary.BigEndian.PutUint32(target[4:8], uint32((unixSeconds-math.Floor(unixSeconds))*twAllBits))
}

func TestEmbeddedTwampy_RoundtripProducesTable(t *testing.T) {
	port, stop := startTestReflector(t)
	defer stop()

	args := strings.Fields("sender 127.0.0.1:" + itoa(port) + " :0 -c 5 -i 20")
	output, errText, _ := runEmbeddedTwampy(context.Background(), args, time.Now().Add(5*time.Second))

	if errText != "" {
		t.Fatalf("ошибок быть не должно: %s", errText)
	}
	for _, want := range []string{"Direction", "Outbound:", "Inbound:", "Roundtrip:"} {
		if !strings.Contains(output, want) {
			t.Errorf("в выводе нет %q:\n%s", want, output)
		}
	}
	// Потерь быть не должно — рефлектор ответил на все пакеты.
	if !strings.Contains(output, "0.0%") {
		t.Errorf("ожидались нулевые потери:\n%s", output)
	}
}

func TestEmbeddedTwampy_NoReflectorFullLoss(t *testing.T) {
	// Свободный порт без рефлектора: ответов не будет → NO STATS.
	conn, _ := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	deadPort := conn.LocalAddr().(*net.UDPAddr).Port
	_ = conn.Close()

	// Сессия ждёт ответов count*interval + 5 c (как оригинал), поэтому дедлайн с запасом,
	// иначе таймаут сработает раньше печати NO STATS.
	args := strings.Fields("sender 127.0.0.1:" + itoa(deadPort) + " :0 -c 3 -i 20")
	output, _, _ := runEmbeddedTwampy(context.Background(), args, time.Now().Add(30*time.Second))

	if !strings.Contains(output, "NO STATS AVAILABLE") {
		t.Errorf("ожидался NO STATS при полной потере:\n%s", output)
	}
}

func TestFormatDuration_Units(t *testing.T) {
	cases := []struct {
		ms   float64
		unit string
	}{
		{0.4, "us"},
		{5.0, "ms"},
		{2500.0, "sec"},
		{120000.0, "min"},
	}
	for _, c := range cases {
		got := formatDuration(c.ms)
		if !strings.HasSuffix(got, c.unit) {
			t.Errorf("formatDuration(%v) = %q, ожидалась единица %q", c.ms, got, c.unit)
		}
	}
}

func TestParseTwampyArgs_Defaults(t *testing.T) {
	opts, err := parseTwampyArgs(strings.Fields("sender 10.0.0.5"))
	if err != nil {
		t.Fatalf("разбор не удался: %v", err)
	}
	if opts.remotePort != twDefaultFarPrt {
		t.Errorf("порт по умолчанию = %d, ожидался %d", opts.remotePort, twDefaultFarPrt)
	}
	if opts.count != 100 || opts.intervalMs != 100 {
		t.Errorf("умолчания count/interval неверны: %d/%d", opts.count, opts.intervalMs)
	}
	if opts.localPort != 0 {
		t.Errorf("near-end по умолчанию должен быть :0, получено %d", opts.localPort)
	}
}

func TestParseTwampyArgs_Overrides(t *testing.T) {
	opts, err := parseTwampyArgs(strings.Fields("-m twampy sender 10.0.0.5:5000 :0 -c 10 -i 200 --padding 64"))
	if err != nil {
		t.Fatalf("разбор не удался: %v", err)
	}
	if opts.remotePort != 5000 || opts.count != 10 || opts.intervalMs != 200 {
		t.Errorf("параметры разобраны неверно: порт=%d count=%d interval=%d", opts.remotePort, opts.count, opts.intervalMs)
	}
	if len(opts.padmix) != 1 || opts.padmix[0] != 64 {
		t.Errorf("паддинг должен быть фиксированным [64], получено %v", opts.padmix)
	}
}

// itoa — локальный аналог strconv.Itoa, чтобы не тащить импорт ради тестов.
func itoa(v int) string {
	if v == 0 {
		return "0"
	}
	var b [20]byte
	i := len(b)
	for v > 0 {
		i--
		b[i] = byte('0' + v%10)
		v /= 10
	}
	return string(b[i:])
}

func TestEmbeddedTwampy_CancelStopsMeasurement(t *testing.T) {
	// Удаление задачи на сервере обязано обрывать и встроенный замер — так же,
	// как убийство внешнего процесса. Иначе замер на 300 пакетов продолжал бы
	// идти минутами по уже несуществующей задаче.
	ctx, cancel := context.WithCancel(context.Background())

	// Рефлектора нет: без отмены отправитель ждал бы ответов до конца.
	args := []string{"127.0.0.1:20099", "-c", "1000", "-i", "100"}

	done := make(chan string, 1)
	go func() {
		_, errText, _ := runEmbeddedTwampy(ctx, args, time.Time{})
		done <- errText
	}()

	time.Sleep(200 * time.Millisecond)
	cancel()

	select {
	case errText := <-done:
		if !strings.Contains(errText, "отменена") {
			t.Errorf("замер завершился с «%s», ожидалось сообщение об отмене", errText)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("замер не прервался после отмены — задача продолжала бы работать")
	}
}

func TestEmbeddedTwampy_RespectsTaskTimeout(t *testing.T) {
	// Индивидуальный таймаут задачи действует и во встроенном режиме.
	deadline := time.Now().Add(300 * time.Millisecond)
	args := []string{"127.0.0.1:20098", "-c", "1000", "-i", "100"}

	started := time.Now()
	_, errText, _ := runEmbeddedTwampy(context.Background(), args, deadline)

	if !strings.Contains(errText, "таймауту") {
		t.Errorf("замер завершился с «%s», ожидалось сообщение о таймауте", errText)
	}
	if elapsed := time.Since(started); elapsed > 3*time.Second {
		t.Errorf("замер шёл %v — таймаут не сработал вовремя", elapsed)
	}
}
