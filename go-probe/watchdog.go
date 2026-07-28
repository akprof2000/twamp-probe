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
		logWatchdog.Info("Сторож связи выключен (Probe:ServerTimeoutHours = 0)")
		return
	}
	logWatchdog.Info("Сторож связи запущен", "порог_молчания_ч", timeoutHours)

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
			logWatchdog.Warn("Сервер молчит дольше порога — проба считает себя удалённой, всё очищено",
				"последний_контакт", last.Format("02.01.2006 15:04"),
				"порог_ч", timeoutHours, "остановлено_задач", stopped)
		}
	}
}
