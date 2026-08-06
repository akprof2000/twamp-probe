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
// Диапазон по умолчанию (20000–32767) выбран ниже эфемерного диапазона Linux
// (32768–60999): оттуда ядро порты не раздаёт, поэтому столкнуться с чужим
// сокетом почти невозможно. Если порт всё же занят посторонним процессом,
// зонд сообщит об этом, и пул уведёт порт в карантин — см. Blacklist.
//
// Пул один на все режимы: и twping (TWamp), и встроенный отправитель twampy
// берут порт отсюда, поэтому замеры разных режимов не могут получить один и
// тот же номер. Управляющее соединение twping идёт по TCP и на пул не влияет:
// пространства портов TCP и UDP у ядра разные.
package main

import (
	"context"
	"fmt"
	"math/rand"
	"strconv"
	"strings"
	"sync"
	"time"
)

// PortPool раздаёт зондам локальные порты и следит, чтобы два замера не взяли
// один и тот же. Нулевой указатель означает «пул выключен» — порт выбирает ядро.
type PortPool struct {
	mu     sync.Mutex
	cond   *sync.Cond
	free   []int
	taken  map[int]bool
	banned map[int]time.Time // порты в карантине и время его окончания
	from   int
	to     int
	closed bool
	done   chan struct{} // закрывается в Close — останавливает сборщик карантина

	// quarantine — срок карантина. Поле, а не константа, только ради тестов:
	// ждать в них пять минут никто не станет.
	quarantine time.Duration

	waited int // сколько раз замеры ждали свободного порта — для журнала
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

// NewPortPool создаёт пул портов из диапазона [from, to].
func NewPortPool(from, to int) (*PortPool, error) {
	if from < 1 || to > 65535 || from > to {
		return nil, fmt.Errorf("недопустимый диапазон портов %d-%d", from, to)
	}

	p := &PortPool{
		free:   make([]int, 0, to-from+1),
		taken:  make(map[int]bool, to-from+1),
		banned: map[int]time.Time{},
		from:   from,
		to:     to,
		done:   make(chan struct{}),

		quarantine: portQuarantine,
	}
	for port := from; port <= to; port++ {
		p.free = append(p.free, port)
	}
	// Перемешиваем: подряд идущие порты у соседних замеров дают всплески
	// в таблицах трансляции сетевого оборудования.
	rand.Shuffle(len(p.free), func(i, j int) { p.free[i], p.free[j] = p.free[j], p.free[i] })

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
			p.mu.Lock()
			before := len(p.free)
			p.expireLocked()
			freed := len(p.free) - before
			p.mu.Unlock()
			if freed > 0 {
				p.cond.Broadcast()
			}
		}
	}
}

// ParsePortPool создаёт пул из настройки вида «20000-32767».
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

// Acquire выдаёт свободный порт, дожидаясь освобождения, если все заняты.
// Возвращает 0 и false, если ждать больше нет смысла: контекст отменён
// (задачу сняли или истёк её таймаут) либо пул остановлен.
//
// У выключенного пула (nil) порт всегда нулевой — его выберет ядро.
func (p *PortPool) Acquire(ctx context.Context) (int, bool) {
	if p == nil {
		return 0, true
	}

	p.mu.Lock()
	defer p.mu.Unlock()

	waited := false
	for {
		p.expireLocked()
		if len(p.free) > 0 || p.closed || ctx.Err() != nil {
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
		// Разбудит либо Release, либо сборщик карантина: портов, которые никто
		// не вернёт, в пуле не бывает.
		p.cond.Wait()
	}
	if p.closed || ctx.Err() != nil {
		return 0, false
	}

	port := p.free[len(p.free)-1]
	p.free = p.free[:len(p.free)-1]
	p.taken[port] = true
	return port, true
}

// Release возвращает порт в пул.
func (p *PortPool) Release(port int) {
	if p == nil || port == 0 {
		return
	}

	p.mu.Lock()
	if p.taken[port] {
		delete(p.taken, port)
		if _, quarantined := p.banned[port]; !quarantined {
			p.free = append(p.free, port)
		}
	}
	p.mu.Unlock()
	p.cond.Signal()
}

// Blacklist уводит порт в карантин: его занял кто-то посторонний, и сразу
// возвращать его в пул незачем — следующий замер налетел бы на ту же ошибку.
// По истечении карантина порт вернётся в оборот сам: чужой сокет к тому
// времени закроется, а безвозвратно терять номера пул не должен.
func (p *PortPool) Blacklist(port int) {
	if p == nil || port == 0 {
		return
	}

	p.mu.Lock()
	p.banned[port] = time.Now().Add(p.quarantine)
	delete(p.taken, port)
	p.mu.Unlock()
	p.cond.Signal()
}

// expireLocked возвращает в оборот порты, отсидевшие карантин.
// Вызывается под захваченным замком.
func (p *PortPool) expireLocked() {
	if len(p.banned) == 0 {
		return
	}

	now := time.Now()
	for port, until := range p.banned {
		if now.Before(until) {
			continue
		}
		delete(p.banned, port)
		// Порт мог быть выдан заново уже после карантина — тогда его вернёт
		// Release, а второй раз класть в free нельзя: два замера получили бы
		// один номер.
		if !p.taken[port] {
			p.free = append(p.free, port)
		}
	}
}

// Stats сообщает состояние пула: свободно, занято, в карантине, сколько раз ждали.
func (p *PortPool) Stats() (free, taken, banned, waited int) {
	if p == nil {
		return 0, 0, 0, 0
	}

	p.mu.Lock()
	defer p.mu.Unlock()
	p.expireLocked()
	return len(p.free), len(p.taken), len(p.banned), p.waited
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
