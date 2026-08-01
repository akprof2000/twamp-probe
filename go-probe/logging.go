// Логирование пробы: структурированный журнал (log/slog) с выводом в консоль
// и в файл с ротацией по размеру и сжатием архивов.
//
// Почему своя ротация, а не внешняя библиотека: у пробы одна зависимость
// (cron), и она собирается в один статический бинарник — тянуть ради ротации
// ещё один пакет невыгодно, задача решается сотней строк.
//
// Формат записи повторяет журнал сервера, чтобы обе части читались одинаково:
//
//	2026-07-24 09:48:23.123 INFO  [dispatcher] Запущено воркеров workers=256
package main

import (
	"compress/gzip"
	"context"
	"fmt"
	"io"
	"log"
	"log/slog"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

// LogConfig — настройки журнала (секция Logging в appsettings.json).
type LogConfig struct {
	Level     string // Trace/Debug/Info/Warn/Error — порог записи
	Dir       string // каталог файлов журнала
	FileName  string // имя текущего файла
	MaxSizeMb int    // размер, после которого файл уходит в архив
	MaxFiles  int    // сколько архивов хранить (старые удаляются)
	Console   bool   // дублировать вывод в консоль
	Compress  bool   // сжимать архивы в .gz
}

// parseLevel переводит имя уровня в порог slog. Trace — «всё подряд»
// (у slog нет такого уровня, берём на ступень ниже Debug).
func parseLevel(name string) slog.Level {
	switch strings.ToLower(strings.TrimSpace(name)) {
	case "trace":
		return slog.LevelDebug - 4
	case "debug":
		return slog.LevelDebug
	case "warn", "warning":
		return slog.LevelWarn
	case "error", "fatal":
		return slog.LevelError
	default:
		return slog.LevelInfo
	}
}

// levelName — короткое имя уровня фиксированной ширины (колонки не «пляшут»).
func levelName(level slog.Level) string {
	switch {
	case level < slog.LevelDebug:
		return "TRACE"
	case level < slog.LevelInfo:
		return "DEBUG"
	case level < slog.LevelWarn:
		return "INFO "
	case level < slog.LevelError:
		return "WARN "
	default:
		return "ERROR"
	}
}

// textHandler — обработчик slog, пишущий человекочитаемые строки.
// Стандартные TextHandler/JSONHandler дают logfmt/JSON: машинам удобно,
// дежурному инженеру — нет, а этот журнал читают люди.
type textHandler struct {
	mu     *sync.Mutex
	out    io.Writer
	level  slog.Leveler
	group  string      // компонент: dispatcher, runner, registry…
	fields []slog.Attr // атрибуты, добавленные через With
}

// Enabled сообщает, писать ли запись данного уровня.
func (h *textHandler) Enabled(_ context.Context, level slog.Level) bool {
	return level >= h.level.Level()
}

// WithAttrs возвращает обработчик с дополнительными постоянными атрибутами.
func (h *textHandler) WithAttrs(attrs []slog.Attr) slog.Handler {
	clone := *h
	clone.fields = append(append([]slog.Attr{}, h.fields...), attrs...)
	return &clone
}

// WithGroup помечает записи именем компонента.
func (h *textHandler) WithGroup(name string) slog.Handler {
	clone := *h
	clone.group = name
	return &clone
}

// Handle форматирует и записывает одну строку журнала.
func (h *textHandler) Handle(_ context.Context, record slog.Record) error {
	var sb strings.Builder
	sb.WriteString(record.Time.Format("2006-01-02 15:04:05.000"))
	sb.WriteByte(' ')
	sb.WriteString(levelName(record.Level))
	if h.group != "" {
		sb.WriteString(" [")
		sb.WriteString(h.group)
		sb.WriteByte(']')
	}
	sb.WriteByte(' ')
	// Переносы строк в сообщении ломают построчный разбор журнала
	// (вывод зонда бывает многострочным) — сворачиваем их.
	sb.WriteString(strings.ReplaceAll(strings.TrimSpace(record.Message), "\n", " ⏎ "))

	for _, attr := range h.fields {
		writeAttr(&sb, attr)
	}
	record.Attrs(func(attr slog.Attr) bool {
		writeAttr(&sb, attr)
		return true
	})
	sb.WriteByte('\n')

	h.mu.Lock()
	defer h.mu.Unlock()
	_, err := io.WriteString(h.out, sb.String())
	return err
}

// writeAttr дописывает пару «ключ=значение»; значения с пробелами берутся в кавычки.
func writeAttr(sb *strings.Builder, attr slog.Attr) {
	if attr.Equal(slog.Attr{}) {
		return
	}
	value := strings.ReplaceAll(strings.TrimSpace(attr.Value.String()), "\n", " ⏎ ")
	sb.WriteByte(' ')
	sb.WriteString(attr.Key)
	sb.WriteByte('=')
	if strings.ContainsAny(value, " \t=") {
		fmt.Fprintf(sb, "%q", value)
		return
	}
	sb.WriteString(value)
}

// rotatingFile — файл журнала с ротацией по размеру.
// Достигнув предела, текущий файл переименовывается в архив с меткой времени
// (при Compress — сжимается), после чего старые архивы сверх MaxFiles удаляются.
type rotatingFile struct {
	mu       sync.Mutex
	path     string
	maxSize  int64
	maxFiles int
	compress bool
	file     *os.File
	size     int64
}

// newRotatingFile открывает (или создаёт) файл журнала и готовит ротацию.
func newRotatingFile(cfg LogConfig) (*rotatingFile, error) {
	if err := os.MkdirAll(cfg.Dir, 0o755); err != nil {
		return nil, fmt.Errorf("не удалось создать каталог журнала %s: %w", cfg.Dir, err)
	}

	r := &rotatingFile{
		path:     filepath.Join(cfg.Dir, cfg.FileName),
		maxSize:  int64(cfg.MaxSizeMb) * 1024 * 1024,
		maxFiles: cfg.MaxFiles,
		compress: cfg.Compress,
	}
	if err := r.open(); err != nil {
		return nil, err
	}
	return r, nil
}

// open открывает текущий файл на дозапись и запоминает его размер.
func (r *rotatingFile) open() error {
	file, err := os.OpenFile(r.path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return fmt.Errorf("не удалось открыть файл журнала %s: %w", r.path, err)
	}

	info, err := file.Stat()
	if err != nil {
		_ = file.Close()
		return fmt.Errorf("не удалось получить размер файла журнала: %w", err)
	}

	r.file = file
	r.size = info.Size()
	return nil
}

// Write записывает порцию журнала, при необходимости выполняя ротацию.
func (r *rotatingFile) Write(p []byte) (int, error) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.maxSize > 0 && r.size+int64(len(p)) > r.maxSize {
		// Сбой ротации не должен ронять запись в журнал: сообщаем и пишем дальше.
		if err := r.rotate(); err != nil {
			fmt.Fprintf(os.Stderr, "ротация журнала не удалась: %v\n", err)
		}
	}

	n, err := r.file.Write(p)
	r.size += int64(n)
	return n, err
}

// rotate закрывает текущий файл, отправляет его в архив и открывает новый.
func (r *rotatingFile) rotate() error {
	if err := r.file.Close(); err != nil {
		return err
	}

	archive := fmt.Sprintf("%s.%s", r.path, time.Now().Format("20060102-150405"))
	if err := os.Rename(r.path, archive); err != nil {
		_ = r.open() // файл переименовать не вышло — продолжаем писать в него же
		return err
	}

	if err := r.open(); err != nil {
		return err
	}

	if r.compress {
		if err := compressFile(archive); err != nil {
			fmt.Fprintf(os.Stderr, "сжатие архива журнала не удалось: %v\n", err)
		}
	}
	r.cleanup()
	return nil
}

// cleanup удаляет архивы сверх maxFiles, начиная с самых старых.
func (r *rotatingFile) cleanup() {
	if r.maxFiles <= 0 {
		return
	}

	matches, err := filepath.Glob(r.path + ".*")
	if err != nil || len(matches) <= r.maxFiles {
		return
	}

	// Имя архива содержит метку времени, поэтому лексикографический порядок
	// совпадает с хронологическим.
	sort.Strings(matches)
	for _, old := range matches[:len(matches)-r.maxFiles] {
		_ = os.Remove(old)
	}
}

// compressFile сжимает архив в .gz и удаляет исходный файл.
func compressFile(path string) error {
	source, err := os.Open(path)
	if err != nil {
		return err
	}
	defer source.Close()

	target, err := os.Create(path + ".gz")
	if err != nil {
		return err
	}
	defer target.Close()

	writer := gzip.NewWriter(target)
	if _, err := io.Copy(writer, source); err != nil {
		_ = writer.Close()
		return err
	}
	if err := writer.Close(); err != nil {
		return err
	}

	_ = source.Close()
	return os.Remove(path)
}

// Close закрывает файл журнала.
func (r *rotatingFile) Close() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.file.Close()
}

// SetupLogging настраивает журнал: консоль и/или файл с ротацией.
// Возвращает функцию закрытия файла — её вызывает main при остановке.
func SetupLogging(cfg LogConfig) (func(), error) {
	writers := []io.Writer{}
	if cfg.Console {
		writers = append(writers, os.Stdout)
	}

	closer := func() {}
	if cfg.Dir != "" && cfg.FileName != "" {
		file, err := newRotatingFile(cfg)
		if err != nil {
			return closer, err
		}
		writers = append(writers, file)
		closer = func() { _ = file.Close() }
	}

	// Без единого приёмника журнал всё равно нужен — иначе потеряются ошибки старта.
	if len(writers) == 0 {
		writers = append(writers, os.Stderr)
	}

	level := new(slog.LevelVar)
	level.Set(parseLevel(cfg.Level))

	handler := &textHandler{mu: &sync.Mutex{}, out: io.MultiWriter(writers...), level: level}
	logger := slog.New(handler)
	slog.SetDefault(logger)

	// Пакет log используют сторонние библиотеки (в т.ч. http.Server для ошибок
	// соединений) — заворачиваем его вывод в тот же журнал, чтобы ничего не терялось.
	log.SetFlags(0)
	log.SetOutput(&stdlogBridge{logger: logger.With(slog.String("источник", "runtime"))})

	return closer, nil
}

// stdlogBridge переводит записи пакета log в slog (уровень Warn:
// стандартный логгер в этом приложении используется только для нештатных ситуаций).
type stdlogBridge struct{ logger *slog.Logger }

// Write передаёт строку стандартного логгера в структурированный журнал.
func (b *stdlogBridge) Write(p []byte) (int, error) {
	b.logger.Warn(strings.TrimSpace(string(p)))
	return len(p), nil
}

// Component возвращает логгер компонента: его имя попадает в каждую запись.
func Component(name string) *slog.Logger { return slog.Default().WithGroup(name) }
