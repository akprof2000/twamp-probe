package main

import (
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// testLogConfig — журнал во временном каталоге, без вывода в консоль.
func testLogConfig(dir string, maxSizeMb, maxFiles int, compress bool) LogConfig {
	return LogConfig{
		Level:     "Debug",
		Dir:       dir,
		FileName:  "probe.log",
		MaxSizeMb: maxSizeMb,
		MaxFiles:  maxFiles,
		Console:   false,
		Compress:  compress,
	}
}

func TestParseLevel(t *testing.T) {
	cases := map[string]slog.Level{
		"trace":   slog.LevelDebug - 4,
		"Debug":   slog.LevelDebug,
		"INFO":    slog.LevelInfo,
		"warning": slog.LevelWarn,
		"Error":   slog.LevelError,
		"чепуха":  slog.LevelInfo, // неизвестное имя — безопасный Info
	}
	for name, want := range cases {
		if got := parseLevel(name); got != want {
			t.Errorf("parseLevel(%q) = %v, ожидалось %v", name, got, want)
		}
	}
}

func TestSetupLogging_WritesFile(t *testing.T) {
	dir := t.TempDir()
	closeLog, err := SetupLogging(testLogConfig(dir, 10, 20, false))
	if err != nil {
		t.Fatalf("SetupLogging: %v", err)
	}

	Component("registry").Info("Задача добавлена", "задача", "abc-123", "название", "тест зонда")
	closeLog()

	data, err := os.ReadFile(filepath.Join(dir, "probe.log"))
	if err != nil {
		t.Fatalf("файл журнала не создан: %v", err)
	}
	line := string(data)

	for _, want := range []string{"INFO", "[registry]", "Задача добавлена", "задача=abc-123"} {
		if !strings.Contains(line, want) {
			t.Errorf("в журнале нет %q; строка: %s", want, line)
		}
	}
	// Значение с пробелом обязано быть в кавычках, иначе разбор строки неоднозначен.
	if !strings.Contains(line, `название="тест зонда"`) {
		t.Errorf("значение с пробелом не взято в кавычки; строка: %s", line)
	}
}

func TestSetupLogging_RespectsLevel(t *testing.T) {
	dir := t.TempDir()
	cfg := testLogConfig(dir, 10, 20, false)
	cfg.Level = "Warn"

	closeLog, err := SetupLogging(cfg)
	if err != nil {
		t.Fatalf("SetupLogging: %v", err)
	}
	Component("test").Info("это не должно попасть в журнал")
	Component("test").Warn("а это должно")
	closeLog()

	data, _ := os.ReadFile(filepath.Join(dir, "probe.log"))
	text := string(data)
	if strings.Contains(text, "не должно попасть") {
		t.Error("запись ниже порога уровня попала в журнал")
	}
	if !strings.Contains(text, "а это должно") {
		t.Error("запись уровня Warn потеряна")
	}
}

func TestSetupLogging_MultilineMessageStaysOneLine(t *testing.T) {
	dir := t.TempDir()
	closeLog, err := SetupLogging(testLogConfig(dir, 10, 20, false))
	if err != nil {
		t.Fatalf("SetupLogging: %v", err)
	}
	// Вывод зонда бывает многострочным — журнал обязан остаться построчным.
	Component("runner").Error("Зонд не запустился", "вывод", "первая строка\nвторая строка")
	closeLog()

	data, _ := os.ReadFile(filepath.Join(dir, "probe.log"))
	lines := strings.Split(strings.TrimSpace(string(data)), "\n")
	if len(lines) != 1 {
		t.Errorf("ожидалась одна строка журнала, получено %d: %q", len(lines), string(data))
	}
}

func TestRotatingFile_RotatesAndKeepsLimit(t *testing.T) {
	dir := t.TempDir()
	cfg := testLogConfig(dir, 1, 2, false) // 1 МБ на файл, хранить 2 архива
	file, err := newRotatingFile(cfg)
	if err != nil {
		t.Fatalf("newRotatingFile: %v", err)
	}
	defer file.Close()

	// Пишем заведомо больше трёх мегабайт — ротация обязана сработать несколько раз.
	chunk := make([]byte, 256*1024)
	for i := range chunk {
		chunk[i] = 'x'
	}
	for range 16 {
		if _, err := file.Write(chunk); err != nil {
			t.Fatalf("запись в журнал: %v", err)
		}
	}

	archives, _ := filepath.Glob(filepath.Join(dir, "probe.log.*"))
	if len(archives) == 0 {
		t.Fatal("ротация не создала ни одного архива")
	}
	if len(archives) > cfg.MaxFiles {
		t.Errorf("архивов %d, лимит %d — старые не удаляются", len(archives), cfg.MaxFiles)
	}
	if _, err := os.Stat(filepath.Join(dir, "probe.log")); err != nil {
		t.Errorf("после ротации нет текущего файла журнала: %v", err)
	}
}

func TestRotatingFile_CompressesArchives(t *testing.T) {
	dir := t.TempDir()
	file, err := newRotatingFile(testLogConfig(dir, 1, 5, true))
	if err != nil {
		t.Fatalf("newRotatingFile: %v", err)
	}
	defer file.Close()

	chunk := make([]byte, 512*1024)
	for range 4 {
		if _, err := file.Write(chunk); err != nil {
			t.Fatalf("запись в журнал: %v", err)
		}
	}

	gzipped, _ := filepath.Glob(filepath.Join(dir, "*.gz"))
	if len(gzipped) == 0 {
		t.Error("архивы не сжимаются при Compress=true")
	}
}

func TestLoadConfig_LoggingDefaults(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "appsettings.json")
	// Секции Logging нет — обязаны примениться значения по умолчанию.
	if err := os.WriteFile(path, []byte(`{"Urls":"http://0.0.0.0:8443"}`), 0o644); err != nil {
		t.Fatal(err)
	}

	cfg, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.Log.Level != "Info" || cfg.Log.FileName != "probe.log" || cfg.Log.Dir != "log" {
		t.Errorf("умолчания журнала не применились: %+v", cfg.Log)
	}
	if cfg.Log.MaxSizeMb != 10 || cfg.Log.MaxFiles != 20 || !cfg.Log.Console || !cfg.Log.Compress {
		t.Errorf("умолчания ротации не применились: %+v", cfg.Log)
	}
}

func TestLoadConfig_LoggingFromFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "appsettings.json")
	content := `{"Logging":{"Level":"Debug","Dir":"логи","FileName":"p.log",
		"MaxSizeMb":5,"MaxFiles":3,"Console":false,"Compress":false}}`
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}

	cfg, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.Log.Level != "Debug" || cfg.Log.Dir != "логи" || cfg.Log.MaxSizeMb != 5 ||
		cfg.Log.MaxFiles != 3 || cfg.Log.Console || cfg.Log.Compress {
		t.Errorf("настройки журнала прочитаны неверно: %+v", cfg.Log)
	}
}
