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
	baseDir  string             // каталог приложения — для PYTHONPATH вендоренного twampy
}

// NewProbeRunner создаёт исполнитель.
func NewProbeRunner(cfg *Config, results *ResultStore, registry *RunRegistry, cancels *RunCancelRegistry) *ProbeRunner {
	exe, err := os.Executable()
	base := "."
	if err == nil {
		base = filepath.Dir(exe)
	}
	return &ProbeRunner{cfg: cfg, results: results, registry: registry, cancels: cancels, baseDir: base}
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

// execute выполняет один замер: встроенным отправителем, если он включён для
// этого режима, иначе — запуском внешнего процесса зонда.
func (r *ProbeRunner) execute(
	ctx context.Context, task *TaskInfo, node, execName string, args, env []string) ActionData {

	if task.Mode == ModeTWampy && r.cfg.TwampyEmbedded {
		return r.executeEmbeddedTwampy(ctx, task, node, args)
	}
	return r.executeOnce(ctx, task, node, execName, args, env)
}

// executeEmbeddedTwampy выполняет замер TWampy прямо в процессе пробы.
//
// Внешний процесс не запускается, поэтому такой замер не занимает ни процесса,
// ни потока ожидания — предел одновременных замеров на него не действует
// (см. docs/parallelism.md). Вывод и разбор совместимы с python-версией:
// серверный парсер и отчёты не отличают один режим от другого.
func (r *ProbeRunner) executeEmbeddedTwampy(
	ctx context.Context, task *TaskInfo, node string, args []string) ActionData {

	// Аргументы python-вызова («-m twampy sender <узел> …») приводим к виду
	// самого отправителя: разбор опций у него общий с оригиналом.
	senderArgs := args
	if idx := indexOf(senderArgs, "sender"); idx >= 0 {
		senderArgs = senderArgs[idx+1:]
	}

	callLine := "twampy(embedded) sender " + strings.Join(senderArgs, " ")
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

	output, errText := runEmbeddedTwampy(runCtx, senderArgs, deadline)

	// Отмена именно этого замера (задачу удалили), а не остановка всей пробы.
	cancelled := runCtx.Err() != nil && ctx.Err() == nil
	timedOut := strings.Contains(errText, errTimeout.Error())

	switch {
	case cancelled:
		logRunner.Warn("Задача удалена — выполнение прервано",
			"название", task.Title, "узел", node, "режим", task.Mode,
			"длительность", time.Since(started).Round(time.Millisecond), "задача", task.Id)
		r.registry.ReportOutcome(task.Id, OutcomeNotStarted, nil, "Задача удалена — выполнение прервано")
		result.Cancelled = true
		return result

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

	exitCode := 0
	summary := errText
	if outcome == OutcomeSuccess {
		summary = lastLine(output)
	}
	r.registry.ReportOutcome(task.Id, outcome, &exitCode, summary)

	result.ExitCode = &exitCode
	result.Outcome = string(outcome)
	result.Console = output
	result.ErrorConsole = errText

	logRun(task, node, outcome, exitCode, time.Since(started), summary)
	return result
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
	ctx context.Context, task *TaskInfo, node, execName string, args, env []string) ActionData {

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
		// Зонд не запустился (например, утилита не установлена) — ошибка обязана
		// дойти до сервера как результат, иначе задача выглядит «молча пропавшей».
		message := fmt.Sprintf("Не удалось запустить зонд «%s»: %v", execName, err)
		logRunner.Error("Задача: зонд не запустился",
			"название", task.Title, "узел", node, "режим", task.Mode,
			"команда", callLine, "ошибка", err, "задача", task.Id)
		r.registry.ReportOutcome(task.Id, OutcomeStartFailed, nil, message)
		result.Outcome = string(OutcomeStartFailed)
		result.ErrorConsole = message
		return result
	}

	waitErr := cmd.Wait()
	timedOut := runCtx.Err() == context.DeadlineExceeded
	// Отмена именно этого запуска (задачу удалили), а не остановка всей пробы.
	cancelled := runCtx.Err() == context.Canceled && ctx.Err() == nil
	exitCode := cmd.ProcessState.ExitCode()

	if cancelled {
		// Задачи больше нет — результат никому не нужен: не отправляем его серверу,
		// иначе в отчёте появится обрывок замера по уже удалённой задаче.
		logRunner.Warn("Задача удалена — выполнение прервано",
			"название", task.Title, "узел", node, "режим", task.Mode,
			"длительность", time.Since(started).Round(time.Millisecond), "задача", task.Id)
		r.registry.ReportOutcome(task.Id, OutcomeNotStarted, nil, "Задача удалена — выполнение прервано")
		result.Outcome = string(OutcomeNotStarted)
		result.ErrorConsole = "Задача удалена — выполнение прервано"
		result.Cancelled = true
		return result
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
	r.registry.ReportOutcome(task.Id, outcome, &exitCode, summary)

	result.ExitCode = &exitCode
	result.Outcome = string(outcome)
	result.Console = output
	result.ErrorConsole = errText

	logRun(task, node, outcome, exitCode, time.Since(started), summary)
	return result
}

// logRun пишет итог одного прогона зонда: успех — Info, нештатный исход — Warn.
// По записи видно, когда, какая задача и по какому узлу отработала и с каким результатом.
func logRun(task *TaskInfo, node string, outcome RunOutcome, exitCode int, elapsed time.Duration, summary string) {
	fields := []any{
		"название", task.Title,
		"узел", node,
		"режим", task.Mode,
		"исход", outcome,
		"код", exitCode,
		"длительность", elapsed.Round(time.Millisecond),
		"задача", task.Id,
	}
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
