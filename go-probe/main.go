// SPI TWamp Probe — проба системы SPI TWamp.
//
// Работает с сервером SPI.Twamp.Server: те же эндпоинты
// api/probeinterface (CheckIn, SetJobs, TaskIds, TaskStatus, CheckData,
// ConfirmData), тот же контракт JSON, та же механика ACK-доставки результатов,
// инкрементального слияния задач и cron-планирования. Конфигурация — тот же
// appsettings.json. Один статический бинарник, снаружи нужны только
// измерительные утилиты (twping / python3+twampy / ping).
package main

import (
	"cmp"
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"time"
)

// probeVersion отображается в списке проб на сервере (поле Version в CheckIn).
// Релизные сборки прошивают версию тега через ldflags:
//
//	go build -ldflags "-X main.probeVersion=1.2.0-go"
var probeVersion = "1.2.0-go-dev"

// Логгеры компонентов: имя компонента попадает в каждую запись журнала,
// поэтому по строке сразу видно, какая часть пробы её написала.
var (
	logMain       = Component("main")
	logHTTP       = Component("http")
	logDispatcher = Component("dispatcher")
	logRegistry   = Component("registry")
	logResults    = Component("results")
	logRunner     = Component("runner")
	logWatchdog   = Component("watchdog")
)

func main() {
	// Служба Windows стартует в System32 — сначала переходим к своим файлам,
	// иначе не найдётся даже appsettings.json.
	prepareServiceEnvironment()

	// Конфигурация читается до настройки журнала (в ней его параметры),
	// поэтому об ошибке на этом шаге сообщаем напрямую в stderr.
	cfg, err := LoadConfig("appsettings.json")
	if err != nil {
		fmt.Fprintln(os.Stderr, "Ошибка конфигурации:", err)
		os.Exit(1)
	}

	closeLog, err := SetupLogging(cfg.Log)
	if err != nil {
		fmt.Fprintln(os.Stderr, "Не удалось настроить журнал:", err)
		os.Exit(1)
	}
	defer closeLog()

	// Логгеры компонентов созданы до SetupLogging и держат прежний обработчик —
	// пересоздаём их поверх настроенного журнала.
	initComponentLoggers()

	// Число одновременных замеров задаёт только настройка: проба поднимает под
	// неё свои лимиты (мягкий RLIMIT_NPROC и предел потоков Go), а если система
	// к такому числу не готова — предупреждает, но значение не меняет. Отказ
	// ядра виден сразу и правится настройкой системы; молча работать вполсилы
	// хуже.
	if warning := prepareLimits(cfg.MaxParallel); warning != "" {
		logMain.Warn("Система может не выдержать настроенное число замеров",
			"настроено", cfg.MaxParallel, "причина", warning)
	}

	logMain.Info("Проба запускается",
		"версия", probeVersion,
		"адрес", cfg.ListenAddr,
		"воркеров", cfg.MaxParallel,
		"очередь_задач", cfg.QueueCapacity,
		"очередь_результатов", cfg.MaxPendingResults,
		"уровень_журнала", cfg.Log.Level,
		"журнал", filepath.Join(cfg.Log.Dir, cfg.Log.FileName))

	// Остановка приходит по-разному: сигналом на Unix и командой диспетчера
	// служб на Windows. Платформенная обёртка прячет эту разницу.
	ctx, stop := shutdownContext()
	defer stop()
	// Пока проба обрывает зонды и сохраняет результаты, службе Windows рано
	// сообщать диспетчеру, что она остановлена.
	defer markProbeFinished()

	results := NewResultStore(cfg.MaxPendingResults, cfg.PersistIntervalSec)
	results.Load()
	runReg := NewRunRegistry()
	cancels := NewRunCancelRegistry()
	runner := NewProbeRunner(cfg, results, runReg, cancels)
	// Предел одновременных запусков: настройка задаёт потолок, память — фактическое
	// значение. Стартуем с малого и разгоняемся, а не запускаем всё разом.
	limiter := NewAdaptiveLimiter(cfg.MaxParallel, cfg.MinParallel, cfg.StartParallel)
	defer limiter.Close()
	go RunMemoryGuard(ctx, limiter, MemoryGuardConfig{
		HighPercent: cfg.MemoryHighPercent,
		LowPercent:  cfg.MemoryLowPercent,
		Interval:    time.Duration(cfg.MemoryCheckSec) * time.Second,
	})

	dispatcher := NewDispatcher(ctx, cfg.MaxParallel, cfg.QueueCapacity, runner, runReg, limiter)
	tasks := NewTaskRegistry(dispatcher, runReg, cancels)
	tasks.Load()

	// Сторож связи: молчание сервера дольше Probe:ServerTimeoutHours означает,
	// что пробу удалили, — задачи останавливаются, реестр и кэш чистятся.
	tracker := NewContactTracker()
	go RunWatchdog(ctx, cfg.ServerTimeoutHours, tracker, tasks, results)

	api := &apiServer{cfg: cfg, results: results, tasks: tasks, runReg: runReg, tracker: tracker}
	server := &http.Server{Addr: cfg.ListenAddr, Handler: api.routes()}

	go func() {
		logHTTP.Info("HTTP-сервер слушает", "адрес", cfg.ListenAddr,
			"аутентификация", cfg.ApiKey != "")
		if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			logHTTP.Error("HTTP-сервер остановлен с ошибкой", "ошибка", err)
			closeLog()
			os.Exit(1)
		}
	}()

	<-ctx.Done()
	logMain.Info("Получен сигнал остановки — завершаем работу")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = server.Shutdown(shutdownCtx) // новых задач с сервера больше не принимаем

	// Зонды — это отдельные процессы. Если просто выйти, они осиротеют и
	// продолжат работать: замер «twping -c 300» живёт минутами, и после
	// перезапуска службы такие процессы копятся, занимая память и порты.
	// Поэтому обрываем их явно и дожидаемся, пока ядро их снимет.
	limiter.Close() // ожидающие воркеры расходятся, новые запуски не стартуют
	if stopped := cancels.CancelAll(); stopped > 0 {
		logMain.Info("Прерываем запущенные зонды", "запусков", stopped)
	}
	waitForRuns(limiter, shutdownWait)

	results.Close() // финальный снимок недоставленных результатов
	logMain.Info("Проба остановлена")
}

// shutdownWait — сколько ждать завершения зондов при остановке пробы.
const shutdownWait = 10 * time.Second

// waitForRuns дожидается, пока прерванные зонды действительно завершатся.
// Сигнал послан мгновенно, но снятие процессов ядром занимает время, и выйти
// раньше — значит снова оставить сирот.
func waitForRuns(limiter *AdaptiveLimiter, timeout time.Duration) {
	deadline := time.Now().Add(timeout)
	for {
		left := limiter.InFlight()
		if left == 0 {
			return
		}
		if time.Now().After(deadline) {
			logMain.Warn("Часть зондов не завершилась за отведённое время",
				"осталось", left, "ждали", timeout)
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
}

// initComponentLoggers пересоздаёт логгеры компонентов после настройки журнала.
func initComponentLoggers() {
	logMain = Component("main")
	logHTTP = Component("http")
	logDispatcher = Component("dispatcher")
	logRegistry = Component("registry")
	logResults = Component("results")
	logRunner = Component("runner")
	logWatchdog = Component("watchdog")
}

// apiServer — HTTP-обработчики эндпоинтов api/probeinterface.
type apiServer struct {
	cfg     *Config
	results *ResultStore
	tasks   *TaskRegistry
	runReg  *RunRegistry
	tracker *ContactTracker
}

// routes собирает маршруты. ASP.NET сопоставляет пути регистронезависимо,
// поэтому путь приводится к нижнему регистру перед выбором обработчика.
func (a *apiServer) routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/probeinterface/checkin", a.checkIn)
	mux.HandleFunc("POST /api/probeinterface/setjobs", a.setJobs)
	mux.HandleFunc("GET /api/probeinterface/taskids", a.taskIds)
	mux.HandleFunc("GET /api/probeinterface/tasks", a.tasksFull)
	mux.HandleFunc("GET /api/probeinterface/taskstatus", a.taskStatus)
	mux.HandleFunc("GET /api/probeinterface/checkdata", a.checkData)
	mux.HandleFunc("POST /api/probeinterface/confirmdata", a.confirmData)

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		r.URL.Path = strings.ToLower(r.URL.Path)
		a.tracker.Mark() // отметка «сервер выходил на связь» — для сторожа

		// Аутентификация по общему ключу — включается, когда задан Auth:ApiKey.
		if a.cfg.ApiKey != "" && r.Header.Get("X-Api-Key") != a.cfg.ApiKey {
			http.Error(w, "Недопустимый ключ API", http.StatusUnauthorized)
			return
		}
		mux.ServeHTTP(w, r)
	})
}

// writeJSON сериализует ответ в JSON (camelCase — как ожидает сервер).
func writeJSON(w http.ResponseWriter, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(value)
}

// queryParam читает параметр строки запроса без учёта регистра имени:
// ASP.NET сопоставляет query-параметры регистронезависимо, и сервер шлёт,
// например, «RequestInfo=…» — Go-проба обязана понимать любой регистр.
func queryParam(r *http.Request, name string) string {
	for key, values := range r.URL.Query() {
		if strings.EqualFold(key, name) && len(values) > 0 {
			return values[0]
		}
	}
	return ""
}

// checkIn возвращает паспорт пробы: адреса, имя хоста, версию.
func (a *apiServer) checkIn(w http.ResponseWriter, r *http.Request) {
	requestInfo := queryParam(r, "requestInfo")
	logHTTP.Info("CheckIn от сервера", "сервер", requestInfo, "версия_пробы", probeVersion)

	ip, mac, ifaceName := firstInterface()
	host, _ := os.Hostname()
	writeJSON(w, Identify{
		IPAddress:   ip,
		HostName:    host,
		MacAddress:  mac,
		Title:       ifaceName,
		Description: ifaceName,
		RequestInfo: requestInfo,
		Version:     probeVersion,
	})
}

// setJobs принимает изменившиеся задачи и сливает их в реестр.
func (a *apiServer) setJobs(w http.ResponseWriter, r *http.Request) {
	var jobs []TaskInfo
	if err := json.NewDecoder(r.Body).Decode(&jobs); err != nil {
		http.Error(w, "Некорректное тело запроса: "+err.Error(), http.StatusBadRequest)
		return
	}
	logHTTP.Info("SetJobs: получены изменения задач", "задач", len(jobs))
	a.tasks.MergeJobs(jobs)
	w.WriteHeader(http.StatusOK)
}

// taskIds возвращает идентификаторы задач по расписанию (для сверки).
func (a *apiServer) taskIds(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, a.tasks.KnownTaskIds())
}

// tasksFull возвращает полные определения задач по расписанию (для восстановления
// сервера после потери данных).
func (a *apiServer) tasksFull(w http.ResponseWriter, _ *http.Request) {
	tasks := a.tasks.AllTasks()
	if tasks == nil {
		tasks = []TaskInfo{}
	}
	writeJSON(w, tasks)
}

// taskStatus возвращает состояние выполнения задач с фильтрами и пагинацией.
func (a *apiServer) taskStatus(w http.ResponseWriter, r *http.Request) {
	skip, _ := strconv.Atoi(queryParam(r, "skip"))
	take, err := strconv.Atoi(queryParam(r, "take"))
	if err != nil || take <= 0 {
		take = 100
	}
	take = min(take, 500) // встроенный min — Go 1.21
	title := queryParam(r, "title")
	outcome := queryParam(r, "outcome")

	all := a.runReg.GetAll()
	filtered := make([]TaskRunInfo, 0, len(all))
	for _, t := range all {
		if title != "" && !strings.Contains(strings.ToLower(t.Title), strings.ToLower(title)) {
			continue
		}
		if outcome != "" {
			isRunning := strings.EqualFold(outcome, "Running") && t.Running > 0
			if !isRunning && !strings.EqualFold(string(t.LastOutcome), outcome) {
				continue
			}
		}
		filtered = append(filtered, t)
	}

	// Сначала выполняющиеся и проблемные, затем по названию.
	// slices.SortFunc + cmp.Compare — Go 1.21.
	bad := func(o RunOutcome) int {
		if o == OutcomeExitCodeError || o == OutcomeStartFailed || o == OutcomeTimedOut {
			return 1
		}
		return 0
	}
	slices.SortFunc(filtered, func(x, y TaskRunInfo) int {
		if c := cmp.Compare(y.Running, x.Running); c != 0 {
			return c
		}
		if c := cmp.Compare(bad(y.LastOutcome), bad(x.LastOutcome)); c != 0 {
			return c
		}
		return strings.Compare(strings.ToLower(x.Title), strings.ToLower(y.Title))
	})

	skip = min(max(skip, 0), len(filtered)) // встроенные min/max — Go 1.21
	end := min(skip+take, len(filtered))
	writeJSON(w, map[string]any{"total": len(filtered), "items": filtered[skip:end]})
}

// checkData выдаёт пачку результатов длинным опросом (до 30 секунд).
func (a *apiServer) checkData(w http.ResponseWriter, _ *http.Request) {
	batch := a.results.TakeBatch(30 * time.Second)
	writeJSON(w, batch)
}

// confirmData подтверждает запись пачки сервером — проба удаляет её.
func (a *apiServer) confirmData(w http.ResponseWriter, r *http.Request) {
	batchId := queryParam(r, "batchId")
	confirmed := a.results.Confirm(strings.ToLower(batchId))
	writeJSON(w, confirmed)
}

// firstInterface возвращает адрес, MAC и имя первого активного не-loopback интерфейса.
func firstInterface() (ip, mac, name string) {
	ip, mac, name = "0.0.0.0", "00:00:00:00:00:00", ""
	ifaces, err := net.Interfaces()
	if err != nil {
		return
	}
	for _, iface := range ifaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, err := iface.Addrs()
		if err != nil {
			continue
		}
		for _, addr := range addrs {
			ipNet, ok := addr.(*net.IPNet)
			if !ok || ipNet.IP.To4() == nil {
				continue
			}
			return ipNet.IP.String(), iface.HardwareAddr.String(), iface.Name
		}
	}
	return
}
