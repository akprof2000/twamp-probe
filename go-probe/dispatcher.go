// Диспетчер зондов: очередь задач + фиксированный пул воркеров — аналог C# ProbeDispatcher.
// Одновременно выполняется не более MaxParallel зондов, сколько бы задач ни поступило.
package main

import (
	"context"
	"fmt"
	"runtime/debug"
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

// runTask выполняет одну задачу и освобождает всё, что она заняла.
//
// Паника внутри зонда не должна уносить с собой службу. Замер идёт в общем
// процессе — и во встроенном режиме это код измерительной библиотеки, и в
// внешнем это разбор чужого вывода; ошибка в любом из них убила бы пробу
// целиком вместе с недоставленными результатами и всеми остальными задачами.
// Поэтому паника здесь превращается в запись в журнале: одна задача пропала,
// остальные продолжают работать.
func (d *Dispatcher) runTask(task *TaskInfo) {
	defer func() {
		if reason := recover(); reason != nil {
			logDispatcher.Error("Задача упала с паникой — остальные продолжают работу",
				"задача", task.Id, "название", task.Title, "узел", task.EndNode,
				"режим", task.Mode, "причина", reason,
				"стек", string(debug.Stack()))
			d.registry.ReportOutcome(task.Id, OutcomeExitCodeError, nil,
				fmt.Sprintf("Внутренняя ошибка зонда: %v", reason))
		}

		// Освобождение — в defer: иначе паника оставила бы занятый слот
		// ограничителя и вечную пометку «задача выполняется».
		d.registry.MarkFinished(task.Id)
		d.limiter.Release()
		d.active.Delete(task.Id) // задача свободна — следующее срабатывание пройдёт
	}()

	// Фиксируем начало и конец выполнения — это видно в TaskStatus.
	d.registry.MarkStarted(task)
	d.runner.RunForNodes(d.ctx, task)
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
			d.runTask(task)
		}
	}
}
