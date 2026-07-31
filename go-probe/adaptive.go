// Адаптивный ограничитель одновременных запусков зондов.
//
// Зачем: настройка Probe:MaxParallel задаёт, сколько зондов можно запустить
// одновременно, но реальный предел — свободная память. Каждый зонд это отдельный
// процесс (python3 -m twampy занимает 15–25 МБ), и на пике из тысяч задач система
// упирается в память: ядро отказывает в fork с «cannot allocate memory», замеры
// пропадают.
//
// Решение: лимит плавает. Память кончается — сжимаем, память освободилась —
// возвращаем обратно, но никогда не выше настроенного MaxParallel. Сжимаем резко,
// а восстанавливаем плавно (как управление окном в TCP): лучше недобрать
// параллельности, чем снова упереться в потолок памяти.
package main

import (
	"context"
	"sync"
	"time"
)

// AdaptiveLimiter — ограничитель с изменяемым пределом одновременных запусков.
type AdaptiveLimiter struct {
	mu       sync.Mutex
	cond     *sync.Cond
	limit    int  // текущий предел
	inFlight int  // сколько запусков идёт прямо сейчас
	maxLimit int  // потолок из настроек — выше не поднимаемся никогда
	minLimit int  // пол: даже под давлением памяти замеры должны идти
	closed   bool // служба останавливается — ожидающих больше не держим

	// Состояние разгона. Пока памяти хватает, предел растёт удвоением («медленный
	// старт»); после первого упора в память рост становится осторожным, линейным.
	hitPressure bool
	cooldown    int // сколько проверок не растём после сжатия — память освобождается не сразу
}

// NewAdaptiveLimiter создаёт ограничитель, начинающий с малого предела.
//
// Стартовать с полного потолка нельзя: тысячи задач по расписанию срываются
// одновременно и успевают запустить все процессы раньше, чем сработает первая
// проверка памяти, — система отказывает в fork, и замеры пропадают. Поэтому
// начинаем с startLimit и наращиваем, пока память позволяет.
func NewAdaptiveLimiter(maxLimit, minLimit, startLimit int) *AdaptiveLimiter {
	if minLimit < 1 {
		minLimit = 1
	}
	if maxLimit < minLimit {
		maxLimit = minLimit
	}
	start := min(max(startLimit, minLimit), maxLimit)

	l := &AdaptiveLimiter{limit: start, maxLimit: maxLimit, minLimit: minLimit}
	l.cond = sync.NewCond(&l.mu)
	return l
}

// Acquire занимает слот, ожидая освобождения, если предел исчерпан.
// Возвращает false, если ждать больше нет смысла (служба останавливается).
func (l *AdaptiveLimiter) Acquire(ctx context.Context) bool {
	l.mu.Lock()
	defer l.mu.Unlock()

	for l.inFlight >= l.limit && !l.closed && ctx.Err() == nil {
		l.cond.Wait()
	}
	if l.closed || ctx.Err() != nil {
		return false
	}
	l.inFlight++
	return true
}

// Release освобождает слот.
func (l *AdaptiveLimiter) Release() {
	l.mu.Lock()
	if l.inFlight > 0 {
		l.inFlight--
	}
	l.mu.Unlock()
	l.cond.Signal()
}

// SetLimit меняет текущий предел (в границах пола и потолка).
// Возвращает установленное значение.
func (l *AdaptiveLimiter) SetLimit(value int) int {
	l.mu.Lock()
	defer l.mu.Unlock()

	l.limit = min(max(value, l.minLimit), l.maxLimit)
	l.cond.Broadcast() // предел мог вырасти — будим ожидающих
	return l.limit
}

// Limit возвращает текущий предел.
func (l *AdaptiveLimiter) Limit() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.limit
}

// InFlight возвращает число выполняющихся прямо сейчас запусков.
func (l *AdaptiveLimiter) InFlight() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.inFlight
}

// Close отпускает всех ожидающих (остановка службы).
func (l *AdaptiveLimiter) Close() {
	l.mu.Lock()
	l.closed = true
	l.mu.Unlock()
	l.cond.Broadcast()
}

// markPressure запоминает, что мы упёрлись в память: дальше растём осторожно,
// и первые проверки после сжатия пропускаем — память освобождается не сразу.
func (l *AdaptiveLimiter) markPressure() {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.hitPressure = true
	l.cooldown = pressureCooldownTicks
}

// coolingDown сообщает, идёт ли пауза после сжатия (и списывает одну проверку).
func (l *AdaptiveLimiter) coolingDown() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.cooldown <= 0 {
		return false
	}
	l.cooldown--
	return true
}

// growthTarget возвращает следующий предел и название режима роста.
// До первого упора в память разгоняемся удвоением, после — прибавляем по шагу.
func (l *AdaptiveLimiter) growthTarget(current, step int) (int, string) {
	l.mu.Lock()
	defer l.mu.Unlock()

	if l.hitPressure {
		return current + step, "плавный"
	}
	return current * 2, "разгон"
}

// pressureCooldownTicks — сколько проверок памяти пропустить после сжатия предела.
const pressureCooldownTicks = 2

// MemoryGuardConfig — пороги подстройки предела по занятости памяти.
type MemoryGuardConfig struct {
	HighPercent float64       // выше этого — сжимаем предел
	LowPercent  float64       // ниже этого — возвращаем предел
	Interval    time.Duration // как часто смотреть на память
}

// RunMemoryGuard следит за памятью и подстраивает предел запусков.
// Работает до отмены контекста; каждое изменение предела попадает в журнал.
func RunMemoryGuard(ctx context.Context, limiter *AdaptiveLimiter, cfg MemoryGuardConfig) {
	if cfg.Interval <= 0 {
		logMain.Info("Слежение за памятью выключено (Probe:MemoryCheckSec = 0)")
		return
	}

	logMain.Info("Слежение за памятью запущено",
		"порог_сжатия_%", cfg.HighPercent, "порог_роста_%", cfg.LowPercent,
		"период_с", int(cfg.Interval.Seconds()),
		"предел", limiter.Limit(), "минимум", limiter.minLimit)

	// Шаг роста — десятая часть потолка: возвращаемся плавно, за ~10 проверок.
	step := max(limiter.maxLimit/10, 1)
	ticker := time.NewTicker(cfg.Interval)
	defer ticker.Stop()

	failures := 0
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			used, err := memoryUsedPercent()
			if err != nil {
				// Сообщаем один раз: если память не читается, лимит просто не трогаем.
				if failures == 0 {
					logMain.Warn("Не удалось прочитать занятость памяти — предел запусков не подстраивается",
						"ошибка", err)
				}
				failures++
				continue
			}
			failures = 0
			adjustLimit(limiter, cfg, used, step)
		}
	}
}

// adjustLimit подстраивает предел под текущую занятость памяти и пишет в журнал,
// если предел изменился. Вынесено отдельно — так поведение проверяется тестами.
//
// Схема — «медленный старт» с последующим осторожным ростом (как управление окном
// в TCP): пока памяти вдоволь и упора ещё не было, предел удваивается; после первого
// упора в память рост идёт шагами, а сжатие всегда резкое. Так проба нащупывает
// безопасную величину, вместо того чтобы разом запустить тысячи процессов.
func adjustLimit(limiter *AdaptiveLimiter, cfg MemoryGuardConfig, used float64, step int) {
	before := limiter.Limit()

	switch {
	case used >= cfg.HighPercent:
		// Памяти нет: режем предел на четверть, чтобы освободить её быстрее,
		// чем система начнёт отказывать в запуске процессов.
		after := limiter.SetLimit(before * 3 / 4)
		limiter.markPressure()

		if after != before {
			logMain.Warn("Памяти мало — предел одновременных запусков снижен",
				"память_%", roundPercent(used), "порог_%", cfg.HighPercent,
				"было", before, "стало", after,
				"выполняется", limiter.InFlight(), "потолок", limiter.maxLimit)
		} else if before == limiter.minLimit {
			logMain.Warn("Памяти мало, но предел уже на минимуме",
				"память_%", roundPercent(used), "предел", before)
		}

	case used <= cfg.LowPercent:
		// Память свободна. Но сразу после сжатия не растём: процессы завершаются
		// не мгновенно, и память освобождается с задержкой — иначе начнём качаться.
		if limiter.coolingDown() {
			return
		}

		next, phase := limiter.growthTarget(before, step)
		after := limiter.SetLimit(next)
		if after != before {
			logMain.Info("Памяти достаточно — предел одновременных запусков повышен",
				"память_%", roundPercent(used), "порог_%", cfg.LowPercent,
				"было", before, "стало", after, "режим", phase,
				"выполняется", limiter.InFlight(), "потолок", limiter.maxLimit)
		}
	}
	// Между порогами предел не трогаем — иначе он дёргался бы туда-сюда.
}

// roundPercent округляет процент до одного знака — журнал не должен пестреть хвостами.
func roundPercent(value float64) float64 {
	return float64(int(value*10+0.5)) / 10
}
