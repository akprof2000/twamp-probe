// Сторож связи с сервером — аналог C# ServerWatchdogService.
//
// Если сервер не обращался к пробе дольше Probe:ServerTimeoutHours часов
// (0 — сторож выключен), проба считает себя удалённой: останавливает все задачи
// по расписанию, удаляет реестр (TaskInfo.json) и очищает кэш недоставленных
// результатов (JobResult.json). HTTP продолжает слушаться: если сервер вернётся,
// его фоновая сверка восстановит задачи автоматически.
package main

import (
	"context"
	"log"
	"sync/atomic"
	"time"
)

// watchdogCheckInterval — период проверки сторожа.
const watchdogCheckInterval = 5 * time.Minute

// ContactTracker — потокобезопасная отметка последнего обращения сервера.
type ContactTracker struct {
	lastContact atomic.Int64 // UnixNano
}

// NewContactTracker создаёт трекер с отсчётом от запуска пробы.
func NewContactTracker() *ContactTracker {
	t := &ContactTracker{}
	t.Mark()
	return t
}

// Mark фиксирует обращение сервера (вызывается на каждый api-запрос).
func (t *ContactTracker) Mark() { t.lastContact.Store(time.Now().UnixNano()) }

// Last возвращает момент последнего обращения сервера.
func (t *ContactTracker) Last() time.Time { return time.Unix(0, t.lastContact.Load()) }

// RunWatchdog — цикл сторожа: молчание сервера дольше timeoutHours приводит
// к остановке всех задач и очистке кэша (однократно до следующего контакта).
func RunWatchdog(ctx context.Context, timeoutHours int, tracker *ContactTracker,
	tasks *TaskRegistry, results *ResultStore) {

	if timeoutHours <= 0 {
		log.Println("Сторож связи с сервером выключен (Probe:ServerTimeoutHours = 0)")
		return
	}
	log.Printf("Сторож связи: без запросов сервера дольше %d ч задачи будут остановлены", timeoutHours)

	timeout := time.Duration(timeoutHours) * time.Hour
	var lastCleared time.Time

	ticker := time.NewTicker(watchdogCheckInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			last := tracker.Last()
			// Уже чистили после этого контакта — ждём следующего обращения сервера.
			if !lastCleared.Before(last) || time.Since(last) < timeout {
				continue
			}

			stopped := tasks.ClearAll()
			results.Clear()
			lastCleared = time.Now()
			log.Printf("Сервер не обращался к пробе с %s (дольше %d ч) — проба считает себя удалённой: "+
				"остановлено задач %d, реестр и кэш результатов очищены",
				last.Format("02.01.2006 15:04"), timeoutHours, stopped)
		}
	}
}
