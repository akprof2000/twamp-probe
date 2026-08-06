// Запуск внешних зондов (ping / twping / twampy) — аналог C# ProbeRunner.
//
// Для каждого узла задачи выполняются циклы и повторы; процесс зонда ограничен
// индивидуальным таймаутом (по истечении — принудительное завершение всей группы).
// Результат каждого запуска (вывод, ошибки, код выхода, исход) уходит в ResultStore.
package main

import (
	"bytes"
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strings"
	"time"
)

// ProbeRunner — исполнитель зондов для узлов задачи.
type ProbeRunner struct {
	cfg      *Config
	results  *ResultStore
	registry *RunRegistry
	cancels  *RunCancelRegistry // активные запуски — чтобы оборвать удалённую задачу
	ports    *PortPool          // локальные порты зондов (nil — порт выбирает ядро)
	baseDir  string             // каталог приложения — для PYTHONPATH вендоренного twampy
}

// NewProbeRunner создаёт исполнитель.
func NewProbeRunner(cfg *Config, results *ResultStore, registry *RunRegistry,
	cancels *RunCancelRegistry, ports *PortPool) *ProbeRunner {

	exe, err := os.Executable()
	base := "."
	if err == nil {
		base = filepath.Dir(exe)
	}
	return &ProbeRunner{
		cfg: cfg, results: results, registry: registry,
		cancels: cancels, ports: ports, baseDir: base,
	}
}

// RunForNodes выполняет все циклы и повторы зонда для каждого узла задачи параллельно.
func (r *ProbeRunner) RunForNodes(ctx context.Context, task *TaskInfo) {
	nodes := splitNodes(task.EndNode)
	done := make(chan struct{}, len(nodes))
	for _, node := range nodes {
		// С Go 1.22 переменная цикла своя на каждой итерации — захват безопасен.
		go func() {
			defer func() { done <- struct{}{} }()
			r.runSingleNode(ctx, task, node)
		}()
	}
	for range nodes {
		<-done
	}
}

// splitNodes разбирает список узлов через «;» или «,».
func splitNodes(endNode string) []string {
	fields := strings.FieldsFunc(endNode, func(r rune) bool { return r == ';' || r == ',' })
	nodes := make([]string, 0, len(fields))
	for _, f := range fields {
		if trimmed := strings.TrimSpace(f); trimmed != "" {
			nodes = append(nodes, trimmed)
		}
	}
	return nodes
}

// runSingleNode выполняет циклы (Circles) × повторы (Repeats) для одного узла.
func (r *ProbeRunner) runSingleNode(ctx context.Context, task *TaskInfo, node string) {
	execName, args, env := r.buildCommand(task, node)

	for circle := range task.Circles { // range по числу — Go 1.22
		for range task.Repeats {
			if ctx.Err() != nil {
				return
			}
			result := r.execute(ctx, task, node, execName, args, env)
			if result.Cancelled {
				return // задачу удалили — прекращаем циклы, результат не отправляем
			}
			r.results.Add(result)
		}
		// Пауза между циклами (кроме последнего).
		if circle != task.Circles-1 && task.PauseSec > 0 {
			select {
			case <-time.After(time.Duration(task.PauseSec) * time.Second):
			case <-ctx.Done():
				return
			}
		}
	}
}

// buildCommand формирует имя исполняемого файла, аргументы и окружение по режиму задачи.
func (r *ProbeRunner) buildCommand(task *TaskInfo, node string) (string, []string, []string) {
	params := r.taskParameters(task)

	switch task.Mode {
	case ModeWinPing:
		// Для ping адрес узла идёт первым аргументом, затем параметры.
		return r.cfg.Ping.Name, append([]string{node}, orDefault(params, r.cfg.Ping.Default)...), nil

	case ModeTWampy:
		// nokia/twampy: «python -m twampy sender <far-end> [опции]» — узел первым,
		// локальный порт эфемерный, чтобы тысячи отправителей не конфликтовали.
		// PYTHONPATH указывает на каталог приложения с вендоренным пакетом twampy.
		args := append([]string{"-m", "twampy", "sender", node}, orDefault(params, r.cfg.Twampy.Default)...)
		env := append(os.Environ(), "PYTHONPATH="+r.baseDir+string(os.PathListSeparator)+os.Getenv("PYTHONPATH"))
		return r.cfg.Twampy.Name, args, env

	default: // ModeTWamp
		// Для twping сначала параметры, адрес узла — последним аргументом.
		return r.cfg.Twamp.Name, append(orDefault(params, r.cfg.Twamp.Default), node), nil
	}
}

// taskParameters собирает аргументы из параметров задачи (значения через пробел).
func (r *ProbeRunner) taskParameters(task *TaskInfo) []string {
	var args []string
	for _, value := range task.Parameters {
		args = append(args, strings.Fields(value)...)
	}
	return args
}

// orDefault возвращает аргументы задачи либо аргументы по умолчанию из конфигурации.
func orDefault(params []string, def string) []string {
	if len(params) > 0 {
		return params
	}
	return strings.Fields(def)
}

// embeddedProbe — встроенный зонд: выполняет замер прямо в процессе пробы и
// возвращает вывод и текст ошибки в том же виде, что и внешняя утилита.
type embeddedProbe func(ctx context.Context, args []string, deadline time.Time) (output, errText string)

// runReport — итог одного прогона зонда для журнала и реестра статусов.
//
// Прогон сам о себе не отчитывается: за неудачной попыткой может последовать
// повтор с другим портом, и тогда в журнале появился бы провал задачи, которой
// на деле ещё предстоит выполниться. Решение принимает execute — он один знает,
// была попытка последней или нет.
//
// Пустой outcome означает, что прогон уже отчитался сам: такие исходы
// терминальны (зонд не запустился, задачу удалили) и повтору не подлежат.
type runReport struct {
	outcome  RunOutcome
	exitCode *int // nil — процесса не было (встроенный зонд) либо он не запустился
	summary  string
	elapsed  time.Duration
}

// report пишет итог замера в журнал и в реестр статусов — ровно один раз на
// замер, после того как исход стал окончательным.
func (r *ProbeRunner) report(task *TaskInfo, node string, rep runReport) {
	if rep.outcome == "" {
		return // прогон отчитался сам
	}
	r.registry.ReportOutcome(task.Id, rep.outcome, rep.exitCode, rep.summary)
	logRun(task, node, rep.outcome, rep.exitCode, rep.elapsed, rep.summary)
}

// cancelReason — обычная причина отмены: задачу удалили на сервере.
const cancelReason = "Задача удалена — выполнение прервано"

// cancelledRun оформляет прерванный запуск: замера больше никто не ждёт, поэтому
// результат помечается отменённым и серверу не отправляется — иначе в отчёте
// появился бы обрывок замера по уже удалённой задаче.
//
// Отчитывается сразу сам: отмена окончательна, повторять с другим портом нечего.
func (r *ProbeRunner) cancelledRun(task *TaskInfo, node string, started time.Time,
	message string, result *ActionData) (ActionData, runReport) {

	logRunner.Warn(message,
		"название", task.Title, "узел", node, "режим", task.Mode,
		"длительность", time.Since(started).Round(time.Millisecond), "задача", task.Id)
	r.registry.ReportOutcome(task.Id, OutcomeNotStarted, nil, message)

	result.Outcome = string(OutcomeNotStarted)
	result.ErrorConsole = message
	result.Cancelled = true
	return *result, runReport{}
}

// execute выполняет один замер: встроенным зондом, если он включён для этого
// режима, иначе — запуском внешнего процесса.
//
// Локальный порт зонду выдаёт проба, а не ядро: так два замера не могут взять
// один и тот же, а когда свободных нет — замер ждёт освобождения вместо того
// чтобы упасть с «address already in use». Пул общий для всех режимов, поэтому
// twping и twampy за номера не конкурируют.
func (r *ProbeRunner) execute(
	ctx context.Context, task *TaskInfo, node, execName string, args, env []string) ActionData {

	var result ActionData
	var report runReport

	// Порт из пула может оказаться занят посторонним процессом: диапазон пробы
	// пересекается с эфемерным диапазоном ядра, и чужое соединение способно
	// встать на наш номер между выдачей и привязкой. Раньше такой замер просто
	// пропадал; теперь берём другой порт и пробуем снова.
	for attempt := 1; ; attempt++ {
		port, ok := r.ports.Acquire(ctx)
		if !ok {
			// Ждать смысла больше нет: задачу сняли или проба останавливается.
			return ActionData{
				ResultId: NewGuid(), Creation: CsTime{time.Now()}, TaskId: task.Id,
				EndNode: node, IPAddress: task.IpAddress, RequestInfo: task.RequestInfo,
				Mode: string(task.Mode), Cancelled: true,
			}
		}

		attemptArgs, portApplied := withLocalPort(task.Mode, args, port)
		if probe, callLine, probeArgs := r.embeddedFor(task, attemptArgs); probe != nil {
			result, report = r.executeEmbedded(ctx, task, node, callLine, probeArgs, probe)
		} else {
			result, report = r.executeOnce(ctx, task, node, execName, attemptArgs, env)
		}

		busy := portTaken(result.ErrorConsole) || portTaken(result.Console)
		// Занятым числим только тот порт, который зонду действительно достался.
		// Если порт свой указала задача (или вызов нестандартный и подставить
		// номер некуда), упал чужой номер — карантинить наш незачем, да и повтор
		// ничего не изменит: следующая попытка возьмёт ровно тот же чужой порт.
		ourPortBusy := busy && portApplied
		if !ourPortBusy {
			r.ports.Release(port)
			r.report(task, node, report)
			return result
		}

		// Порт занят: в оборот он временно не идёт, иначе следующий замер
		// налетел бы на ту же ошибку. Сам замер повторяем с другим номером.
		r.ports.Blacklist(port)
		free, _, quarantined, _ := r.ports.Stats()

		if attempt >= portRetries || result.Cancelled {
			logRunner.Warn("Порт занят посторонним процессом — замер не удался",
				"порт", port, "попыток", attempt, "режим", task.Mode,
				"свободно", free, "в_карантине", quarantined,
				"задача", task.Id, "узел", node)
			r.report(task, node, report)
			return result
		}
		logRunner.Warn("Порт занят посторонним процессом — в карантин, пробуем другой",
			"порт", port, "попытка", attempt, "режим", task.Mode,
			"свободно", free, "в_карантине", quarantined,
			"задача", task.Id, "узел", node)
	}
}

// portRetries — сколько раз пробовать другой порт, если выданный занят.
//
// Попытки не бесплодны: каждый занятый порт уходит в карантин, поэтому выбор
// с каждым разом сужается и следующая попытка вероятнее предыдущей. Десяти
// хватает и на редкое невезение, и на несколько чужих сокетов подряд; если
// заняты и они, дело не в случайности, а в том, что пул пересекается с
// эфемерным диапазоном ядра, — это лечится ip_local_reserved_ports, а не
// повторами.
const portRetries = 10

// withLocalPort добавляет зонду локальный порт, если проба раздаёт их сама
// (port > 0) и задача не указала свой. Второе значение сообщает, достался ли
// порт зонду: без него замер пойдёт с номером от ядра, и отвечать за него пул
// не может.
//
// У каждой утилиты свой способ: twping принимает диапазон флагом -P, а twampy —
// адрес near-end вторым позиционным аргументом.
func withLocalPort(mode TaskMode, args []string, port int) ([]string, bool) {
	if port <= 0 {
		return args, false
	}

	switch mode {
	case ModeTWamp:
		if slices.Contains(args, "-P") {
			return args, false // задача выбрала диапазон сама — не перебиваем
		}
		// Диапазон из одного порта: именно его зонд и займёт. Перебирать номера
		// внутри диапазона — дело пула, а не зонда, иначе два замера столкнулись
		// бы на одном порту.
		return append([]string{"-P", fmt.Sprintf("%d-%d", port, port)}, args...), true

	case ModeTWampy:
		return withTwampyNearEnd(args, port)
	}
	return args, false
}

// withTwampyNearEnd задаёт локальный адрес отправителя в вызове twampy.
//
// Формат: «sender <far-end> [near-end] [опции]». Возможны два случая:
//
//   - near-end задан задачей (например «10.123.20.140») — тогда порт
//     дописывается к нему: «10.123.20.140:20006». Вставлять порт отдельным
//     аргументом здесь нельзя: адрес задачи съехал бы на третью позицию,
//     где twampy его уже не ждёт, и замер пошёл бы не с того интерфейса;
//   - near-end не задан — добавляем «:порт» сразу за far-end.
func withTwampyNearEnd(args []string, port int) ([]string, bool) {
	idx := indexOf(args, "sender")
	if idx < 0 {
		return args, false
	}

	far := idx + 1 // адрес рефлектора
	if far >= len(args) || strings.HasPrefix(args[far], "-") {
		return args, false // вызов не похож на «sender <адрес>» — не вмешиваемся
	}

	near := far + 1
	if near < len(args) && !strings.HasPrefix(args[near], "-") {
		if hasPort(args[near]) {
			return args, false // задача указала и адрес, и порт — её выбор важнее
		}
		out := slices.Clone(args)
		out[near] = fmt.Sprintf("%s:%d", args[near], port)
		return out, true
	}

	return slices.Insert(slices.Clone(args), near, fmt.Sprintf(":%d", port)), true
}

// hasPort сообщает, указан ли в адресе порт. Для IPv6 в квадратных скобках
// («[::1]:20001») двоеточия внутри адреса не считаются.
func hasPort(addr string) bool {
	if end := strings.LastIndex(addr, "]"); end >= 0 {
		return strings.Contains(addr[end:], ":")
	}
	return strings.Contains(addr, ":")
}

// portTaken распознаёт отказ ядра «порт уже занят».
func portTaken(text string) bool {
	return strings.Contains(text, "address already in use")
}

// embeddedFor выбирает встроенный зонд для режима задачи: возвращает функцию
// замера, строку вызова для журнала и аргументы. Нулевая функция означает,
// что режим выполняется внешним процессом.
func (r *ProbeRunner) embeddedFor(task *TaskInfo, args []string) (embeddedProbe, string, []string) {
	switch {
	case task.Mode == ModeTWampy && r.cfg.TwampyEmbedded:
		// Аргументы python-вызова («-m twampy sender <узел> …») приводим к виду
		// самого отправителя: разбор опций у него общий с оригиналом.
		senderArgs := args
		if idx := indexOf(senderArgs, "sender"); idx >= 0 {
			senderArgs = senderArgs[idx+1:]
		}
		return runEmbeddedTwampy, "twampy(embedded) sender " + strings.Join(senderArgs, " "), senderArgs

	case task.Mode == ModeTWamp && r.cfg.TwampEmbedded:
		// Аргументы twping совпадают с аргументами внешней утилиты: клиент тот же.
		return runEmbeddedTwping, "twping(embedded) " + strings.Join(args, " "), args
	}
	return nil, "", nil
}

// executeEmbedded выполняет замер встроенным зондом.
//
// Внешний процесс не запускается, поэтому такой замер не занимает ни процесса,
// ни потока ожидания — предел одновременных замеров на него не действует
// (см. docs/parallelism.md). Вывод совпадает с внешней утилитой: серверный
// парсер и отчёты не отличают один режим от другого.
func (r *ProbeRunner) executeEmbedded(ctx context.Context, task *TaskInfo, node,
	callLine string, args []string, probe embeddedProbe) (ActionData, runReport) {

	started := time.Now()
	result := ActionData{
		ResultId:    NewGuid(),
		Creation:    CsTime{started},
		TaskId:      task.Id,
		EndNode:     node,
		IPAddress:   task.IpAddress,
		RequestInfo: task.RequestInfo,
		Mode:        string(task.Mode),
		CallLine:    callLine,
	}

	// Отмена работает так же, как для внешнего зонда: удаление задачи на сервере
	// обрывает замер немедленно, а не после его окончания.
	runCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	untrack := r.cancels.Track(task.Id, cancel)
	defer untrack()

	var deadline time.Time
	if task.TimeoutSec > 0 {
		deadline = started.Add(time.Duration(task.TimeoutSec) * time.Second)
	}

	output, errText := probe(runCtx, args, deadline)

	// Отмена именно этого замера (задачу удалили), а не остановка всей пробы.
	cancelled := runCtx.Err() != nil && ctx.Err() == nil
	timedOut := strings.Contains(errText, errTimeout.Error())

	switch {
	case cancelled:
		return r.cancelledRun(task, node, started, cancelReason, &result)

	case timedOut:
		errText = fmt.Sprintf("Задача прервана по таймауту %d c.", task.TimeoutSec)
	}

	outcome := OutcomeSuccess
	switch {
	case timedOut:
		outcome = OutcomeTimedOut
	case errText != "":
		outcome = OutcomeExitCodeError
	}

	summary := errText
	if outcome == OutcomeSuccess {
		summary = lastLine(output)
	}

	// Кода выхода у встроенного зонда нет: процесса не было. В результате он
	// остаётся нулевым — этого поля ждёт сервер, — но в журнал и в реестр не
	// идёт, иначе строка «исход=ExitCodeError код=0» противоречит сама себе.
	exitCode := 0
	result.ExitCode = &exitCode
	result.Outcome = string(outcome)
	result.Console = output
	result.ErrorConsole = errText

	return result, runReport{outcome: outcome, summary: summary, elapsed: time.Since(started)}
}

// indexOf возвращает позицию значения в срезе (-1, если его нет).
func indexOf(items []string, value string) int {
	for i, item := range items {
		if item == value {
			return i
		}
	}
	return -1
}

// executeOnce запускает процесс зонда один раз и возвращает собранный результат.
func (r *ProbeRunner) executeOnce(
	ctx context.Context, task *TaskInfo, node, execName string, args, env []string) (ActionData, runReport) {

	callLine := execName + " " + strings.Join(args, " ")
	started := time.Now()
	result := ActionData{
		ResultId:    NewGuid(),
		Creation:    CsTime{started},
		TaskId:      task.Id,
		EndNode:     node,
		IPAddress:   task.IpAddress,
		RequestInfo: task.RequestInfo,
		Mode:        string(task.Mode),
		CallLine:    callLine,
	}

	// Контекст запуска отменяем всегда: по индивидуальному таймауту задачи и по
	// команде извне (задачу удалили на сервере) — тогда процесс зонда завершается
	// принудительно, не дожидаясь конца замера.
	var runCtx context.Context
	var cancel context.CancelFunc
	if task.TimeoutSec > 0 {
		runCtx, cancel = context.WithTimeout(ctx, time.Duration(task.TimeoutSec)*time.Second)
	} else {
		runCtx, cancel = context.WithCancel(ctx)
	}
	defer cancel()

	untrack := r.cancels.Track(task.Id, cancel)
	defer untrack()

	cmd := exec.CommandContext(runCtx, execName, args...)
	if env != nil {
		cmd.Env = env
	}
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	configureProcessGroup(cmd) // убивать всё дерево процессов по таймауту

	err := cmd.Start()
	if err != nil {
		// Отмена успела прийти до самого запуска: exec отказывается стартовать по
		// отменённому контексту. Зонд тут ни при чём — задачу удалили (или проба
		// останавливается), и обходиться с этим надо как с отменой, а не как с
		// поломкой. Иначе по удалённой задаче уходит выдуманный результат «зонд не
		// запустился», а в журнале появляется ERROR на ровном месте.
		if runCtx.Err() != nil {
			message := cancelReason
			if ctx.Err() != nil {
				message = "Проба останавливается — замер не начат"
			}
			return r.cancelledRun(task, node, started, message, &result)
		}
		// Зонд не запустился (например, утилита не установлена) — ошибка обязана
		// дойти до сервера как результат, иначе задача выглядит «молча пропавшей».
		message := fmt.Sprintf("Не удалось запустить зонд «%s»: %v", execName, err)
		logRunner.Error("Задача: зонд не запустился",
			"название", task.Title, "узел", node, "режим", task.Mode,
			"команда", callLine, "ошибка", err, "задача", task.Id)
		r.registry.ReportOutcome(task.Id, OutcomeStartFailed, nil, message)
		result.Outcome = string(OutcomeStartFailed)
		result.ErrorConsole = message
		return result, runReport{}
	}

	waitErr := cmd.Wait()
	timedOut := runCtx.Err() == context.DeadlineExceeded
	// Отмена именно этого запуска (задачу удалили), а не остановка всей пробы.
	cancelled := runCtx.Err() == context.Canceled && ctx.Err() == nil
	exitCode := cmd.ProcessState.ExitCode()

	if cancelled {
		return r.cancelledRun(task, node, started, cancelReason, &result)
	}

	output := stdout.String()
	errText := stderr.String()

	// В ErrorConsole собираются ВСЕ ошибки запуска: stderr, таймаут, код выхода.
	switch {
	case timedOut:
		note := fmt.Sprintf("Задача прервана по таймауту %d c и принудительно завершена.", task.TimeoutSec)
		errText = joinNonEmpty(errText, note)
	case exitCode != 0:
		note := fmt.Sprintf("Процесс зонда завершился с кодом %d.", exitCode)
		errText = joinNonEmpty(errText, note)
	case waitErr != nil && exitCode == 0:
		errText = joinNonEmpty(errText, waitErr.Error())
	}

	outcome := OutcomeSuccess
	if timedOut {
		outcome = OutcomeTimedOut
	} else if exitCode != 0 {
		outcome = OutcomeExitCodeError
	}

	summary := errText
	if outcome == OutcomeSuccess {
		summary = lastLine(output)
	}

	result.ExitCode = &exitCode
	result.Outcome = string(outcome)
	result.Console = output
	result.ErrorConsole = errText

	return result, runReport{
		outcome: outcome, exitCode: &exitCode, summary: summary, elapsed: time.Since(started),
	}
}

// logRun пишет итог одного прогона зонда: успех — Info, нештатный исход — Warn.
// По записи видно, когда, какая задача и по какому узлу отработала и с каким результатом.
func logRun(task *TaskInfo, node string, outcome RunOutcome, exitCode *int,
	elapsed time.Duration, summary string) {

	fields := []any{
		"название", task.Title,
		"узел", node,
		"режим", task.Mode,
		"исход", outcome,
	}
	// Код выхода есть только у внешнего зонда: у встроенного процесса нет, и
	// «код=0» рядом с нештатным исходом только сбивал бы с толку.
	if exitCode != nil {
		fields = append(fields, "код", *exitCode)
	}
	fields = append(fields,
		"длительность", elapsed.Round(time.Millisecond),
		"задача", task.Id,
	)
	if outcome == OutcomeSuccess {
		logRunner.Info("Задача выполнена", fields...)
		return
	}
	// Для нештатного исхода добавляем краткую причину (первая строка ошибки).
	logRunner.Warn("Задача завершилась нештатно", append(fields, "причина", firstLine(summary))...)
}

// firstLine возвращает первую непустую строку текста (краткая причина для лога).
func firstLine(text string) string {
	for _, line := range strings.Split(text, "\n") {
		if trimmed := strings.TrimSpace(line); trimmed != "" {
			return trimmed
		}
	}
	return ""
}

// joinNonEmpty объединяет две строки через перевод строки, пропуская пустые.
func joinNonEmpty(a, b string) string {
	if a == "" {
		return b
	}
	return a + "\n" + b
}

// lastLine возвращает последнюю непустую строку вывода (итоговую статистику),
// обрезанную до 200 символов — как краткий результат для статуса задачи.
func lastLine(output string) string {
	lines := strings.Split(strings.TrimSpace(output), "\n")
	for _, raw := range slices.Backward(lines) { // итератор обратного обхода — Go 1.23
		if line := strings.TrimSpace(raw); line != "" {
			return line[:min(len(line), 200)] // встроенный min — Go 1.21
		}
	}
	return ""
}
