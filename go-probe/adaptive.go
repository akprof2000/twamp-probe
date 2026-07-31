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

	// База для замера: сколько было свободно и сколько зондов работало на прошлой
	// проверке. Память тратят не пределы, а запущенные процессы, поэтому расход
	// считается на прирост именно работающих зондов.
	freeAtCheck uint64
	runsAtCheck int
	hasBaseline bool

	costPerRun uint64 // измеренная цена одного зонда в памяти (0 — ещё не знаем)
}

// memoryStep — что произошло с памятью и с числом зондов между двумя проверками.
type memoryStep struct {
	consumed   uint64 // сколько памяти убыло
	freeBefore uint64 // сколько было свободно на прошлой проверке
	added      int    // на сколько выросло число работающих зондов
	measured   bool   // был ли реальный прирост зондов, то есть есть ли что мерить
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

// observe сравнивает текущее состояние с прошлой проверкой.
//
// Мерить расход имеет смысл только по **реально запущенным зондам**: если за
// интервал не прибавилось ни одного процесса, изменение свободной памяти к нам
// отношения не имеет (это соседние службы, кеш ядра), и делать по нему выводы
// о пределе нельзя.
func (l *AdaptiveLimiter) observe(freeNow uint64, runsNow int) memoryStep {
	l.mu.Lock()
	defer l.mu.Unlock()

	step := memoryStep{freeBefore: l.freeAtCheck}
	if !l.hasBaseline {
		return step
	}
	step.added = runsNow - l.runsAtCheck
	if step.added <= 0 || freeNow >= l.freeAtCheck {
		return step // зондов не прибавилось (или память не убыла) — мерить нечего
	}
	step.consumed = l.freeAtCheck - freeNow
	step.measured = true
	return step
}

// rebase запоминает состояние текущей проверки как базу для следующей.
func (l *AdaptiveLimiter) rebase(freeNow uint64, runsNow int) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.freeAtCheck = freeNow
	l.runsAtCheck = runsNow
	l.hasBaseline = freeNow > 0
}

// noteCost уточняет цену одного зонда, сглаживая замер: разовый выброс
// (сборка мусора, соседняя служба) не должен ломать прогноз.
func (l *AdaptiveLimiter) noteCost(cost uint64) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if cost == 0 {
		return
	}
	if l.costPerRun == 0 {
		l.costPerRun = cost
		return
	}
	l.costPerRun = (l.costPerRun*3 + cost) / 4
}

// CostPerRun возвращает измеренную цену одного зонда (0 — ещё не измерена).
func (l *AdaptiveLimiter) CostPerRun() uint64 {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.costPerRun
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
// Всё решение строится вокруг **реально запущенных зондов**, а не вокруг самого
// числа-предела. Предел — это лишь разрешение; память тратят процессы. Поэтому:
//
//   - расход считается на прирост работающих зондов и даёт цену одного зонда;
//   - предел повышается, только когда он **выбран до конца** (зонды упираются в
//     него и ждут слот). Поднимать предел, под которым работает три задачи из
//     сотни, бессмысленно: нагрузки это не добавит, а замер расхода испортит —
//     память в этот момент меняют посторонние службы, и проба «разгонялась» бы
//     вхолостую до значения, которое при первой же настоящей пачке её и уронит;
//   - величина роста ограничена тем, что физически влезает в свободную память
//     по измеренной цене зонда.
func adjustLimit(limiter *AdaptiveLimiter, cfg MemoryGuardConfig, status MemoryStatus) {
	before := limiter.Limit()
	runs := limiter.InFlight()
	step := limiter.observe(status.AvailableBytes, runs)
	defer limiter.rebase(status.AvailableBytes, runs)

	if step.measured {
		limiter.noteCost(step.consumed / uint64(step.added))
	}

	switch {
	// 1. Аварийный порог: памяти почти нет — режем вдвое немедленно.
	case status.UsedPercent >= cfg.HighPercent:
		after := limiter.SetLimit(before / 2)
		limiter.markPressure()
		if after != before {
			logMain.Warn("Памяти мало — предел одновременных запусков снижен вдвое",
				"память_%", roundPercent(status.UsedPercent), "порог_%", cfg.HighPercent,
				"свободно", humanBytes(status.AvailableBytes),
				"было", before, "стало", after,
				"выполняется", runs, "потолок", limiter.maxLimit)
		} else if before == limiter.minLimit {
			logMain.Warn("Памяти мало, но предел уже на минимуме",
				"память_%", roundPercent(status.UsedPercent),
				"свободно", humanBytes(status.AvailableBytes), "предел", before)
		}

	// 2. Запущенные зонды съели больше половины свободной памяти — следующий раз вдвое меньше.
	//    Это и есть защита от «ещё одно удвоение — и всё упадёт».
	case step.measured && step.consumed*2 > step.freeBefore:
		after := limiter.SetLimit(before / 2)
		limiter.markPressure()
		if after != before {
			logMain.Warn("Запущенные зонды съели больше половины свободной памяти — предел снижен вдвое",
				"съедено", humanBytes(step.consumed), "было_свободно", humanBytes(step.freeBefore),
				"осталось", humanBytes(status.AvailableBytes),
				"новых_зондов", step.added, "цена_зонда", humanBytes(limiter.CostPerRun()),
				"было", before, "стало", after,
				"выполняется", runs, "потолок", limiter.maxLimit)
		}

	// 3. Памяти вдоволь — растём, но только если предел действительно выбран.
	case status.UsedPercent <= cfg.LowPercent:
		if runs < before {
			return // предел не выбран: свободные слоты есть, расти незачем
		}
		if limiter.coolingDown() {
			return // после сжатия ждём, пока освободятся процессы
		}
		target, mode := limiter.growthTarget(before)
		target, capped := affordableTarget(before, target, limiter.CostPerRun(), status.AvailableBytes)
		if target <= before {
			return // по измеренной цене зонда прибавка не влезает в свободную память
		}
		after := limiter.SetLimit(target)
		if after != before {
			logMain.Info("Предел выбран, памяти достаточно — предел одновременных запусков повышен",
				"режим", mode, "ограничен_памятью", capped,
				"память_%", roundPercent(status.UsedPercent),
				"свободно", humanBytes(status.AvailableBytes),
				"цена_зонда", humanBytes(limiter.CostPerRun()),
				"было", before, "стало", after,
				"выполняется", runs, "потолок", limiter.maxLimit)
		}
	}
	// Между порогами и при умеренном расходе предел держим — иначе он дёргался бы.
}

// affordableTarget урезает желаемый предел до того, что влезает в свободную память.
//
// Тратим на прибавку не больше половины свободного: вторая половина — запас на
// сам замер (память освобождается с задержкой) и на соседние службы. Пока цена
// зонда не измерена, доверяем желаемому значению — иначе проба не сдвинется
// с места и мерить будет нечего.
func affordableTarget(before, wanted int, costPerRun, freeBytes uint64) (int, bool) {
	if costPerRun == 0 || wanted <= before {
		return wanted, false
	}
	room := int(freeBytes / 2 / costPerRun)
	if before+room >= wanted {
		return wanted, false
	}
	return before + room, true
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
