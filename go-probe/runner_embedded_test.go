package main

import (
	"context"
	"strings"
	"testing"
)

// newEmbeddedRunner собирает исполнитель со встроенным twampy.
func newEmbeddedRunner(t *testing.T, embedded bool) (*ProbeRunner, *ResultStore) {
	t.Helper()
	cfg := &Config{
		TwampyEmbedded: embedded,
		// Заведомо несуществующая утилита: если встроенный режим не сработает,
		// проба попытается запустить внешний процесс, и это будет видно по исходу.
		Twampy: ProbeToolConfig{Name: "python3-не-существует", Default: ""},
	}
	results := NewResultStore(100, 0)
	return NewProbeRunner(cfg, results, NewRunRegistry(), NewRunCancelRegistry()), results
}

func TestRunner_EmbeddedTwampyRunsWithoutExternalProcess(t *testing.T) {
	// При включённом встроенном режиме внешний python не запускается: замер идёт
	// прямо в процессе пробы. Проверяем по строке вызова и по тому, что замер
	// состоялся, хотя указанной утилиты в системе нет.
	runner, results := newEmbeddedRunner(t, true)

	task := &TaskInfo{
		Id: "emb-1", Title: "встроенный замер", Mode: ModeTWampy,
		// Рефлектора нет, поэтому отправитель ждёт ответы до конца окна (~5 с) —
		// таймаут задачи берём с запасом, иначе замер прервётся без таблицы.
		EndNode: "127.0.0.1:20999", Circles: 1, Repeats: 1, TimeoutSec: 20,
		Parameters: map[string]string{"args": "-c 2 -i 100"},
	}
	runner.RunForNodes(context.Background(), task)

	batch := results.TakeBatch(10).Items
	if len(batch) != 1 {
		t.Fatalf("получено результатов: %d, ожидался 1", len(batch))
	}
	if !strings.Contains(batch[0].CallLine, "embedded") {
		t.Errorf("строка вызова «%s» — ожидался встроенный режим", batch[0].CallLine)
	}
	// Рефлектора нет: как и оригинальный twampy, встроенный печатает таблицу
	// с пометкой о полной потере — её и разбирает серверный парсер.
	if !strings.Contains(batch[0].Console, "Jitter Algorithm") {
		t.Errorf("в выводе нет таблицы замера: исход=%s вывод=%q ошибка=%q",
			batch[0].Outcome, batch[0].Console, batch[0].ErrorConsole)
	}
	if !strings.Contains(batch[0].Console, "100% loss") {
		t.Errorf("без рефлектора ожидалась полная потеря, получено:\n%s", batch[0].Console)
	}
}

func TestRunner_ExternalTwampyUsedWhenEmbeddedDisabled(t *testing.T) {
	// Без встроенного режима проба запускает внешний процесс — и, раз утилиты
	// нет, честно сообщает об ошибке запуска, а не выполняет замер сама.
	runner, results := newEmbeddedRunner(t, false)

	task := &TaskInfo{
		Id: "ext-1", Title: "внешний замер", Mode: ModeTWampy,
		EndNode: "127.0.0.1:20999", Circles: 1, Repeats: 1, TimeoutSec: 3,
		Parameters: map[string]string{"args": "-c 2 -i 100"},
	}
	runner.RunForNodes(context.Background(), task)

	batch := results.TakeBatch(10).Items
	if len(batch) != 1 {
		t.Fatalf("получено результатов: %d, ожидался 1", len(batch))
	}
	if strings.Contains(batch[0].CallLine, "embedded") {
		t.Errorf("строка вызова «%s» — ожидался внешний процесс", batch[0].CallLine)
	}
	if batch[0].Outcome != string(OutcomeStartFailed) {
		t.Errorf("исход «%s», ожидался отказ запуска несуществующей утилиты", batch[0].Outcome)
	}
}
