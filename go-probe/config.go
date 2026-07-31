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
	ListenAddr         string    // из Urls: "http://0.0.0.0:8443" → "0.0.0.0:8443"
	ApiKey             string    // Auth:ApiKey; пусто — аутентификация выключена
	MaxParallel        int       // Probe:MaxParallel — размер пула воркеров
	MaxPendingResults  int       // Probe:MaxPendingResults — лимит очереди результатов
	PersistIntervalSec int       // Probe:PersistIntervalSec — период снимка очереди на диск
	ServerTimeoutHours int       // Probe:ServerTimeoutHours — молчание сервера, после которого проба чистит всё (0 — выключено)
	MinParallel        int       // Probe:MinParallel — ниже этого предел не опускается даже при нехватке памяти
	MemoryHighPercent  float64   // Probe:MemoryHighPercent — выше этой занятости памяти предел сжимается
	MemoryLowPercent   float64   // Probe:MemoryLowPercent — ниже этой занятости предел возвращается
	MemoryCheckSec     int       // Probe:MemoryCheckSec — период проверки памяти (0 — слежение выключено)
	Log                LogConfig // секция Logging — журнал пробы
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
		MinParallel:        num(raw, "Probe:MinParallel", 16),
		MemoryHighPercent:  float64(num(raw, "Probe:MemoryHighPercent", 95)),
		MemoryLowPercent:   float64(num(raw, "Probe:MemoryLowPercent", 80)),
		MemoryCheckSec:     num(raw, "Probe:MemoryCheckSec", 5),
		Log: LogConfig{
			Level:     str(raw, "Logging:Level", "Info"),
			Dir:       str(raw, "Logging:Dir", "log"),
			FileName:  str(raw, "Logging:FileName", "probe.log"),
			MaxSizeMb: num(raw, "Logging:MaxSizeMb", 10),
			MaxFiles:  num(raw, "Logging:MaxFiles", 20),
			Console:   flag(raw, "Logging:Console", true),
			Compress:  flag(raw, "Logging:Compress", true),
		},
		Ping:   ProbeToolConfig{str(raw, "ping:name", "ping"), str(raw, "ping:default", "")},
		Twamp:  ProbeToolConfig{str(raw, "twamp:name", "./twping"), str(raw, "twamp:default", "")},
		Twampy: ProbeToolConfig{str(raw, "twampy:name", "python3"), str(raw, "twampy:default", "")},
	}
	return cfg, nil
}

// resolveParallel возвращает число воркеров: явное значение (>0) — как есть;
// 0 (или меньше) — автоподбор «ядра × 256» с потолком 100000 и полом 16. Зонды —
// внешние процессы, в основном ждущие I/O (особенно длинный TWAMP), поэтому
// воркеров нужно много; потолок бережёт многоядерные машины.
func resolveParallel(configured int) int {
	if configured > 0 {
		return configured
	}
	return min(max(runtime.NumCPU()*256, 16), 100000) // min/max — Go 1.21
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

// flag достаёт логическое значение по пути «Секция:Ключ» (true/false или строка).
func flag(raw map[string]any, path string, def bool) bool {
	v, ok := dig(raw, path)
	if !ok {
		return def
	}
	switch t := v.(type) {
	case bool:
		return t
	case string:
		if b, err := strconv.ParseBool(t); err == nil {
			return b
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
