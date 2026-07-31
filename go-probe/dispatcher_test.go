package main

import (
	"context"
	"testing"
	"time"
)

// blockingRunner — исполнитель, который держит задачу, пока тест его не отпустит.
type blockingRunner struct {
	started chan string
	release chan struct{}
}

func (r *blockingRunner) RunForNodes(ctx context.Context, task *TaskInfo) {
	r.started <- task.Id
	<-r.release
}

// newTestDispatcher собирает диспетчер с заглушкой исполнителя.
func newTestDispatcher(ctx context.Context, runner Executor, workers int) *Dispatcher {
	limiter := NewAdaptiveLimiter(workers, 1, workers)
	return NewDispatcher(ctx, workers, runner, NewRunRegistry(), limiter)
}

func TestDispatcher_SkipsTaskWhilePreviousRunIsAlive(t *testing.T) {
	// Замер «twping -c 300» живёт минутами, а расписание может сработать раньше.
	// Повторный запуск той же задачи обязан пропускаться: копить параллельные
	// замеры одной задачи бессмысленно и опасно для памяти.
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	runner := &blockingRunner{started: make(chan string, 4), release: make(chan struct{})}
	d := newTestDispatcher(ctx, runner, 4)

	task := &TaskInfo{Id: "aaaa-1", Title: "долгий замер", EndNode: "10.0.0.1", Circles: 1, Repeats: 1}

	d.Enqueue(task)
	select {
	case <-runner.started:
	case <-time.After(2 * time.Second):
		t.Fatal("первая постановка не дошла до исполнителя")
	}

	// Пока первый запуск держится, ставим ту же задачу ещё дважды.
	d.Enqueue(task)
	d.Enqueue(task)

	select {
	case id := <-runner.started:
		t.Fatalf("задача %s запущена повторно, хотя предыдущий запуск не завершён", id)
	case <-time.After(300 * time.Millisecond):
		// ожидаемо: повторные постановки пропущены
	}

	close(runner.release) // отпускаем первый запуск
}

func TestDispatcher_RunsTaskAgainAfterPreviousFinished(t *testing.T) {
	// Пропуск не должен «залипать»: как только запуск завершился,
	// следующее срабатывание расписания обязано пройти.
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	runner := &blockingRunner{started: make(chan string, 4), release: make(chan struct{})}
	d := newTestDispatcher(ctx, runner, 4)
	task := &TaskInfo{Id: "bbbb-2", Title: "замер", EndNode: "10.0.0.2", Circles: 1, Repeats: 1}

	d.Enqueue(task)
	<-runner.started
	close(runner.release) // первый запуск завершается

	// Ждём, пока диспетчер снимет пометку занятости.
	deadline := time.Now().Add(2 * time.Second)
	for {
		if _, busy := d.active.Load(task.Id); !busy {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("пометка занятости не снята после завершения запуска")
		}
		time.Sleep(10 * time.Millisecond)
	}

	runner.release = make(chan struct{})
	d.Enqueue(task)
	select {
	case <-runner.started:
	case <-time.After(2 * time.Second):
		t.Fatal("после завершения задача не запустилась повторно")
	}
	close(runner.release)
}

func TestDispatcher_DifferentTasksRunInParallel(t *testing.T) {
	// Пропуск действует только на одну и ту же задачу — разные идут параллельно.
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	runner := &blockingRunner{started: make(chan string, 4), release: make(chan struct{})}
	d := newTestDispatcher(ctx, runner, 4)

	d.Enqueue(&TaskInfo{Id: "cccc-3", Title: "первая", EndNode: "10.0.0.3", Circles: 1, Repeats: 1})
	d.Enqueue(&TaskInfo{Id: "dddd-4", Title: "вторая", EndNode: "10.0.0.4", Circles: 1, Repeats: 1})

	seen := map[string]bool{}
	for range 2 {
		select {
		case id := <-runner.started:
			seen[id] = true
		case <-time.After(2 * time.Second):
			t.Fatalf("запустилось только %d задач из 2", len(seen))
		}
	}
	if !seen["cccc-3"] || !seen["dddd-4"] {
		t.Errorf("запустились не те задачи: %v", seen)
	}
	close(runner.release)
}
