// Тесты реестра задач: инкрементальное слияние, сверка, очистка сторожем.
package main

import (
	"os"
	"testing"
)

// fakeEnqueuer — заглушка диспетчера: копит поставленные в очередь задачи.
type fakeEnqueuer struct{ enqueued []*TaskInfo }

func (f *fakeEnqueuer) Enqueue(task *TaskInfo) { f.enqueued = append(f.enqueued, task) }

// newTestRegistry — реестр во временном каталоге (не трогает рабочие файлы).
func newTestRegistry(t *testing.T) (*TaskRegistry, *fakeEnqueuer) {
	t.Helper()
	dir := t.TempDir()
	cwd, _ := os.Getwd()
	if err := os.Chdir(dir); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chdir(cwd) })

	sink := &fakeEnqueuer{}
	return NewTaskRegistry(sink, NewRunRegistry()), sink
}

func schedulerTask(id string) TaskInfo {
	return TaskInfo{
		Id: id, Title: "t-" + id, Type: TypeScheduler, Mode: ModeWinPing,
		CronExpression: "*/5 * * * *",
	}
}

// Добавление и удаление задач по расписанию отражаются в KnownTaskIds и в файле реестра.
func TestRegistry_MergeAddAndDelete(t *testing.T) {
	reg, _ := newTestRegistry(t)

	reg.MergeJobs([]TaskInfo{schedulerTask("a"), schedulerTask("b")})
	if ids := reg.KnownTaskIds(); len(ids) != 2 {
		t.Fatalf("ожидалось 2 задачи, получено %d", len(ids))
	}
	if _, err := os.Stat(tasksFileName); err != nil {
		t.Errorf("реестр должен сохраниться в %s: %v", tasksFileName, err)
	}

	del := schedulerTask("a")
	del.Delete = true
	reg.MergeJobs([]TaskInfo{del})
	ids := reg.KnownTaskIds()
	if len(ids) != 1 || ids[0] != "b" {
		t.Errorf("после удаления должна остаться только «b», получено %v", ids)
	}
}

// Разовая задача уходит в очередь и в реестре не хранится.
func TestRegistry_RepeaterGoesToQueue(t *testing.T) {
	reg, sink := newTestRegistry(t)

	task := schedulerTask("r")
	task.Type = TypeRepeater
	reg.MergeJobs([]TaskInfo{task})

	if len(sink.enqueued) != 1 {
		t.Fatalf("разовая задача должна попасть в очередь, получено %d", len(sink.enqueued))
	}
	if len(reg.KnownTaskIds()) != 0 {
		t.Error("разовая задача не должна храниться в реестре")
	}
}

// AllTasks отдаёт полные определения задач (для восстановления сервера).
func TestRegistry_AllTasksFullDefinitions(t *testing.T) {
	reg, _ := newTestRegistry(t)

	task := schedulerTask("x")
	task.Title = "восстанавливаемая"
	task.EndNode = "10.0.0.7:5018"
	reg.MergeJobs([]TaskInfo{task})

	all := reg.AllTasks()
	if len(all) != 1 {
		t.Fatalf("ожидалась 1 задача, получено %d", len(all))
	}
	if all[0].Id != "x" || all[0].Title != "восстанавливаемая" || all[0].EndNode != "10.0.0.7:5018" {
		t.Errorf("неполное определение задачи: %+v", all[0])
	}
}

// Пустой реестр отдаёт «[]», а не null, — сервер не проверяет ответ на null.
func TestRegistry_EmptyIdsNotNil(t *testing.T) {
	reg, _ := newTestRegistry(t)
	if ids := reg.KnownTaskIds(); ids == nil {
		t.Error("KnownTaskIds обязан вернуть пустой срез, а не nil")
	}
}

// ClearAll (сторож связи) останавливает всё и удаляет файл реестра.
func TestRegistry_ClearAll(t *testing.T) {
	reg, _ := newTestRegistry(t)

	reg.MergeJobs([]TaskInfo{schedulerTask("a"), schedulerTask("b")})
	if stopped := reg.ClearAll(); stopped != 2 {
		t.Errorf("ожидалось 2 остановленных задачи, получено %d", stopped)
	}
	if len(reg.KnownTaskIds()) != 0 {
		t.Error("после ClearAll реестр должен быть пуст")
	}
	if _, err := os.Stat(tasksFileName); !os.IsNotExist(err) {
		t.Error("файл реестра должен быть удалён")
	}
}
