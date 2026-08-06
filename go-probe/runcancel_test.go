package main

import (
	"context"
	"runtime"
	"strings"
	"testing"
	"time"
)

// longRunningProbe возвращает аргументы ping, при которых зонд заведомо живёт
// дольше теста — имитация длинного замера (twping -c 300 живёт минутами).
func longRunningProbe() (string, string) {
	if runtime.GOOS == "windows" {
		return "ping", "-n 30"
	}
	return "ping", "-c 30 -i 1"
}

// newTestRunner собирает исполнитель с временным хранилищем результатов.
func newTestRunner(t *testing.T, exec, args string) (*ProbeRunner, *ResultStore, *RunCancelRegistry) {
	t.Helper()
	cfg := &Config{
		MaxPendingResults: 1000,
		Ping:              ProbeToolConfig{Name: exec, Default: args},
	}
	results := NewResultStore(cfg.MaxPendingResults, 60)
	cancels := NewRunCancelRegistry()
	return NewProbeRunner(cfg, results, NewRunRegistry(), cancels, nil), results, cancels
}

// probeTask — задача, выполняющая заведомо долгий зонд без таймаута.
func probeTask(id string) *TaskInfo {
	return &TaskInfo{
		Id: id, Title: "длинный замер", Mode: ModeWinPing,
		EndNode: "127.0.0.1", Circles: 1, Repeats: 1, TimeoutSec: 0,
		Parameters: map[string]string{},
	}
}

func TestCancelTask_KillsRunningProbe(t *testing.T) {
	exec, args := longRunningProbe()
	runner, results, cancels := newTestRunner(t, exec, args)
	task := probeTask("11111111-1111-1111-1111-111111111111")

	done := make(chan struct{})
	go func() {
		defer close(done)
		runner.RunForNodes(context.Background(), task)
	}()

	// Дожидаемся, пока запуск действительно начнётся.
	deadline := time.Now().Add(5 * time.Second)
	for cancels.ActiveRuns(task.Id) == 0 {
		if time.Now().After(deadline) {
			t.Fatal("зонд так и не запустился")
		}
		time.Sleep(20 * time.Millisecond)
	}

	// Обрываем задачу — процесс обязан быть убит, а не домерять свои 30 секунд.
	start := time.Now()
	if stopped := cancels.CancelTask(task.Id); stopped != 1 {
		t.Fatalf("оборвано запусков: %d, ожидался 1", stopped)
	}

	select {
	case <-done:
		if elapsed := time.Since(start); elapsed > 5*time.Second {
			t.Errorf("процесс убит за %v — слишком долго", elapsed)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("процесс зонда не был убит после отмены задачи")
	}

	// Результата по удалённой задаче быть не должно.
	batch := results.TakeBatch(200 * time.Millisecond)
	if len(batch.Items) != 0 {
		t.Errorf("по оборванной задаче отправлено %d результатов, ожидалось 0", len(batch.Items))
	}
}

func TestExecute_CancelledBeforeProcessStarted(t *testing.T) {
	// Отмена может прийти в узкое окно между регистрацией запуска и самим
	// стартом процесса: exec тогда отказывается запускать зонд по отменённому
	// контексту, и запуск падает с ошибкой, хотя зонд ни при чём.
	//
	// Раньше это оформлялось как «зонд не запустился»: по удалённой задаче
	// уходил выдуманный результат, а в журнал — ERROR. На боевой пробе окно
	// узкое, но при тысячах замеров в него попадают регулярно; в тестах ошибка
	// всплывала примерно в одном полном прогоне из четырёх. Здесь оно
	// воспроизводится точно: контекст отменён ещё до вызова.
	exec, args := longRunningProbe()
	runner, _, _ := newTestRunner(t, exec, args)
	task := probeTask("22222222-2222-2222-2222-222222222222")

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	execName, cmdArgs, env := runner.buildCommand(task, "127.0.0.1")
	result := runner.execute(ctx, task, "127.0.0.1", execName, cmdArgs, env)

	if !result.Cancelled {
		t.Error("прерванный до старта замер не помечен отменённым — результат уйдёт серверу")
	}
	if result.Outcome == string(OutcomeStartFailed) {
		t.Errorf("отмена оформлена как поломка зонда: %s", result.ErrorConsole)
	}
}

func TestCancelAll_KillsEveryRunningProbe(t *testing.T) {
	exec, args := longRunningProbe()
	runner, _, cancels := newTestRunner(t, exec, args)

	tasks := []*TaskInfo{probeTask("aaaaaaaa-0000-0000-0000-000000000001"), probeTask("bbbbbbbb-0000-0000-0000-000000000002")}
	done := make(chan struct{}, len(tasks))
	for _, task := range tasks {
		go func() {
			defer func() { done <- struct{}{} }()
			runner.RunForNodes(context.Background(), task)
		}()
	}

	deadline := time.Now().Add(5 * time.Second)
	for cancels.ActiveRuns(tasks[0].Id)+cancels.ActiveRuns(tasks[1].Id) < 2 {
		if time.Now().After(deadline) {
			t.Fatal("зонды так и не запустились")
		}
		time.Sleep(20 * time.Millisecond)
	}

	if stopped := cancels.CancelAll(); stopped != 2 {
		t.Fatalf("оборвано запусков: %d, ожидалось 2", stopped)
	}
	for range tasks {
		select {
		case <-done:
		case <-time.After(10 * time.Second):
			t.Fatal("не все процессы зондов были убиты")
		}
	}
}

func TestCancelTask_UntracksFinishedRuns(t *testing.T) {
	// Быстрый зонд: после завершения запись обязана исчезнуть из реестра,
	// иначе он будет расти и отмена начнёт трогать давно завершённые запуски.
	quick := "-n 1"
	if runtime.GOOS != "windows" {
		quick = "-c 1"
	}
	runner, _, cancels := newTestRunner(t, "ping", quick)
	task := probeTask("cccccccc-0000-0000-0000-000000000003")

	runner.RunForNodes(context.Background(), task)

	if active := cancels.ActiveRuns(task.Id); active != 0 {
		t.Errorf("после завершения осталось активных запусков: %d", active)
	}
	if stopped := cancels.CancelTask(task.Id); stopped != 0 {
		t.Errorf("отмена завершённой задачи вернула %d, ожидалось 0", stopped)
	}
}

func TestDeletedTask_CancelsRunThroughRegistry(t *testing.T) {
	// Сквозная проверка: сервер прислал задачу с Delete=true — реестр обязан
	// не только снять расписание, но и оборвать выполняющийся зонд.
	exec, args := longRunningProbe()
	runner, _, cancels := newTestRunner(t, exec, args)
	registry := NewTaskRegistry(&fakeEnqueuer{}, NewRunRegistry(), cancels)

	task := probeTask("dddddddd-0000-0000-0000-000000000004")
	done := make(chan struct{})
	go func() {
		defer close(done)
		runner.RunForNodes(context.Background(), task)
	}()

	deadline := time.Now().Add(5 * time.Second)
	for cancels.ActiveRuns(task.Id) == 0 {
		if time.Now().After(deadline) {
			t.Fatal("зонд так и не запустился")
		}
		time.Sleep(20 * time.Millisecond)
	}

	deleted := *task
	deleted.Delete = true
	deleted.Type = TypeScheduler
	registry.MergeJobs([]TaskInfo{deleted})

	select {
	case <-done:
	case <-time.After(10 * time.Second):
		t.Fatal("удаление задачи не оборвало выполняющийся зонд")
	}
}

// Страховка самого теста: зонд действительно собирается из конфигурации,
// адрес узла идёт первым аргументом.
func TestTestRunnerUsesConfiguredProbe(t *testing.T) {
	exec, args := longRunningProbe()
	runner, _, _ := newTestRunner(t, exec, args)
	name, built, _ := runner.buildCommand(probeTask("id"), "127.0.0.1")
	line := strings.Join(built, " ")
	if name != exec || !strings.HasPrefix(line, "127.0.0.1") || !strings.Contains(line, strings.Fields(args)[0]) {
		t.Errorf("команда собрана неверно: %s %v", name, built)
	}
}
