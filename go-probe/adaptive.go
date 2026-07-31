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
	"fmt"
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
	// старт»); после первого упора в память рост становится осторожным.
	hitPressure bool
	cooldown    int // сколько проверок не растём после сжатия — память освобождается не сразу

	// Сколько памяти было свободно, когда предел меняли в прошлый раз. По разнице
	// видно, во сколько обошёлся этот шаг, — на этом и строится решение о следующем.
	freeAtChange uint64
	hasBaseline  bool
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

// SetLimit меняет текущий предел (в границах пола и потолка) и запоминает,
// сколько памяти было свободно в этот момент — чтобы на следующей проверке
// увидеть, во сколько обошёлся шаг. Возвращает установленное значение.
func (l *AdaptiveLimiter) SetLimit(value int, freeBytes uint64) int {
	l.mu.Lock()
	defer l.mu.Unlock()

	l.limit = min(max(value, l.minLimit), l.maxLimit)
	l.freeAtChange = freeBytes
	l.hasBaseline = freeBytes > 0
	l.cond.Broadcast() // предел мог вырасти — будим ожидающих
	return l.limit
}

// consumption сообщает, сколько памяти ушло с прошлого изменения предела:
// (съедено, было свободно, есть ли с чем сравнивать).
func (l *AdaptiveLimiter) consumption(freeNow uint64) (uint64, uint64, bool) {
	l.mu.Lock()
	defer l.mu.Unlock()

	if !l.hasBaseline || freeNow >= l.freeAtChange {
		return 0, l.freeAtChange, false // памяти не убавилось — считать нечего
	}
	return l.freeAtChange - freeNow, l.freeAtChange, true
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
// До первого упора в память разгоняемся удвоением; после — прибавляем четверть,
// чтобы не прыгнуть обратно в тот же потолок памяти.
func (l *AdaptiveLimiter) growthTarget(current int) (int, string) {
	l.mu.Lock()
	defer l.mu.Unlock()

	if l.hitPressure {
		return current + max(current/4, 1), "плавный"
	}
	return current * 2, "разгон"
}

// pressureCooldownTicks — сколько проверок памяти пропустить после сжатия предела.
const pressureCooldownTicks = 2

// MemoryGuardConfig — пороги подстройки предела по занятости памяти.
type MemoryGuardConfig struct {
	HighPercent float64       // выше этой занятости — аварийное сжатие
	LowPercent  float64       // ниже этой занятости — можно разгоняться
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
		"предел", limiter.Limit(), "минимум", limiter.minLimit, "потолок", limiter.maxLimit)

	ticker := time.NewTicker(cfg.Interval)
	defer ticker.Stop()

	failures := 0
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			status, err := memoryStatus()
			if err != nil {
				// Сообщаем один раз: если память не читается, предел просто не трогаем.
				if failures == 0 {
					logMain.Warn("Не удалось прочитать состояние памяти — предел запусков не подстраивается",
						"ошибка", err)
				}
				failures++
				continue
			}
			failures = 0
			adjustLimit(limiter, cfg, status)
		}
	}
}

// adjustLimit подстраивает предел под состояние памяти и пишет в журнал при изменении.
//
// Решение принимается не по одной лишь занятости, а по тому, **сколько памяти съел
// предыдущий шаг**. Пороги занятости срабатывают слишком поздно: при 64 ГБ всего и
// 16 ГБ свободных занято лишь 75% — формально «есть запас», а следующее удвоение
// уже не влезет. Поэтому: если прошлый шаг съел больше половины того, что было
// свободно, — предел уменьшается вдвое, не дожидаясь порога.
func adjustLimit(limiter *AdaptiveLimiter, cfg MemoryGuardConfig, status MemoryStatus) {
	before := limiter.Limit()
	consumed, freeBefore, measured := limiter.consumption(status.AvailableBytes)

	switch {
	// 1. Аварийный порог: памяти почти нет — режем вдвое немедленно.
	case status.UsedPercent >= cfg.HighPercent:
		after := limiter.SetLimit(before/2, status.AvailableBytes)
		limiter.markPressure()
		if after != before {
			logMain.Warn("Памяти мало — предел одновременных запусков снижен вдвое",
				"память_%", roundPercent(status.UsedPercent), "порог_%", cfg.HighPercent,
				"свободно", humanBytes(status.AvailableBytes),
				"было", before, "стало", after,
				"выполняется", limiter.InFlight(), "потолок", limiter.maxLimit)
		} else if before == limiter.minLimit {
			logMain.Warn("Памяти мало, но предел уже на минимуме",
				"память_%", roundPercent(status.UsedPercent),
				"свободно", humanBytes(status.AvailableBytes), "предел", before)
		}

	// 2. Прошлый шаг съел больше половины свободной памяти — следующий раз вдвое меньше.
	//    Это и есть защита от «ещё одно удвоение — и всё упадёт».
	case measured && consumed*2 > freeBefore:
		after := limiter.SetLimit(before/2, status.AvailableBytes)
		limiter.markPressure()
		if after != before {
			logMain.Warn("Прошлый шаг съел больше половины свободной памяти — предел снижен вдвое",
				"съедено", humanBytes(consumed), "было_свободно", humanBytes(freeBefore),
				"осталось", humanBytes(status.AvailableBytes),
				"было", before, "стало", after,
				"выполняется", limiter.InFlight(), "потолок", limiter.maxLimit)
		}

	// 3. Памяти вдоволь и прошлый шаг обошёлся дёшево — наращиваем.
	case status.UsedPercent <= cfg.LowPercent:
		if limiter.coolingDown() {
			return // после сжатия ждём, пока освободятся процессы
		}
		target, mode := limiter.growthTarget(before)
		after := limiter.SetLimit(target, status.AvailableBytes)
		if after != before {
			logMain.Info("Памяти достаточно — предел одновременных запусков повышен",
				"режим", mode, "память_%", roundPercent(status.UsedPercent),
				"свободно", humanBytes(status.AvailableBytes),
				"съедено_прошлым_шагом", humanBytes(consumed),
				"было", before, "стало", after,
				"выполняется", limiter.InFlight(), "потолок", limiter.maxLimit)
		}
	}
	// Между порогами и при умеренном расходе предел держим — иначе он дёргался бы.
}

// roundPercent округляет процент до одного знака — журнал не должен пестреть хвостами.
func roundPercent(value float64) float64 {
	return float64(int(value*10+0.5)) / 10
}

// humanBytes переводит байты в удобные для чтения единицы.
func humanBytes(value uint64) string {
	const unit = 1024
	if value < unit {
		return fmt.Sprintf("%d Б", value)
	}
	div, exp := uint64(unit), 0
	for n := value / unit; n >= unit && exp < 3; n /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.1f %s", float64(value)/float64(div), [...]string{"КБ", "МБ", "ГБ", "ТБ"}[exp])
}
