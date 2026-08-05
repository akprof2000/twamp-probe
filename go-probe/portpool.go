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
// зонд сообщит об этом, и пул исключит порт из оборота — см. Blacklist.
package main

import (
	"context"
	"fmt"
	"math/rand"
	"strconv"
	"strings"
	"sync"
)

// PortPool раздаёт зондам локальные порты и следит, чтобы два замера не взяли
// один и тот же. Нулевой указатель означает «пул выключен» — порт выбирает ядро.
type PortPool struct {
	mu     sync.Mutex
	cond   *sync.Cond
	free   []int
	taken  map[int]bool
	banned map[int]bool // порты, занятые кем-то посторонним
	from   int
	to     int
	closed bool

	waited int // сколько раз замеры ждали свободного порта — для журнала
}

// NewPortPool создаёт пул портов из диапазона [from, to].
func NewPortPool(from, to int) (*PortPool, error) {
	if from < 1 || to > 65535 || from > to {
		return nil, fmt.Errorf("недопустимый диапазон портов %d-%d", from, to)
	}

	p := &PortPool{
		free:   make([]int, 0, to-from+1),
		taken:  make(map[int]bool, to-from+1),
		banned: map[int]bool{},
		from:   from,
		to:     to,
	}
	for port := from; port <= to; port++ {
		p.free = append(p.free, port)
	}
	// Перемешиваем: подряд идущие порты у соседних замеров дают всплески
	// в таблицах трансляции сетевого оборудования.
	rand.Shuffle(len(p.free), func(i, j int) { p.free[i], p.free[j] = p.free[j], p.free[i] })

	p.cond = sync.NewCond(&p.mu)
	return p, nil
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
	for len(p.free) == 0 && !p.closed && ctx.Err() == nil {
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
		if !p.banned[port] {
			p.free = append(p.free, port)
		}
	}
	p.mu.Unlock()
	p.cond.Signal()
}

// Blacklist исключает порт из оборота: его занял кто-то посторонний, и
// возвращать его в пул незачем — следующий замер налетел бы на ту же ошибку.
func (p *PortPool) Blacklist(port int) {
	if p == nil || port == 0 {
		return
	}

	p.mu.Lock()
	p.banned[port] = true
	delete(p.taken, port)
	p.mu.Unlock()
	p.cond.Signal()
}

// Stats сообщает состояние пула: свободно, занято, исключено, сколько раз ждали.
func (p *PortPool) Stats() (free, taken, banned, waited int) {
	if p == nil {
		return 0, 0, 0, 0
	}

	p.mu.Lock()
	defer p.mu.Unlock()
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
	p.closed = true
	p.mu.Unlock()
	p.cond.Broadcast()
}
