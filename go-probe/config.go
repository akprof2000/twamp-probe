// Чтение конфигурации из appsettings.json — формат файла тот же, что у C#-пробы,
// поэтому появившийся Go-вариант можно подложить в существующую инсталляцию.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"runtime"
	"strconv"
	"strings"
)

// ProbeToolConfig — настройки одной измерительной утилиты (имя + аргументы по умолчанию).
type ProbeToolConfig struct {
	Name    string
	Default string
}

// Config — настройки Go-пробы (подмножество appsettings.json C#-пробы).
type Config struct {
	ListenAddr         string // из Urls: "http://0.0.0.0:8443" → "0.0.0.0:8443"
	ApiKey             string // Auth:ApiKey; пусто — аутентификация выключена
	MaxParallel        int    // Probe:MaxParallel — размер пула воркеров
	MaxPendingResults  int    // Probe:MaxPendingResults — лимит очереди результатов
	PersistIntervalSec int    // Probe:PersistIntervalSec — период снимка очереди на диск
	ServerTimeoutHours int    // Probe:ServerTimeoutHours — молчание сервера, после которого проба чистит всё (0 — выключено)
	Ping               ProbeToolConfig
	Twamp              ProbeToolConfig
	Twampy             ProbeToolConfig
}

// LoadConfig читает appsettings.json рядом с исполняемым файлом.
func LoadConfig(path string) (*Config, error) {
	raw := map[string]any{}
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("не удалось прочитать %s: %w", path, err)
	}
	// В файле от Windows может быть UTF-8 BOM — json.Unmarshal его не переваривает.
	data = []byte(strings.TrimPrefix(string(data), "\uFEFF"))
	if err := json.Unmarshal(data, &raw); err != nil {
		return nil, fmt.Errorf("не удалось разобрать %s: %w", path, err)
	}

	cfg := &Config{
		ListenAddr:         parseUrls(str(raw, "Urls", "http://0.0.0.0:8443")),
		ApiKey:             str(raw, "Auth:ApiKey", ""),
		MaxParallel:        resolveParallel(num(raw, "Probe:MaxParallel", 0)),
		MaxPendingResults:  num(raw, "Probe:MaxPendingResults", 100000),
		PersistIntervalSec: num(raw, "Probe:PersistIntervalSec", 5),
		ServerTimeoutHours: num(raw, "Probe:ServerTimeoutHours", 24),
		Ping:               ProbeToolConfig{str(raw, "ping:name", "ping"), str(raw, "ping:default", "")},
		Twamp:              ProbeToolConfig{str(raw, "twamp:name", "./twping"), str(raw, "twamp:default", "")},
		Twampy:             ProbeToolConfig{str(raw, "twampy:name", "python3"), str(raw, "twampy:default", "")},
	}
	return cfg, nil
}

// resolveParallel возвращает число воркеров: явное значение (>0) — как есть;
// 0 (или меньше) — автоподбор «ядра × 16» с потолком 1024 и полом 16. Зонды —
// внешние процессы, в основном ждущие I/O (особенно длинный TWAMP), поэтому
// воркеров нужно много; потолок бережёт многоядерные машины.
func resolveParallel(configured int) int {
	if configured > 0 {
		return configured
	}
	return min(max(runtime.NumCPU()*16, 16), 1024) // min/max — Go 1.21
}

// parseUrls выделяет адрес прослушивания из строки Urls ASP.NET ("http://0.0.0.0:8443").
func parseUrls(urls string) string {
	u := strings.Split(urls, ";")[0]
	u = strings.TrimPrefix(u, "http://")
	u = strings.TrimPrefix(u, "https://")
	return strings.TrimSuffix(u, "/")
}

// str достаёт строку по пути «Секция:Ключ» (или значение по умолчанию).
func str(raw map[string]any, path, def string) string {
	if v, ok := dig(raw, path); ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return def
}

// num достаёт целое по пути «Секция:Ключ» (число или строка с числом).
func num(raw map[string]any, path string, def int) int {
	v, ok := dig(raw, path)
	if !ok {
		return def
	}
	switch t := v.(type) {
	case float64:
		return int(t)
	case string:
		if n, err := strconv.Atoi(t); err == nil {
			return n
		}
	}
	return def
}

// dig спускается по вложенным объектам JSON по пути с разделителем «:».
func dig(raw map[string]any, path string) (any, bool) {
	current := any(raw)
	for _, part := range strings.Split(path, ":") {
		obj, ok := current.(map[string]any)
		if !ok {
			return nil, false
		}
		current, ok = obj[part]
		if !ok {
			return nil, false
		}
	}
	return current, true
}
