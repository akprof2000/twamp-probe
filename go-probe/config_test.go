// Тесты конфигурации: формат appsettings.json, BOM, значения по умолчанию.
package main

import (
	"os"
	"path/filepath"
	"testing"
)

// Файл с UTF-8 BOM (типичный для Windows) разбирается, значения достаются по секциям.
func TestLoadConfig_WithBom(t *testing.T) {
	path := filepath.Join(t.TempDir(), "appsettings.json")
	content := "\uFEFF" + `{
		"Urls": "http://0.0.0.0:9999",
		"Auth": {"ApiKey": "secret"},
		"Probe": {"MaxParallel": 42, "ServerTimeoutHours": 7},
		"twampy": {"name": "python"}
	}`
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}

	cfg, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("разбор конфига: %v", err)
	}
	if cfg.ListenAddr != "0.0.0.0:9999" {
		t.Errorf("ListenAddr: %s", cfg.ListenAddr)
	}
	if cfg.ApiKey != "secret" || cfg.MaxParallel != 42 || cfg.ServerTimeoutHours != 7 {
		t.Errorf("значения не разобраны: %+v", cfg)
	}
	if cfg.Twampy.Name != "python" {
		t.Errorf("twampy:name: %s", cfg.Twampy.Name)
	}
}

// Отсутствующие ключи получают значения по умолчанию.
func TestLoadConfig_Defaults(t *testing.T) {
	path := filepath.Join(t.TempDir(), "appsettings.json")
	if err := os.WriteFile(path, []byte(`{}`), 0o644); err != nil {
		t.Fatal(err)
	}

	cfg, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("разбор пустого конфига: %v", err)
	}
	if cfg.ListenAddr != "0.0.0.0:8443" {
		t.Errorf("порт по умолчанию должен быть 8443: %s", cfg.ListenAddr)
	}
	// MaxParallel по умолчанию (0 в конфиге) — значение из константы, без
	// подгонки под число ядер или лимиты системы.
	if cfg.MaxParallel != defaultMaxParallel {
		t.Errorf("MaxParallel по умолчанию = %d, ожидалось %d", cfg.MaxParallel, defaultMaxParallel)
	}
	if cfg.PersistIntervalSec != 5 || cfg.ServerTimeoutHours != 24 {
		t.Errorf("значения по умолчанию: %+v", cfg)
	}
}

func TestLoadConfig_TwampyEmbeddedFlag(t *testing.T) {
	// Встроенный режим включается из того же appsettings.json, что и всё остальное.
	dir := t.TempDir()
	path := filepath.Join(dir, "appsettings.json")

	if err := os.WriteFile(path, []byte(`{"twampy":{"name":"python3","embedded":true}}`), 0o644); err != nil {
		t.Fatalf("не удалось записать конфигурацию: %v", err)
	}
	cfg, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("конфигурация не прочиталась: %v", err)
	}
	if !cfg.TwampyEmbedded {
		t.Error("twampy:embedded=true не прочитан")
	}

	// По умолчанию режим выключен: обновление пробы не меняет поведение молча.
	if err := os.WriteFile(path, []byte(`{"twampy":{"name":"python3"}}`), 0o644); err != nil {
		t.Fatalf("не удалось записать конфигурацию: %v", err)
	}
	cfg, err = LoadConfig(path)
	if err != nil {
		t.Fatalf("конфигурация не прочиталась: %v", err)
	}
	if cfg.TwampyEmbedded {
		t.Error("без настройки встроенный режим оказался включён")
	}
}
