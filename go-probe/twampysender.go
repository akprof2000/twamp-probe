// Встроенный отправитель TWAMP-Light (эксперимент): порт режима «sender» из
// nokia/twampy на Go, работающий прямо в процессе пробы — без запуска внешнего
// python. Формат тестовых пакетов, вычисление задержек/джиттера [RFC1889]/потерь
// и текст итоговой таблицы воспроизведены так, чтобы серверный TwampyParser
// разбирал вывод один-в-один с оригиналом (и со встроенным C#-отправителем).
//
// Зачем: внешний замер стоит системе процесса и потока, который его ждёт, —
// именно этим ограничено число одновременных замеров (см. docs/parallelism.md).
// Встроенный отправитель не запускает процесс вовсе: замер идёт в горутине,
// поэтому предел «процесс + поток» на него не распространяется. Плюс исчезают
// накладные расходы на старт интерпретатора python при каждом замере.
package main

import (
	"context"
	"encoding/binary"
	"fmt"
	"math"
	"math/rand"
	"net"
	"strconv"
	"strings"
	"time"
)

const (
	twTimeOffset    = 2208988800.0 // разница эпох NTP (1900) и Unix (1970), секунд
	twAllBits       = 4294967295.0 // маска 32-битной дробной части секунды NTP (0xFFFFFFFF)
	twDefaultFarPrt = 20001        // порт рефлектора по умолчанию (far-end)
)

// twNow — текущее время в секундах Unix с высоким разрешением.
func twNow() float64 { return float64(time.Now().UnixNano()) / 1e9 }

// twampyOptions — разобранные параметры отправителя.
type twampyOptions struct {
	remoteIP   net.IP
	remotePort int
	localPort  int
	count      int
	intervalMs int
	tos        int
	ttl        int
	padmix     []int
}

// runEmbeddedTwampy проводит замер встроенным отправителем и возвращает текст
// итоговой таблицы (stdout) и текст ошибки (stderr-аналог). Совместим по формату
// с python-выводом, поэтому серверный парсер работает без изменений.
func runEmbeddedTwampy(ctx context.Context, args []string, deadline time.Time) (output, errText string) {
	opts, err := parseTwampyArgs(args)
	if err != nil {
		return "", fmt.Sprintf("Некорректные аргументы twampy sender: %v", err)
	}

	table, runErr := twampySession(ctx, opts, deadline)
	if runErr != nil {
		return "", fmt.Sprintf("Ошибка встроенного twampy sender: %v", runErr)
	}
	return table, ""
}

// twampySession проводит один сеанс отправителя и возвращает текст таблицы.
func twampySession(ctx context.Context, opts *twampyOptions, deadline time.Time) (string, error) {
	network := "udp4"
	if opts.remoteIP.To4() == nil {
		network = "udp6"
	}
	conn, err := net.ListenUDP(network, &net.UDPAddr{Port: opts.localPort})
	if err != nil {
		return "", fmt.Errorf("не удалось открыть UDP-сокет: %w", err)
	}
	defer conn.Close()

	remote := &net.UDPAddr{IP: opts.remoteIP, Port: opts.remotePort}
	stats := &twampStats{}
	buf := make([]byte, 9216)

	interval := time.Duration(opts.intervalMs) * time.Millisecond
	schedule := twNow()
	endTime := schedule + float64(opts.count)*interval.Seconds() + 5.0
	idx := 0
	rng := rand.New(rand.NewSource(time.Now().UnixNano()))

	for {
		if !deadline.IsZero() && time.Now().After(deadline) {
			return "", errTimeout
		}
		// Задачу удалили на сервере или проба останавливается — замер обрывается
		// сразу, как это делает убийство внешнего процесса.
		if err := ctx.Err(); err != nil {
			return "", errCancelled
		}

		// Забираем все уже пришедшие ответы (неблокирующе).
		for {
			_ = conn.SetReadDeadline(time.Now()) // немедленный возврат, если данных нет
			n, _, rerr := conn.ReadFromUDP(buf)
			if rerr != nil || n < 36 {
				break
			}
			if handleReply(buf[:n], stats)+1 == uint32(opts.count) {
				return stats.dump(idx), nil // получены все ответы
			}
		}

		sendTime := twNow()
		if sendTime >= schedule && idx < opts.count {
			schedule += interval.Seconds()
			if err := sendTwampyPacket(conn, remote, opts, idx, sendTime, rng); err != nil {
				return "", fmt.Errorf("отправка пакета: %w", err)
			}
			idx++
		}

		if sendTime > endTime {
			return stats.dump(idx), nil
		}

		// Единственная точка ожидания в цикле: спим на сокете до ближайшего
		// события — до следующего слота отправки, а когда всё отправлено, —
		// до истечения времени ожидания ответов. Без этого цикл крутился бы
		// вхолостую и занимал ядро почти целиком.
		wakeAt := endTime
		if idx < opts.count {
			wakeAt = schedule
		}
		if wait := wakeAt - twNow(); wait > 0 {
			_ = conn.SetReadDeadline(time.Now().Add(time.Duration(wait * float64(time.Second))))
			if n, _, rerr := conn.ReadFromUDP(buf); rerr == nil && n >= 36 {
				if handleReply(buf[:n], stats)+1 == uint32(opts.count) {
					return stats.dump(idx), nil
				}
			}
		}
	}
}

// errTimeout — сеанс прерван по внешнему таймауту задачи.
var errTimeout = fmt.Errorf("замер прерван по таймауту")

// errCancelled — сеанс прерван отменой: задачу удалили или проба остановлена.
var errCancelled = fmt.Errorf("замер прерван: задача отменена")

// handleReply разбирает один ответ рефлектора, добавляет его в статистику
// и возвращает номер пакета отправителя (sseq) — по нему видно, все ли ответы получены.
func handleReply(data []byte, stats *twampStats) uint32 {
	if len(data) < 36 {
		return 0
	}
	t4 := twNow()
	t3 := ntpToSeconds(data[4:12])
	t2 := ntpToSeconds(data[16:24])
	t1 := ntpToSeconds(data[28:36])
	delayRT := math.Max(0, 1000*(t4-t1+t2-t3))
	delayOB := math.Max(0, 1000*(t2-t1))
	delayIB := math.Max(0, 1000*(t4-t3))
	sseq := binary.BigEndian.Uint32(data[24:28])
	stats.add(delayRT, delayOB, delayIB, binary.BigEndian.Uint32(data[0:4]), sseq)
	return sseq
}

// sendTwampyPacket отправляет один тестовый пакет (формат TWAMP-Light sender).
func sendTwampyPacket(conn *net.UDPConn, remote *net.UDPAddr, opts *twampyOptions, seq int, t1 float64, rng *rand.Rand) error {
	// Заголовок 14 байт: seq(4) + NTP секунды(4) + NTP дробь(4) + оценка ошибки 0x3FFF(2).
	padding := opts.padmix[rng.Intn(len(opts.padmix))]
	packet := make([]byte, 14+padding)
	binary.BigEndian.PutUint32(packet[0:4], uint32(seq))
	binary.BigEndian.PutUint32(packet[4:8], uint32(twTimeOffset+math.Floor(t1)))
	binary.BigEndian.PutUint32(packet[8:12], uint32((t1-math.Floor(t1))*twAllBits))
	binary.BigEndian.PutUint16(packet[12:14], 0x3FFF)
	// Остаток пакета — нули (паддинг), срез уже обнулён.

	_, err := conn.WriteToUDP(packet, remote)
	return err
}

// ntpToSeconds преобразует 8-байтную метку NTP (сек+дробь, big-endian) в секунды Unix.
func ntpToSeconds(ntp []byte) float64 {
	seconds := binary.BigEndian.Uint32(ntp[0:4])
	fraction := binary.BigEndian.Uint32(ntp[4:8])
	return float64(seconds) - twTimeOffset + float64(fraction)/twAllBits
}

// formatDuration — формат длительности из twampy: min/sec/ms/us по величине.
func formatDuration(ms float64) string {
	abs := math.Abs(ms)
	switch {
	case abs > 60000:
		return fmt.Sprintf("%7.1fmin", ms/60000)
	case abs > 10000:
		return fmt.Sprintf("%7.1fsec", ms/1000)
	case abs > 1000:
		return fmt.Sprintf("%7.2fsec", ms/1000)
	case abs > 1:
		return fmt.Sprintf("%8.2fms", ms)
	default:
		return fmt.Sprintf("%8dus", int(ms*1000))
	}
}

// twampStats — накопитель статистики сеанса, точный порт TwampStatistics из twampy.
type twampStats struct {
	count                        int
	minOB, minIB, minRT          float64
	maxOB, maxIB, maxRT          float64
	sumOB, sumIB, sumRT          float64
	jitterOB, jitterIB, jitterRT float64
	lastOB, lastIB, lastRT       float64
	lossIB, lossOB               int64
}

// add добавляет один ответ: задержки RT/OB/IB и последовательности рефлектора/отправителя.
func (s *twampStats) add(delayRT, delayOB, delayIB float64, rseq, sseq uint32) {
	if s.count == 0 {
		s.minOB, s.maxOB, s.sumOB, s.lastOB = delayOB, delayOB, delayOB, delayOB
		s.minIB, s.maxIB, s.sumIB, s.lastIB = delayIB, delayIB, delayIB, delayIB
		s.minRT, s.maxRT, s.sumRT, s.lastRT = delayRT, delayRT, delayRT, delayRT
		s.lossIB = int64(rseq)
		s.lossOB = int64(sseq) - int64(rseq)
		s.jitterOB, s.jitterIB, s.jitterRT = 0, 0, 0
	} else {
		s.minOB, s.minIB, s.minRT = math.Min(s.minOB, delayOB), math.Min(s.minIB, delayIB), math.Min(s.minRT, delayRT)
		s.maxOB, s.maxIB, s.maxRT = math.Max(s.maxOB, delayOB), math.Max(s.maxIB, delayIB), math.Max(s.maxRT, delayRT)
		s.sumOB, s.sumIB, s.sumRT = s.sumOB+delayOB, s.sumIB+delayIB, s.sumRT+delayRT
		s.lossIB = int64(rseq) - int64(s.count)
		s.lossOB = int64(sseq) - int64(rseq)

		if s.count == 1 {
			s.jitterOB = math.Abs(s.lastOB - delayOB)
			s.jitterIB = math.Abs(s.lastIB - delayIB)
			s.jitterRT = math.Abs(s.lastRT - delayRT)
		} else {
			s.jitterOB += (math.Abs(s.lastOB-delayOB) - s.jitterOB) / 16
			s.jitterIB += (math.Abs(s.lastIB-delayIB) - s.jitterIB) / 16
			s.jitterRT += (math.Abs(s.lastRT-delayRT) - s.jitterRT) / 16
		}
		s.lastOB, s.lastIB, s.lastRT = delayOB, delayIB, delayRT
	}
	s.count++
}

// dump печатает таблицу направлений — тот же текст, что и twampy sender.
func (s *twampStats) dump(total int) string {
	const bar = "==============================================================================="
	const dash = "-------------------------------------------------------------------------------"
	var sb strings.Builder
	sb.WriteString(bar + "\n")
	sb.WriteString("Direction         Min         Max         Avg          Jitter     Loss\n")
	sb.WriteString(dash + "\n")

	if s.count > 0 && total > 0 {
		lossRT := int64(total - s.count)
		sb.WriteString("  Outbound:    " + statRow(s.minOB, s.maxOB, s.sumOB/float64(s.count), s.jitterOB, s.lossOB, total) + "\n")
		sb.WriteString("  Inbound:     " + statRow(s.minIB, s.maxIB, s.sumIB/float64(s.count), s.jitterIB, s.lossIB, total) + "\n")
		sb.WriteString("  Roundtrip:   " + statRow(s.minRT, s.maxRT, s.sumRT/float64(s.count), s.jitterRT, lossRT, total) + "\n")
	} else {
		sb.WriteString("  NO STATS AVAILABLE (100% loss)\n")
	}

	sb.WriteString(dash + "\n")
	sb.WriteString("                                                    Jitter Algorithm [RFC1889]\n")
	sb.WriteString(bar + "\n")
	return sb.String()
}

// statRow собирает одну строку направления: min/max/avg/jitter + процент потерь.
func statRow(min, max, avg, jitter float64, loss int64, total int) string {
	lossPercent := 100 * float64(loss) / float64(total)
	return fmt.Sprintf("%s  %s  %s  %s    %5.1f%%",
		formatDuration(min), formatDuration(max), formatDuration(avg), formatDuration(jitter), lossPercent)
}

// parseTwampyArgs разбирает строку аргументов sender'а в параметры.
// Раскладка: [-m twampy] sender <far> [<near>] [-c N] [-i мс] [--padding B] [--tos T] [--ttl H].
func parseTwampyArgs(tokens []string) (*twampyOptions, error) {
	count, interval, padding, tos, ttl := 100, 100, 0, 0x88, 64
	var positionals []string

	for i := 0; i < len(tokens); i++ {
		switch tokens[i] {
		case "-m", "twampy", "sender":
			// префикс запуска — пропускаем
		case "-c", "--count":
			count = nextInt(tokens, &i, count)
		case "-i", "--interval":
			interval = nextInt(tokens, &i, interval)
		case "--padding":
			padding = nextInt(tokens, &i, padding)
		case "--tos":
			tos = nextInt(tokens, &i, tos)
		case "--ttl":
			ttl = nextInt(tokens, &i, ttl)
		case "-d", "-v", "-q", "--do-not-fragment":
			// флаги без значения — не влияют на замер
		default:
			if !strings.HasPrefix(tokens[i], "-") {
				positionals = append(positionals, tokens[i])
			}
		}
	}

	if len(positionals) == 0 {
		return nil, fmt.Errorf("не указан адрес рефлектора (far-end)")
	}

	farIP, farPort, err := parseTwampyAddr(positionals[0], twDefaultFarPrt)
	if err != nil {
		return nil, err
	}
	localPort := 0 // near-end по умолчанию :0 (эфемерный порт)
	if len(positionals) > 1 {
		if _, p, perr := parseTwampyAddr(positionals[1], 0); perr == nil {
			localPort = p
		}
	}

	ip, err := resolveTwampyIP(farIP)
	if err != nil {
		return nil, err
	}

	var padmix []int
	switch {
	case padding > 0:
		padmix = []int{padding}
	case ip.To4() == nil:
		padmix = []int{0, 0, 0, 0, 0, 0, 0, 514, 514, 514, 514, 1438}
	default:
		padmix = []int{8, 8, 8, 8, 8, 8, 8, 534, 534, 534, 534, 1458}
	}

	return &twampyOptions{
		remoteIP:   ip,
		remotePort: farPort,
		localPort:  localPort,
		count:      clampInt(count, 1, 9999),
		intervalMs: maxInt(1, interval),
		tos:        tos,
		ttl:        clampInt(ttl, 1, 255),
		padmix:     padmix,
	}, nil
}

// nextInt читает целочисленное значение следующего токена, сдвигая индекс.
func nextInt(tokens []string, i *int, fallback int) int {
	if *i+1 < len(tokens) {
		if v, err := strconv.Atoi(tokens[*i+1]); err == nil {
			*i++
			return v
		}
	}
	return fallback
}

// parseTwampyAddr разбирает «ip:port» / «[ipv6]:port» / «ip» / «:port» в адрес и порт.
func parseTwampyAddr(addr string, defaultPort int) (string, int, error) {
	if addr == "" || addr == ":0" {
		return "", 0, nil
	}
	if strings.HasPrefix(addr, ":") {
		if p, err := strconv.Atoi(addr[1:]); err == nil {
			return "", p, nil // «:port» — только локальный порт
		}
	}
	if strings.Contains(addr, "]:") { // IPv6 с портом
		idx := strings.LastIndex(addr, ":")
		p, err := strconv.Atoi(addr[idx+1:])
		if err != nil {
			return "", 0, fmt.Errorf("неверный порт в «%s»", addr)
		}
		return strings.Trim(addr[:idx], "[]"), p, nil
	}
	if strings.Contains(addr, "]") || strings.Count(addr, ":") > 1 { // IPv6 без порта
		return strings.Trim(addr, "[]"), defaultPort, nil
	}
	if strings.Contains(addr, ":") { // IPv4 с портом
		parts := strings.SplitN(addr, ":", 2)
		p, err := strconv.Atoi(parts[1])
		if err != nil {
			return "", 0, fmt.Errorf("неверный порт в «%s»", addr)
		}
		return parts[0], p, nil
	}
	return addr, defaultPort, nil // IPv4 без порта
}

// resolveTwampyIP преобразует адрес рефлектора в net.IP (с DNS-резолвингом по имени).
func resolveTwampyIP(host string) (net.IP, error) {
	if host == "" {
		return net.IPv4(127, 0, 0, 1), nil
	}
	if ip := net.ParseIP(host); ip != nil {
		return ip, nil
	}
	addrs, err := net.LookupIP(host)
	if err != nil || len(addrs) == 0 {
		return nil, fmt.Errorf("не удалось разрешить адрес «%s»: %w", host, err)
	}
	// IPv4 в приоритете.
	for _, a := range addrs {
		if a.To4() != nil {
			return a, nil
		}
	}
	return addrs[0], nil
}

// clampInt ограничивает значение диапазоном [lo, hi].
func clampInt(v, lo, hi int) int { return maxInt(lo, minInt(v, hi)) }

// minInt/maxInt — целочисленные min/max (совместимость с ранними Go 1.2x без generics-встроек).
func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}

func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
