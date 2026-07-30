// Реестр активных запусков зондов: позволяет оборвать выполнение задачи прямо
// в процессе, а не ждать её естественного завершения.
//
// Зачем: задача вроде «twping -c 300 -i 1» живёт минутами. Если её удалили на
// сервере, недостаточно снять расписание — уже запущенный процесс зонда надо
// завершить принудительно, иначе он продолжит мерить и занимать ресурсы ещё
// несколько минут после удаления.
//
// Механика: на каждый запуск заводится свой контекст; его функция отмены
// хранится здесь под идентификатором задачи. Отмена контекста через
// exec.CommandContext убивает процесс, а configureProcessGroup — всё дерево.
package main

import (
	"context"
	"sync"
)

// RunCancelRegistry — активные запуски зондов по задачам.
type RunCancelRegistry struct {
	mu     sync.Mutex
	nextID uint64
	// задача → её активные запуски (идентификатор запуска → отмена)
	active map[string]map[uint64]context.CancelFunc
}

// NewRunCancelRegistry создаёт пустой реестр.
func NewRunCancelRegistry() *RunCancelRegistry {
	return &RunCancelRegistry{active: map[string]map[uint64]context.CancelFunc{}}
}

// Track регистрирует запуск задачи и возвращает функцию снятия с учёта
// (её вызывает исполнитель по завершении запуска, обычно через defer).
func (r *RunCancelRegistry) Track(taskId string, cancel context.CancelFunc) func() {
	r.mu.Lock()
	defer r.mu.Unlock()

	r.nextID++
	id := r.nextID
	runs, ok := r.active[taskId]
	if !ok {
		runs = map[uint64]context.CancelFunc{}
		r.active[taskId] = runs
	}
	runs[id] = cancel

	return func() {
		r.mu.Lock()
		defer r.mu.Unlock()
		if runs, ok := r.active[taskId]; ok {
			delete(runs, id)
			if len(runs) == 0 {
				delete(r.active, taskId)
			}
		}
	}
}

// CancelTask обрывает все активные запуски задачи. Возвращает, сколько оборвано.
func (r *RunCancelRegistry) CancelTask(taskId string) int {
	r.mu.Lock()
	runs := r.active[taskId]
	cancels := make([]context.CancelFunc, 0, len(runs))
	for _, cancel := range runs {
		cancels = append(cancels, cancel)
	}
	delete(r.active, taskId)
	r.mu.Unlock()

	// Отменяем вне замка: обработчики завершения тоже берут этот замок.
	for _, cancel := range cancels {
		cancel()
	}
	return len(cancels)
}

// CancelAll обрывает все активные запуски (проба считает себя удалённой).
// Возвращает, сколько запусков оборвано.
func (r *RunCancelRegistry) CancelAll() int {
	r.mu.Lock()
	cancels := []context.CancelFunc{}
	for _, runs := range r.active {
		for _, cancel := range runs {
			cancels = append(cancels, cancel)
		}
	}
	r.active = map[string]map[uint64]context.CancelFunc{}
	r.mu.Unlock()

	for _, cancel := range cancels {
		cancel()
	}
	return len(cancels)
}

// ActiveRuns возвращает число активных запусков задачи (для тестов и диагностики).
func (r *RunCancelRegistry) ActiveRuns(taskId string) int {
	r.mu.Lock()
	defer r.mu.Unlock()
	return len(r.active[taskId])
}
