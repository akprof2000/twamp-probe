// Пул локальных UDP-портов для зондов.
//
// Зачем: без него каждый зонд просит у ядра любой свободный порт (`bind` на
// порт 0). Когда одновременных замеров тысячи, эфемерный диапазон кончается,
// и ядро отвечает «address already in use» — замер просто пропадает, хотя
// подождать пару секунд было бы достаточно.
//
// Пул решает это двумя способами. Во-первых, порт выдаётся из собственного
// диапазона, который проба ведёт сама, — два замера не могут получить один и
// тот же. Во-вторых, если свободных портов нет, замер **ждёт освобождения**
// вместо того чтобы упасть с ошибкой: очередь лучше потерянного замера.
//
// Диапазон по умолчанию (9000–65000) совпадает с эфемерным диапазоном ядра из
// поставляемого 99-twamp-probe.conf — так замерам TWamp хватает номеров и на
// управляющие TCP-соединения. Цена — редкие столкновения с чужими UDP-сокетами
// машины (чаще всего резолвер DNS): такой порт зонд занять не сможет, сообщит
// об этом, и пул уведёт номер в карантин — см. Blacklist.
//
// Пул один на все режимы: и twping (TWamp), и встроенный отправитель twampy
// берут порт отсюда, поэтому замеры разных режимов не могут получить один и
// тот же номер. Управляющее соединение twping идёт по TCP и на пул не влияет:
// пространства портов TCP и UDP у ядра разные.
//
// Порт выдаётся в аренду: он принадлежит замеру от выдачи до возврата, а после
// возврата не уходит следующему замеру сразу — сначала отлёживается (см.
// portCooldown) и встаёт в конец очереди. Номера тем самым идут по кругу, а не
// перевыбираются между одними и теми же.
package main

import (
	"context"
	"fmt"
	"math/rand"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"
)

// PortPool раздаёт зондам локальные порты и следит, чтобы два замера не взяли
// один и тот же. Нулевой указатель означает «пул выключен» — порт выбирает ядро.
//
// Учёт ведётся отдельно по каждому локальному адресу. Ядро различает сокеты по
// паре «адрес + порт»: замер с 10.0.0.1 и замер с 10.0.0.2 могут занять один
// номер и не помешать друг другу. Общий на всю машину счёт номеров отнимал бы
// у пробы ровно во столько раз больше ёмкости, сколько на ней адресов.
type PortPool struct {
	mu     sync.Mutex
	cond   *sync.Cond
	byAddr map[string]*addrLease // ключ — локальный адрес зонда
	from   int
	to     int
	closed bool
	done   chan struct{} // закрывается в Close — останавливает сборщик

	// quarantine и cooldown — сроки. Поля, а не константы, только ради тестов:
	// ждать в них пять минут никто не станет.
	quarantine time.Duration
	cooldown   time.Duration

	waited  int // сколько раз замеры ждали свободного порта — для журнала
	hurried int // сколько раз порт выдан, не долежав cooldown (пул под нагрузкой)
}

// addrLease — состояние номеров на одном локальном адресе.
type addrLease struct {
	free    []int             // готовые к выдаче, в порядке очереди
	cooling []coolingPort     // возвращённые, отлёживаются перед новой выдачей
	taken   map[int]bool      // в аренде у замеров
	banned  map[int]time.Time // в карантине, и когда он кончится
}

// coolingPort — возвращённый порт и время, когда он снова годен к выдаче.
type coolingPort struct {
	port  int
	until time.Time
}

// leaseFor возвращает состояние номеров для адреса, заводя его при первом
// обращении. Вызывается под замком.
//
// Пустой адрес — отдельный, полноправный ключ: он означает «сокет встанет на
// все адреса сразу» (bind на 0.0.0.0). Такой номер конфликтует с тем же
// номером на любом конкретном адресе, поэтому смешивать эти случаи в одном
// пуле нельзя — см. комментарий к Acquire.
func (p *PortPool) leaseFor(addr string) *addrLease {
	lease, known := p.byAddr[addr]
	if known {
		return lease
	}

	lease = &addrLease{
		free:   make([]int, 0, p.to-p.from+1),
		taken:  map[int]bool{},
		banned: map[int]time.Time{},
	}
	for port := p.from; port <= p.to; port++ {
		lease.free = append(lease.free, port)
	}
	// Перемешиваем: подряд идущие порты у соседних замеров дают всплески
	// в таблицах трансляции сетевого оборудования.
	rand.Shuffle(len(lease.free), func(i, j int) {
		lease.free[i], lease.free[j] = lease.free[j], lease.free[i]
	})

	p.byAddr[addr] = lease
	return lease
}

// portQuarantine — на сколько порт выводится из оборота, если его занял
// посторонний процесс.
//
// Навсегда исключать нельзя: чужой сокет живёт минуты (а TIME_WAIT — и вовсе
// секунды), пул же работает месяцами. При вечном бане каждая случайная
// коллизия отъедала бы номер безвозвратно, и за неделю от диапазона осталась
// бы половина. Пяти минут хватает, чтобы переждать и чужое соединение, и
// TIME_WAIT, но мало, чтобы пул заметно похудел.
const portQuarantine = 5 * time.Minute

// portCooldown — сколько порт отлёживается после возврата, прежде чем уйти
// следующему замеру.
//
// Зачем: закончившийся замер не значит, что о его порте забыли все. Рефлектор
// может ещё досылать ответы на последние пакеты, а сетевое оборудование —
// держать запись трансляции. Если отдать номер сразу, эти хвосты прилетят уже
// новому замеру — на том же порту, с того же адреса, — и попадут в его
// статистику. Секунды хватает, чтобы хвост рассеялся.
//
// Под нагрузкой срок не соблюдается: когда готовых портов не осталось, замеру
// отдаётся самый долго лежащий из отлёживающихся — очередь замеров хуже, чем
// недолежавший порт. Такие случаи считает счётчик hurried.
const portCooldown = time.Second

// NewPortPool создаёт пул портов из диапазона [from, to].
func NewPortPool(from, to int) (*PortPool, error) {
	if from < 1 || to > 65535 || from > to {
		return nil, fmt.Errorf("недопустимый диапазон портов %d-%d", from, to)
	}

	p := &PortPool{
		byAddr: map[string]*addrLease{},
		from:   from,
		to:     to,
		done:   make(chan struct{}),

		quarantine: portQuarantine,
		cooldown:   portCooldown,
	}

	p.cond = sync.NewCond(&p.mu)
	go p.sweepQuarantine()
	return p, nil
}

// quarantineSweep — как часто проверяются сроки карантина.
//
// Отдельная горутина, а не будильник на ближайший срок: одноразовый таймер
// способен сработать между проверкой и засыпанием на sync.Cond, и тогда
// пробуждение потеряется — замер простоит до своего таймаута рядом с портами,
// которые давно свободны. Секундный шаг такой ошибки не допускает и на фоне
// пятиминутного карантина ничего не стоит.
const quarantineSweep = time.Second

// sweepQuarantine возвращает в оборот отсидевшие карантин порты и будит тех,
// кто ждёт свободного. Завершается вместе с пулом.
func (p *PortPool) sweepQuarantine() {
	ticker := time.NewTicker(quarantineSweep)
	defer ticker.Stop()

	for {
		select {
		case <-p.done:
			return
		case <-ticker.C:
			freed := 0
			p.mu.Lock()
			for _, lease := range p.byAddr {
				before := len(lease.free)
				p.expireLease(lease)
				freed += len(lease.free) - before
			}
			p.mu.Unlock()
			if freed > 0 {
				p.cond.Broadcast()
			}
		}
	}
}

// ParsePortPool создаёт пул из настройки вида «9000-65000».
// Пустая строка означает «пул выключен» — тогда возвращается nil без ошибки.
func ParsePortPool(setting string) (*PortPool, error) {
	setting = strings.TrimSpace(setting)
	if setting == "" {
		return nil, nil
	}

	low, high, ok := strings.Cut(setting, "-")
	if !ok {
		return nil, fmt.Errorf("диапазон портов задаётся как «нижний-верхний», получено %q", setting)
	}
	from, err := strconv.Atoi(strings.TrimSpace(low))
	if err != nil {
		return nil, fmt.Errorf("нижняя граница диапазона портов: %w", err)
	}
	to, err := strconv.Atoi(strings.TrimSpace(high))
	if err != nil {
		return nil, fmt.Errorf("верхняя граница диапазона портов: %w", err)
	}
	return NewPortPool(from, to)
}

// Acquire выдаёт свободный порт на локальном адресе addr, дожидаясь
// освобождения, если все заняты. Возвращает 0 и false, если ждать больше нет
// смысла: контекст отменён (задачу сняли или истёк её таймаут) либо пул
// остановлен.
//
// Адрес — тот, с которого зонд пойдёт на узел. Номера считаются по каждому
// адресу отдельно: ядро различает сокеты по паре «адрес + порт», поэтому один
// и тот же номер на разных адресах конфликта не создаёт, и ёмкость пробы
// растёт кратно числу адресов.
//
// Пустой addr означает «зонд встанет на все адреса» (bind на 0.0.0.0). Такой
// сокет занимает номер разом везде, поэтому его учёт ведётся в собственном
// наборе — и, строго говоря, он способен столкнуться с тем же номером на
// конкретном адресе. Проба этого не допускает: адрес она определяет заранее и
// передаёт зонду, так что пустой ключ остаётся только для случаев, когда
// определить его не удалось.
//
// У выключенного пула (nil) порт всегда нулевой — его выберет ядро.
func (p *PortPool) Acquire(ctx context.Context, addr string) (int, bool) {
	if p == nil {
		return 0, true
	}

	p.mu.Lock()
	defer p.mu.Unlock()

	lease := p.leaseFor(addr)
	waited := false
	for {
		p.expireLease(lease)
		if len(lease.free) > 0 || len(lease.cooling) > 0 || p.closed || ctx.Err() != nil {
			break
		}
		if !waited {
			p.waited++
			waited = true
			// sync.Cond не умеет ждать по контексту, поэтому будильник ставит
			// отдельная горутина — ровно одна на всё ожидание, а не на каждый
			// его виток. Она завершится вместе с контекстом задачи.
			go func() {
				<-ctx.Done()
				p.cond.Broadcast()
			}()
		}
		// Разбудит либо Release, либо сборщик: портов, которые никто не вернёт,
		// в пуле не бывает.
		p.cond.Wait()
	}
	if p.closed || ctx.Err() != nil {
		return 0, false
	}

	var port int
	switch {
	case len(lease.free) > 0:
		// Выдаём с головы очереди: там номер, который отдыхал дольше всех.
		// Брать с конца (как раньше) означало бы гонять по кругу два-три
		// последних номера, пока остальной диапазон простаивает.
		port = lease.free[0]
		lease.free = lease.free[1:]

	default:
		// Готовых портов нет — берём самый долго лежащий из отлёживающихся.
		// Очередь замеров хуже недолежавшего порта.
		port = lease.cooling[0].port
		lease.cooling = lease.cooling[1:]
		p.hurried++
	}
	lease.taken[port] = true
	return port, true
}

// Release завершает аренду: порт возвращается в пул того же адреса, но не сразу
// к выдаче — сначала отлёживается portCooldown, чтобы хвосты предыдущего замера
// не попали в следующий.
func (p *PortPool) Release(addr string, port int) {
	if p == nil || port == 0 {
		return
	}

	p.mu.Lock()
	lease := p.leaseFor(addr)
	if lease.taken[port] {
		delete(lease.taken, port)
		if _, quarantined := lease.banned[port]; !quarantined {
			// Срок у всех одинаковый, поэтому append сохраняет очередь
			// упорядоченной по времени готовности.
			lease.cooling = append(lease.cooling,
				coolingPort{port: port, until: time.Now().Add(p.cooldown)})
		}
	}
	p.mu.Unlock()
	// Broadcast, а не Signal: ожидающие ждут порты на разных адресах, и
	// разбуженный наугад может оказаться не тем — он снова уснёт, а тот, для
	// кого номер освободился, так и не проснётся.
	p.cond.Broadcast()
}

// Blacklist уводит порт в карантин на этом адресе: его занял кто-то
// посторонний, и сразу возвращать его в пул незачем — следующий замер налетел
// бы на ту же ошибку. По истечении карантина порт вернётся в оборот сам: чужой
// сокет к тому времени закроется, а безвозвратно терять номера пул не должен.
//
// На других адресах номер остаётся в обороте: занят он именно здесь.
func (p *PortPool) Blacklist(addr string, port int) {
	if p == nil || port == 0 {
		return
	}

	p.mu.Lock()
	lease := p.leaseFor(addr)
	lease.banned[port] = time.Now().Add(p.quarantine)
	delete(lease.taken, port)
	p.mu.Unlock()
	// Broadcast, а не Signal: ожидающие ждут порты на разных адресах, и
	// разбуженный наугад может оказаться не тем — он снова уснёт, а тот, для
	// кого номер освободился, так и не проснётся.
	p.cond.Broadcast()
}

// expireLease переводит в готовые к выдаче порты, отсидевшие свой срок:
// отлежавшиеся после аренды и отбывшие карантин. Вызывается под замком.
func (p *PortPool) expireLease(lease *addrLease) {
	now := time.Now()

	// Очередь отлёживающихся упорядочена по времени, поэтому достаточно снять
	// с головы всё, чей срок вышел.
	ready := 0
	for ready < len(lease.cooling) && !now.Before(lease.cooling[ready].until) {
		lease.free = append(lease.free, lease.cooling[ready].port)
		ready++
	}
	lease.cooling = lease.cooling[ready:]

	for port, until := range lease.banned {
		if now.Before(until) {
			continue
		}
		delete(lease.banned, port)
		// Порт мог быть выдан заново уже после карантина — тогда его вернёт
		// Release, а второй раз класть в free нельзя: два замера получили бы
		// один номер.
		if !lease.taken[port] {
			lease.free = append(lease.free, port)
		}
	}
}

// PoolStats — снимок состояния пула для журнала и страницы состояния.
// Числа сложены по всем локальным адресам, на которых работали зонды.
type PoolStats struct {
	Free      int // готовы к выдаче, включая отлёживающиеся после аренды
	Cooling   int // из них отлёживаются — выданы будут только под нагрузкой
	Taken     int // в аренде у идущих замеров
	Banned    int // в карантине: заняты посторонним процессом
	Waited    int // сколько раз замеры ждали свободного порта
	Hurried   int // сколько раз порт выдан, не долежав cooldown
	Addresses int // локальных адресов в обороте
	Capacity  int // всего номеров: диапазон × число адресов
}

// Stats сообщает состояние пула.
func (p *PortPool) Stats() PoolStats {
	if p == nil {
		return PoolStats{}
	}

	p.mu.Lock()
	defer p.mu.Unlock()

	stats := PoolStats{
		Waited:    p.waited,
		Hurried:   p.hurried,
		Addresses: len(p.byAddr),
		Capacity:  len(p.byAddr) * (p.to - p.from + 1),
	}
	for _, lease := range p.byAddr {
		p.expireLease(lease)
		// Отлёживающиеся числятся свободными: они не заняты замером и будут
		// выданы, как только понадобятся.
		stats.Free += len(lease.free) + len(lease.cooling)
		stats.Cooling += len(lease.cooling)
		stats.Taken += len(lease.taken)
		stats.Banned += len(lease.banned)
	}
	return stats
}

// checkPortBudget сверяет настроенное число одновременных замеров с тем,
// сколько номеров вообще есть в распоряжении пробы. Возвращает текст
// предупреждения или пустую строку.
//
// Считать надо на два порта за замер TWamp: UDP-порт тестового сокета из пула
// и TCP-порт управляющего канала twping из эфемерного диапазона ядра. Оба
// считаются по каждому локальному адресу отдельно, поэтому ёмкость умножается
// на число адресов.
//
// Без этой сверки несоответствие обнаруживается только по россыпи
// «address already in use» в журнале — а выглядит она как проблема сети,
// а не как настройка, которая заведомо не сходится.
func checkPortBudget(maxParallel, poolFrom, poolTo, addresses, ephemeralPerAddr int) string {
	if maxParallel <= 0 || poolFrom <= 0 || addresses <= 0 {
		return ""
	}

	poolCapacity := (poolTo - poolFrom + 1) * addresses
	if maxParallel > poolCapacity {
		return fmt.Sprintf(
			"настроено %d одновременных замеров, а в пуле %d номеров "+
				"(диапазон %d-%d × %d локальных адресов). Лишние замеры будут ждать "+
				"освобождения порта. Расширьте Probe:PortRange или уменьшите Probe:MaxParallel",
			maxParallel, poolCapacity, poolFrom, poolTo, addresses)
	}

	if ephemeralPerAddr <= 0 {
		return ""
	}
	// Управляющие каналы twping живут в эфемерном диапазоне ядра, и этот запас
	// кончается раньше пула, если диапазон узкий.
	if control := ephemeralPerAddr * addresses; maxParallel > control {
		return fmt.Sprintf(
			"настроено %d одновременных замеров, а эфемерных портов ядра %d "+
				"(%d × %d локальных адресов). Каждому замеру TWamp нужен ещё и такой порт "+
				"под управляющий канал twping, поэтому потолок здесь ниже размера пула. "+
				"Расширьте net.ipv4.ip_local_port_range (не пересекая пул) или уменьшите "+
				"Probe:MaxParallel",
			maxParallel, control, ephemeralPerAddr, addresses)
	}
	return ""
}

// localAddressCount считает адреса IPv4, с которых проба может выходить наружу.
// Петлевые не в счёт: замеры через них не идут.
func localAddressCount() int {
	addrs, err := net.InterfaceAddrs()
	if err != nil {
		return 1 // не выяснить — считаем по одному, это никого не обманет
	}

	count := 0
	for _, addr := range addrs {
		ipNet, ok := addr.(*net.IPNet)
		if !ok || ipNet.IP.IsLoopback() || ipNet.IP.To4() == nil {
			continue
		}
		count++
	}
	if count == 0 {
		return 1
	}
	return count
}

// Range возвращает границы диапазона (0, 0 — пул выключен).
func (p *PortPool) Range() (int, int) {
	if p == nil {
		return 0, 0
	}
	return p.from, p.to
}

// Close отпускает всех ожидающих (остановка службы).
func (p *PortPool) Close() {
	if p == nil {
		return
	}

	p.mu.Lock()
	if !p.closed {
		p.closed = true
		close(p.done)
	}
	p.mu.Unlock()
	p.cond.Broadcast()
}
