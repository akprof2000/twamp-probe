// Тесты конфигурации: формат appsettings.json C#-пробы, BOM, значения по умолчанию.
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
	if cfg.MaxParallel != 1024 || cfg.PersistIntervalSec != 5 || cfg.ServerTimeoutHours != 24 {
		t.Errorf("значения по умолчанию: %+v", cfg)
	}
}
