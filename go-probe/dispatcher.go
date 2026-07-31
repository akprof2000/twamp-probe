// Диспетчер зондов: очередь задач + фиксированный пул воркеров — аналог C# ProbeDispatcher.
// Одновременно выполняется не более MaxParallel зондов, сколько бы задач ни поступило.
package main

import (
	"context"
	"sync"
)

// Executor — исполнитель задачи. Диспетчеру нужен ровно один метод, поэтому он
// зависит от интерфейса, а не от конкретного ProbeRunner: так поведение очереди
// проверяется тестами без запуска настоящих процессов зондов.
type Executor interface {
	RunForNodes(ctx context.Context, task *TaskInfo)
}

// Dispatcher — очередь задач и пул воркеров с ограниченной параллельностью.
type Dispatcher struct {
	queue    chan *TaskInfo
	runner   Executor
	registry *RunRegistry
	limiter  *AdaptiveLimiter // фактический предел: сжимается, когда кончается память
	ctx      context.Context

	// Задачи, которые сейчас в очереди или выполняются. Нужен, чтобы не ставить
	// задачу повторно, пока не завершился её предыдущий запуск: замер вроде
	// «twping -c 300» живёт минутами, а расписание может сработать раньше — иначе
	// запуски копятся и съедают память.
	active sync.Map // ключ — идентификатор задачи
}

// NewDispatcher создаёт диспетчер и поднимает пул воркеров.
// queueCapacity — ёмкость очереди задач (Probe:QueueCapacity); это не та же
// величина, что лимит очереди результатов (Probe:MaxPendingResults).
func NewDispatcher(ctx context.Context, workers, queueCapacity int, runner Executor,
	registry *RunRegistry, limiter *AdaptiveLimiter) *Dispatcher {

	if queueCapacity < 1 {
		queueCapacity = 1
	}
	d := &Dispatcher{
		// Ёмкость с запасом на массовую заливку: постановка задач не блокирует приём HTTP.
		queue:    make(chan *TaskInfo, queueCapacity),
		runner:   runner,
		registry: registry,
		limiter:  limiter,
		ctx:      ctx,
	}
	for range workers { // range по числу — Go 1.22
		go d.workerLoop()
	}
	logDispatcher.Info("Пул воркеров запущен",
		"воркеров", workers, "ёмкость_очереди_задач", cap(d.queue))
	return d
}

// Enqueue ставит задачу в очередь на выполнение (не блокируя отправителя).
// Если предыдущий запуск этой задачи ещё не завершился, новый пропускается:
// накапливать параллельные замеры одной задачи бессмысленно и опасно для памяти.
func (d *Dispatcher) Enqueue(task *TaskInfo) {
	if _, busy := d.active.LoadOrStore(task.Id, struct{}{}); busy {
		logDispatcher.Warn("Предыдущий запуск ещё не завершён — задача пропущена",
			"задача", task.Id, "название", task.Title, "узел", task.EndNode)
		return
	}

	select {
	case d.queue <- task:
	default:
		d.active.Delete(task.Id) // в очередь не попала — держать пометку незачем
		logDispatcher.Error("Очередь переполнена — задача пропущена",
			"задача", task.Id, "название", task.Title, "ёмкость_очереди_задач", cap(d.queue))
	}
}

// workerLoop — рабочий цикл: берёт задачи из очереди и выполняет их.
func (d *Dispatcher) workerLoop() {
	for {
		select {
		case <-d.ctx.Done():
			return
		case task := <-d.queue:
			// Слот ограничителя: под давлением памяти часть воркеров ждёт здесь,
			// вместо того чтобы плодить процессы, которые ОС всё равно не запустит.
			if !d.limiter.Acquire(d.ctx) {
				return // служба останавливается
			}

			// Фиксируем начало и конец выполнения — это видно в TaskStatus.
			d.registry.MarkStarted(task)
			d.runner.RunForNodes(d.ctx, task)
			d.registry.MarkFinished(task.Id)

			d.limiter.Release()
			d.active.Delete(task.Id) // задача свободна — следующее срабатывание пройдёт
		}
	}
}
