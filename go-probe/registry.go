// Реестр задач по расписанию + cron-планирование — аналог C# Worker и CronExecuter.
//
// Сервер присылает только изменения (MergeJobs): новые задачи добавляются,
// существующие обновляются, помеченные Delete — удаляются. Разовые задачи
// (Repeater) выполняются сразу и в реестре не хранятся. Реестр переживает
// перезапуск (TaskInfo.json). Каждая задача планируется таймером на момент
// следующего срабатывания cron-выражения; по срабатыванию задача уходит
// в очередь диспетчера, и сразу планируется следующий запуск.
package main

import (
	"encoding/json"
	"maps"
	"os"
	"slices"
	"sync"
	"time"

	"github.com/robfig/cron/v3"
)

// tasksFileName — файл с сохранённым реестром задач по расписанию.
const tasksFileName = "TaskInfo.json"

// cronStandard разбирает классические 5-польные cron-выражения.
var cronStandard = cron.NewParser(
	cron.Minute | cron.Hour | cron.Dom | cron.Month | cron.Dow)

// cronWithSeconds разбирает 6-польные выражения с секундами.
var cronWithSeconds = cron.NewParser(
	cron.Second | cron.Minute | cron.Hour | cron.Dom | cron.Month | cron.Dow)

// scheduledTask — одна задача по расписанию с её таймером.
type scheduledTask struct {
	task  *TaskInfo
	timer *time.Timer
}

// Enqueuer — приёмник задач на выполнение (реализуется диспетчером;
// в тестах подменяется заглушкой).
type Enqueuer interface {
	Enqueue(task *TaskInfo)
}

// TaskRegistry — реестр задач по расписанию.
type TaskRegistry struct {
	mu         sync.Mutex
	tasks      map[string]*scheduledTask
	dispatcher Enqueuer
	registry   *RunRegistry
	cancels    *RunCancelRegistry // чтобы оборвать выполнение удалённой задачи
}

// NewTaskRegistry создаёт пустой реестр.
func NewTaskRegistry(dispatcher Enqueuer, registry *RunRegistry, cancels *RunCancelRegistry) *TaskRegistry {
	return &TaskRegistry{tasks: map[string]*scheduledTask{}, dispatcher: dispatcher, registry: registry, cancels: cancels}
}

// Load восстанавливает реестр из TaskInfo.json после перезапуска.
func (r *TaskRegistry) Load() {
	data, err := os.ReadFile(tasksFileName)
	if err != nil {
		return // файла нет — чистая проба, задачи дошлёт сверка сервера
	}
	var saved []TaskInfo
	if err := json.Unmarshal(data, &saved); err != nil {
		logRegistry.Error("Не удалось загрузить реестр задач", "файл", tasksFileName, "ошибка", err)
		return
	}
	r.MergeJobs(saved)
	logRegistry.Info("Реестр задач восстановлен с диска", "файл", tasksFileName, "задач", len(saved))
}

// MergeJobs применяет инкрементальные изменения задач (добавить/обновить/удалить).
func (r *TaskRegistry) MergeJobs(jobs []TaskInfo) {
	r.mu.Lock()
	defer r.mu.Unlock()

	changed := false
	for i := range jobs {
		changed = r.mergeOne(&jobs[i]) || changed
	}
	if changed {
		r.persist()
	}
}

// mergeOne применяет одну задачу; вызывается под mu. Возвращает «реестр изменился».
func (r *TaskRegistry) mergeOne(item *TaskInfo) bool {
	// Пришло удаление — прежде всего обрываем то, что уже выполняется.
	// Снять расписание мало: зонд вроде «twping -c 300» живёт минутами и иначе
	// домерял бы по несуществующей задаче. Делаем это для любой задачи, даже
	// если её нет в реестре: разовые (Repeater) там не хранятся вовсе.
	stopped := 0
	if item.Delete {
		stopped = r.cancels.CancelTask(item.Id)
	}

	// Разовые задачи выполняем немедленно и в реестре не храним.
	if item.Type == TypeRepeater {
		if !item.Delete {
			r.dispatcher.Enqueue(item)
		} else if stopped > 0 {
			logRegistry.Info("Разовая задача удалена — выполнение прервано",
				"задача", item.Id, "название", item.Title, "оборвано_запусков", stopped)
		}
		return false
	}

	// Задача по расписанию, помеченная на удаление.
	if item.Delete {
		entry, ok := r.tasks[item.Id]
		if !ok {
			// В реестре её уже нет, но выполнение мы прервали — это главное.
			return false
		}
		if entry.timer != nil {
			entry.timer.Stop()
		}
		delete(r.tasks, item.Id)
		r.registry.Remove(item.Id)
		logRegistry.Info("Задача удалена", "задача", item.Id, "название", item.Title,
			"оборвано_запусков", stopped)
		return true
	}

	// Обновление существующей или добавление новой.
	if entry, ok := r.tasks[item.Id]; ok {
		if entry.timer != nil {
			entry.timer.Stop()
		}
		entry.task = item
		r.scheduleNext(entry)
		logRegistry.Debug("Задача обновлена", "задача", item.Id, "название", item.Title)
		return true
	}

	entry := &scheduledTask{task: item}
	r.tasks[item.Id] = entry
	r.scheduleNext(entry)
	logRegistry.Debug("Задача добавлена", "задача", item.Id, "название", item.Title,
		"режим", item.Mode, "узел", item.EndNode, "расписание", item.CronExpression)
	return true
}

// scheduleNext планирует следующий запуск задачи; вызывается под mu.
func (r *TaskRegistry) scheduleNext(entry *scheduledTask) {
	task := entry.task

	parser := cronStandard
	if task.CronWithSeconds {
		parser = cronWithSeconds
	}
	schedule, err := parser.Parse(task.CronExpression)
	if err != nil {
		logRegistry.Error("Некорректное cron-выражение — задача не будет запускаться",
			"задача", task.Id, "название", task.Title, "выражение", task.CronExpression, "ошибка", err)
		r.registry.SetNextRun(task.Id, task.Title, string(task.Mode), nil)
		return
	}

	next := schedule.Next(time.Now())
	if !task.End.IsZero() && next.After(task.End.Time) {
		logRegistry.Info("Задача завершена по дате окончания",
			"задача", task.Id, "название", task.Title, "окончание", task.End.Format("02.01.2006 15:04"))
		r.registry.SetNextRun(task.Id, task.Title, string(task.Mode), nil)
		return
	}

	// Фиксируем план в реестре статусов — оператор видит, когда следующий запуск.
	r.registry.SetNextRun(task.Id, task.Title, string(task.Mode), &next)

	entry.timer = time.AfterFunc(time.Until(next), func() {
		// Только ставим в очередь — выполнение возьмёт диспетчер; следующее
		// срабатывание планируем сразу, не дожидаясь завершения зонда.
		r.dispatcher.Enqueue(task)

		r.mu.Lock()
		defer r.mu.Unlock()
		if current, ok := r.tasks[task.Id]; ok && current == entry {
			r.scheduleNext(entry)
		}
	})
}

// KnownTaskIds возвращает идентификаторы задач по расписанию (для сверки сервером).
func (r *TaskRegistry) KnownTaskIds() []string {
	r.mu.Lock()
	defer r.mu.Unlock()
	// Итератор ключей + сборка в срез — Go 1.23. Пустой результат обязан быть
	// «[]», а не «null»: сервер разворачивает ответ в HashSet без проверки на null.
	ids := slices.Collect(maps.Keys(r.tasks))
	if ids == nil {
		ids = []string{}
	}
	return ids
}

// AllTasks возвращает полные определения задач по расписанию, которые держит проба.
// Сервер забирает их для восстановления своей БД после потери данных.
func (r *TaskRegistry) AllTasks() []TaskInfo {
	r.mu.Lock()
	defer r.mu.Unlock()
	all := make([]TaskInfo, 0, len(r.tasks))
	for _, entry := range r.tasks {
		all = append(all, *entry.task)
	}
	return all
}

// ClearAll останавливает и удаляет ВСЕ задачи по расписанию вместе с файлом реестра.
// Используется сторожем связи: сервер молчит дольше таймаута — проба считает себя
// удалённой. Возвращает число остановленных задач.
func (r *TaskRegistry) ClearAll() int {
	r.mu.Lock()
	defer r.mu.Unlock()

	count := len(r.tasks)
	for id, entry := range r.tasks {
		if entry.timer != nil {
			entry.timer.Stop()
		}
		delete(r.tasks, id)
		r.registry.Remove(id)
	}
	_ = os.Remove(tasksFileName)
	// Заодно обрываем всё, что выполняется прямо сейчас.
	if stopped := r.cancels.CancelAll(); stopped > 0 {
		logRegistry.Info("Оборваны выполняющиеся запуски", "запусков", stopped)
	}
	return count
}

// persist сохраняет реестр на диск; вызывается под mu.
func (r *TaskRegistry) persist() {
	all := make([]TaskInfo, 0, len(r.tasks))
	for _, entry := range r.tasks {
		all = append(all, *entry.task)
	}
	data, err := json.Marshal(all)
	if err != nil {
		logRegistry.Error("Не удалось сериализовать реестр задач", "ошибка", err)
		return
	}
	if err := os.WriteFile(tasksFileName, data, 0o644); err != nil {
		logRegistry.Error("Не удалось сохранить реестр задач", "файл", tasksFileName, "ошибка", err)
	}
}
