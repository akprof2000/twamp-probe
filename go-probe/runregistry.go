// Реестр состояния выполнения задач — аналог C# TaskRunRegistry.
// Всё в памяти: после перезапуска пробы статистика начинается заново.
package main

import (
	"sync"
	"time"
)

// RunRegistry — потокобезопасный реестр «как выполняются задачи прямо сейчас».
type RunRegistry struct {
	mu     sync.Mutex
	states map[string]*TaskRunInfo
}

// NewRunRegistry создаёт пустой реестр.
func NewRunRegistry() *RunRegistry {
	return &RunRegistry{states: map[string]*TaskRunInfo{}}
}

// get возвращает (или создаёт) запись задачи; вызывается под mu.
func (r *RunRegistry) get(taskId, title, mode string) *TaskRunInfo {
	info, ok := r.states[taskId]
	if !ok {
		info = &TaskRunInfo{TaskId: taskId, LastOutcome: OutcomeNotStarted}
		r.states[taskId] = info
	}
	if title != "" {
		info.Title = title
	}
	if mode != "" {
		info.Mode = mode
	}
	return info
}

// MarkStarted фиксирует начало выполнения задачи.
func (r *RunRegistry) MarkStarted(task *TaskInfo) {
	r.mu.Lock()
	defer r.mu.Unlock()
	info := r.get(task.Id, task.Title, string(task.Mode))
	info.Running++
	now := CsTime{time.Now()}
	info.LastStart = &now
	info.Executions++
	info.LastOutcome = OutcomeRunning
}

// ReportOutcome фиксирует исход завершившегося запуска зонда.
func (r *RunRegistry) ReportOutcome(taskId string, outcome RunOutcome, exitCode *int, result string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	info := r.get(taskId, "", "")
	info.LastOutcome = outcome
	info.LastExitCode = exitCode
	if result != "" {
		info.LastResult = &result
	} else {
		info.LastResult = nil
	}
	if outcome == OutcomeSuccess {
		info.SuccessTotal++
		info.LastError = nil // успех снимает «залипшую» ошибку
	} else {
		info.ErrorTotal++
		if result != "" {
			info.LastError = &result
		}
	}
}

// MarkFinished фиксирует завершение выполнения задачи.
func (r *RunRegistry) MarkFinished(taskId string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	info := r.get(taskId, "", "")
	if info.Running > 0 {
		info.Running--
	}
	now := CsTime{time.Now()}
	info.LastFinish = &now
}

// SetNextRun фиксирует момент следующего запланированного запуска.
func (r *RunRegistry) SetNextRun(taskId, title, mode string, nextRun *time.Time) {
	r.mu.Lock()
	defer r.mu.Unlock()
	info := r.get(taskId, title, mode)
	if nextRun == nil {
		info.NextRun = nil
	} else {
		t := CsTime{*nextRun}
		info.NextRun = &t
	}
}

// Remove удаляет запись задачи (задача удалена с пробы).
func (r *RunRegistry) Remove(taskId string) {
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.states, taskId)
}

// GetAll возвращает снимок всех записей.
func (r *RunRegistry) GetAll() []TaskRunInfo {
	r.mu.Lock()
	defer r.mu.Unlock()
	all := make([]TaskRunInfo, 0, len(r.states))
	for _, info := range r.states {
		all = append(all, *info)
	}
	return all
}
