// Диспетчер зондов: очередь задач + фиксированный пул воркеров — аналог C# ProbeDispatcher.
// Одновременно выполняется не более MaxParallel зондов, сколько бы задач ни поступило.
package main

import (
	"context"
)

// Dispatcher — очередь задач и пул воркеров с ограниченной параллельностью.
type Dispatcher struct {
	queue    chan *TaskInfo
	runner   *ProbeRunner
	registry *RunRegistry
	limiter  *AdaptiveLimiter // фактический предел: сжимается, когда кончается память
	ctx      context.Context
}

// NewDispatcher создаёт диспетчер и поднимает пул воркеров.
func NewDispatcher(ctx context.Context, workers int, runner *ProbeRunner, registry *RunRegistry,
	limiter *AdaptiveLimiter) *Dispatcher {

	d := &Dispatcher{
		// Ёмкость с запасом на массовую заливку: постановка задач не блокирует приём HTTP.
		queue:    make(chan *TaskInfo, 100_000),
		runner:   runner,
		registry: registry,
		limiter:  limiter,
		ctx:      ctx,
	}
	for range workers { // range по числу — Go 1.22
		go d.workerLoop()
	}
	logDispatcher.Info("Пул воркеров запущен", "воркеров", workers, "ёмкость_очереди", cap(d.queue))
	return d
}

// Enqueue ставит задачу в очередь на выполнение (не блокируя отправителя).
func (d *Dispatcher) Enqueue(task *TaskInfo) {
	select {
	case d.queue <- task:
	default:
		logDispatcher.Error("Очередь переполнена — задача пропущена",
			"задача", task.Id, "название", task.Title, "ёмкость", cap(d.queue))
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
		}
	}
}
