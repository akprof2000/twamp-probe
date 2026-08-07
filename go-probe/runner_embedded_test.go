package main

import (
	"context"
	"net"
	"strings"
	"testing"
	"time"
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
	return NewProbeRunner(cfg, results, NewRunRegistry(), NewRunCancelRegistry(), nil), results
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

func TestRunner_EmbeddedTwpingRunsWithoutExternalProcess(t *testing.T) {
	// Режим TWamp со встроенным клиентом: внешняя утилита twping не запускается,
	// замер идёт прямо в процессе пробы.
	cfg := &Config{
		TwampEmbedded: true,
		// Утилиты с таким именем нет: если встроенный путь не сработает,
		// это сразу видно по исходу «зонд не запустился».
		Twamp: ProbeToolConfig{Name: "twping-не-существует"},
	}
	results := NewResultStore(100, 0)
	runner := NewProbeRunner(cfg, results, NewRunRegistry(), NewRunCancelRegistry(), nil)

	task := &TaskInfo{
		Id: "twamp-emb", Title: "встроенный twping", Mode: ModeTWamp,
		// Адрес из документационного диапазона TEST-NET-1: сервера там нет,
		// замер завершится ошибкой соединения — но именно встроенного клиента.
		EndNode: "192.0.2.1", Circles: 1, Repeats: 1, TimeoutSec: 5,
		Parameters: map[string]string{"args": "-c 1"},
	}
	runner.RunForNodes(context.Background(), task)

	batch := results.TakeBatch(10).Items
	if len(batch) != 1 {
		t.Fatalf("получено результатов: %d, ожидался 1", len(batch))
	}
	if !strings.Contains(batch[0].CallLine, "twping(embedded)") {
		t.Errorf("строка вызова «%s» — ожидался встроенный клиент", batch[0].CallLine)
	}
	if batch[0].Outcome == string(OutcomeStartFailed) {
		t.Errorf("проба пыталась запустить внешнюю утилиту: %s", batch[0].ErrorConsole)
	}
}

func TestRunner_ExternalTwpingUsedWhenEmbeddedDisabled(t *testing.T) {
	// Без настройки режим TWamp по-прежнему выполняется внешней утилитой.
	cfg := &Config{Twamp: ProbeToolConfig{Name: "twping-не-существует"}}
	results := NewResultStore(100, 0)
	runner := NewProbeRunner(cfg, results, NewRunRegistry(), NewRunCancelRegistry(), nil)

	task := &TaskInfo{
		Id: "twamp-ext", Title: "внешний twping", Mode: ModeTWamp,
		EndNode: "192.0.2.1", Circles: 1, Repeats: 1, TimeoutSec: 5,
		Parameters: map[string]string{"args": "-c 1"},
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

func TestEmbeddedTwping_CancelStopsMeasurement(t *testing.T) {
	// Удаление задачи обрывает и встроенный замер TWamp — в том числе на
	// стадии подключения, где раньше пришлось бы ждать таймаута сети.
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan string, 1)
	go func() {
		_, errText, _ := runEmbeddedTwping(ctx, []string{"-c", "100", "192.0.2.1"}, time.Time{})
		done <- errText
	}()

	time.Sleep(100 * time.Millisecond)
	cancel()

	select {
	case errText := <-done:
		if !strings.Contains(errText, "отменена") {
			t.Errorf("замер завершился с «%s», ожидалось сообщение об отмене", errText)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("встроенный twping не прервался после отмены")
	}
}

// silentServer принимает TCP-соединения и молчит — сервер, который «висит».
//
// Недостижимый адрес для этого не годится: где-то он даёт долгое ожидание,
// а в контейнере маршрута до него нет вовсе, и соединение отбивается сразу —
// тогда замер падает по своей ошибке, не дожив до таймаута задачи, и проверка
// теряет смысл. Молчащий слушатель ведёт себя одинаково везде.
func silentServer(t *testing.T) string {
	t.Helper()
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("не поднять слушатель: %v", err)
	}
	t.Cleanup(func() { ln.Close() })

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			// Соединение принято и брошено открытым: клиент ждёт приветствия,
			// которого не будет.
			t.Cleanup(func() { conn.Close() })
		}
	}()
	return ln.Addr().String()
}

func TestEmbeddedTwping_RespectsTaskTimeout(t *testing.T) {
	// Индивидуальный таймаут задачи действует и во встроенном режиме.
	started := time.Now()
	_, errText, _ := runEmbeddedTwping(context.Background(),
		[]string{"-c", "100", silentServer(t)}, time.Now().Add(500*time.Millisecond))

	if !strings.Contains(errText, "таймауту") {
		t.Errorf("замер завершился с «%s», ожидалось сообщение о таймауте", errText)
	}
	if elapsed := time.Since(started); elapsed > 5*time.Second {
		t.Errorf("замер шёл %v — таймаут не сработал вовремя", elapsed)
	}
}

func TestPortExhaustionHint(t *testing.T) {
	// «address already in use» при привязке к порту 0 означает не конфликт,
	// а исчерпание эфемерных портов. Без подсказки это уводит в сторону:
	// на боевой пробе такая ошибка шла тысячами и выглядела необъяснимо.
	got := portExhaustionHint("Ошибка встроенного twping: не удалось открыть тестовый сокет: "+
		"listen udp4 10.123.20.142:0: bind: address already in use",
		[]string{"-c", "100", "192.0.2.1"})
	if !strings.Contains(got, "ip_local_port_range") {
		t.Errorf("подсказка не называет, что чинить: %q", got)
	}

	// Прочие ошибки подсказкой не засоряем.
	if got := portExhaustionHint("Ошибка встроенного twping: сервер отклонил сессию", nil); got != "" {
		t.Errorf("подсказка добавлена к посторонней ошибке: %q", got)
	}

	// Диапазон из одного порта задала сама проба: там нечему кончаться — занят
	// именно этот номер. Совет «расширьте эфемерный диапазон» тут уводит не туда.
	pooled := portExhaustionHint("Ошибка встроенного twping: не удалось открыть тестовый сокет: "+
		"нет свободного порта в диапазоне 31400-31400: listen udp4 10.123.20.142:31400: "+
		"bind: address already in use",
		[]string{"-P", "31400-31400", "-c", "100", "192.0.2.1"})
	if !strings.Contains(pooled, "31400") || !strings.Contains(pooled, "карантин") {
		t.Errorf("подсказка про порт пула не объясняет происходящее: %q", pooled)
	}
	if strings.Contains(pooled, "уменьшите Probe:MaxParallel") {
		t.Errorf("подсказка советует лечить не то: %q", pooled)
	}
}
