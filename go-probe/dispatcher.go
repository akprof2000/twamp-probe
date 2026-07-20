// Диспетчер зондов: очередь задач + фиксированный пул воркеров — аналог C# ProbeDispatcher.
// Одновременно выполняется не более MaxParallel зондов, сколько бы задач ни поступило.
package main

import (
	"context"
	"log"
)

// Dispatcher — очередь задач и пул воркеров с ограниченной параллельностью.
type Dispatcher struct {
	queue    chan *TaskInfo
	runner   *ProbeRunner
	registry *RunRegistry
	ctx      context.Context
}

// NewDispatcher создаёт диспетчер и поднимает пул воркеров.
func NewDispatcher(ctx context.Context, workers int, runner *ProbeRunner, registry *RunRegistry) *Dispatcher {
	d := &Dispatcher{
		// Ёмкость с запасом на массовую заливку: постановка задач не блокирует приём HTTP.
		queue:    make(chan *TaskInfo, 100_000),
		runner:   runner,
		registry: registry,
		ctx:      ctx,
	}
	for range workers { // range по числу — Go 1.22
		go d.workerLoop()
	}
	log.Printf("Запущено %d воркеров обработки зондов", workers)
	return d
}

// Enqueue ставит задачу в очередь на выполнение (не блокируя отправителя).
func (d *Dispatcher) Enqueue(task *TaskInfo) {
	select {
	case d.queue <- task:
	default:
		log.Printf("Очередь диспетчера переполнена — задача %s пропущена", task.Id)
	}
}

// workerLoop — рабочий цикл: берёт задачи из очереди и выполняет их.
func (d *Dispatcher) workerLoop() {
	for {
		select {
		case <-d.ctx.Done():
			return
		case task := <-d.queue:
			// Фиксируем начало и конец выполнения — это видно в TaskStatus.
			d.registry.MarkStarted(task)
			d.runner.RunForNodes(d.ctx, task)
			d.registry.MarkFinished(task.Id)
		}
	}
}
