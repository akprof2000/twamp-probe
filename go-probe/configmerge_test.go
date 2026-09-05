package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"testing"
)

// writeConfig кладёт файл настроек во временный каталог теста.
func writeConfig(t *testing.T, name, content string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), name)
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatalf("не записать %s: %v", name, err)
	}
	return path
}

// readConfig читает файл настроек обратно в дерево.
func readConfig(t *testing.T, path string) map[string]any {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("не прочитать %s: %v", path, err)
	}
	object := map[string]any{}
	if err := json.Unmarshal(data, &object); err != nil {
		t.Fatalf("%s — некорректный JSON: %v\n%s", path, err, data)
	}
	return object
}

func TestMergeConfig_KeepsAdminValuesAddsNewKeys(t *testing.T) {
	// Главное свойство обновления: настройки администратора остаются как есть,
	// а появившиеся в новой версии добавляются рядом. Иначе одно из двух —
	// либо человек не узнаёт о новых ключах, пока не полезет в документацию,
	// либо теряет свои правки при каждом обновлении.
	current := writeConfig(t, "appsettings.json", `{
	  "Urls": "http://0.0.0.0:9443",
	  "Probe": {
	    "MaxParallel": 500,
	    "PortRange": "40000-45000"
	  },
	  "Auth": { "ApiKey": "секрет-администратора" }
	}`)
	reference := writeConfig(t, "reference.json", `{
	  "Urls": "http://0.0.0.0:8443",
	  "Probe": {
	    "MaxParallel": 20000,
	    "PortRange": "25000-65000",
	    "MinParallel": 16
	  },
	  "Auth": { "ApiKey": "" },
	  "Logging": { "Level": "Warn" }
	}`)

	added, err := mergeConfigFiles(current, reference)
	if err != nil {
		t.Fatalf("слияние не удалось: %v", err)
	}

	result := readConfig(t, current)
	probe := result["Probe"].(map[string]any)

	// Значения администратора нетронуты — включая те, что расходятся с эталоном.
	if got := result["Urls"]; got != "http://0.0.0.0:9443" {
		t.Errorf("Urls стал %v — правка администратора затёрта", got)
	}
	if got := probe["MaxParallel"]; got != float64(500) {
		t.Errorf("Probe:MaxParallel стал %v, ожидалось 500", got)
	}
	if got := probe["PortRange"]; got != "40000-45000" {
		t.Errorf("Probe:PortRange стал %v — диапазон затёрт", got)
	}
	if got := result["Auth"].(map[string]any)["ApiKey"]; got != "секрет-администратора" {
		t.Errorf("ключ API затёрт эталонным пустым значением: %v", got)
	}

	// Новое добавлено — и в корне, и внутри существующей секции.
	if got := probe["MinParallel"]; got != float64(16) {
		t.Errorf("Probe:MinParallel не добавлен: %v", got)
	}
	logging, ok := result["Logging"].(map[string]any)
	if !ok || logging["Level"] != "Warn" {
		t.Errorf("секция Logging не добавлена целиком: %v", result["Logging"])
	}

	// Установщику есть что показать: он печатает этот список.
	want := []string{"Logging", "Probe:MinParallel"}
	if !slices.Equal(added, want) {
		t.Errorf("добавленным числится %v, ожидалось %v", added, want)
	}
}

func TestMergeConfig_FirstInstallCopiesReference(t *testing.T) {
	// Первая установка: своего файла ещё нет, берётся эталон целиком.
	reference := writeConfig(t, "reference.json", `{"Urls": "http://0.0.0.0:8443"}`)
	current := filepath.Join(t.TempDir(), "appsettings.json")

	added, err := mergeConfigFiles(current, reference)
	if err != nil {
		t.Fatalf("слияние не удалось: %v", err)
	}
	if len(added) != 0 {
		t.Errorf("при первой установке добавленным числится %v — показывать нечего", added)
	}
	if got := readConfig(t, current)["Urls"]; got != "http://0.0.0.0:8443" {
		t.Errorf("файл не создан из эталона: %v", got)
	}
}

func TestMergeConfig_NothingNewLeavesFileUntouched(t *testing.T) {
	// Обновление без новых настроек не должно трогать файл вовсе: ни
	// переформатировать, ни менять время изменения. Файл правят руками, и его
	// вид — тоже чья-то работа.
	const original = "{\n\t\"Urls\": \"http://0.0.0.0:9443\"\n}"
	current := writeConfig(t, "appsettings.json", original)
	reference := writeConfig(t, "reference.json", `{"Urls": "http://0.0.0.0:8443"}`)

	before, err := os.Stat(current)
	if err != nil {
		t.Fatal(err)
	}

	added, err := mergeConfigFiles(current, reference)
	if err != nil {
		t.Fatalf("слияние не удалось: %v", err)
	}
	if len(added) != 0 {
		t.Errorf("добавленным числится %v, хотя новых ключей нет", added)
	}

	data, _ := os.ReadFile(current)
	if string(data) != original {
		t.Errorf("файл переписан без нужды:\n%s", data)
	}
	after, err := os.Stat(current)
	if err != nil {
		t.Fatal(err)
	}
	if !after.ModTime().Equal(before.ModTime()) {
		t.Error("время изменения файла обновилось, хотя менять было нечего")
	}
}

func TestMergeConfig_BadJSONKeepsFile(t *testing.T) {
	// Испорченный файл лучше не трогать: слить его нельзя, а перезаписать
	// эталоном — потерять настройки. Установщик покажет ошибку и остановится.
	const broken = `{"Urls": "http://0.0.0.0:9443",`
	current := writeConfig(t, "appsettings.json", broken)
	reference := writeConfig(t, "reference.json", `{"Urls": "http://0.0.0.0:8443"}`)

	if _, err := mergeConfigFiles(current, reference); err == nil {
		t.Fatal("испорченный файл принят как исправный")
	}

	data, _ := os.ReadFile(current)
	if string(data) != broken {
		t.Errorf("испорченный файл изменён:\n%s", data)
	}
}

func TestMergeConfig_KeepsKeyOrderAndValueText(t *testing.T) {
	// Файл правят руками, и его вид — тоже чья-то работа. После слияния ключи
	// должны идти в прежнем порядке (новые — в конце своей секции), а значения
	// остаться тем же текстом: без сортировки по алфавиту, без escape-кодов
	// вместо «&», «<», «>» и без переписывания чисел.
	current := writeConfig(t, "appsettings.json", `{
	  "Urls": "http://0.0.0.0:9443",
	  "Probe": { "MaxParallel": 5e2, "PortRange": "40000-45000" },
	  "Auth": { "ApiKey": "a&b<c>" },
	  "ping": { "name": "ping" }
	}`)
	reference := writeConfig(t, "reference.json", `{
	  "Auth": { "ApiKey": "" },
	  "Logging": { "Level": "Warn" },
	  "Probe": { "MinParallel": 16, "MaxParallel": 20000 },
	  "Urls": "http://0.0.0.0:8443",
	  "ping": { "name": "ping" }
	}`)

	if _, err := mergeConfigFiles(current, reference); err != nil {
		t.Fatalf("слияние не удалось: %v", err)
	}

	data, _ := os.ReadFile(current)
	text := string(data)
	order := []string{`"Urls"`, `"Probe"`, `"MaxParallel"`, `"PortRange"`, `"MinParallel"`, `"Auth"`, `"ping"`, `"Logging"`}
	last := -1
	for _, key := range order {
		at := strings.Index(text, key)
		if at < last {
			t.Fatalf("порядок ключей нарушен, %s не на месте:\n%s", key, text)
		}
		last = at
	}
	if !strings.Contains(text, `"a&b<c>"`) {
		t.Errorf("значение с «&<>» заэкранировано:\n%s", text)
	}
	if !strings.Contains(text, "5e2") {
		t.Errorf("текст числа переписан:\n%s", text)
	}
}

func TestMergeConfig_KeepsFileMode(t *testing.T) {
	// Администратор мог закрыть файл от чужих глаз из-за ключа API — после
	// слияния права должны остаться прежними, а не сброситься в 0644.
	if runtime.GOOS == "windows" {
		t.Skip("права POSIX на Windows не действуют")
	}
	current := writeConfig(t, "appsettings.json", `{"Urls": "http://0.0.0.0:9443"}`)
	reference := writeConfig(t, "reference.json", `{"Urls": "x", "New": 1}`)
	if err := os.Chmod(current, 0o600); err != nil {
		t.Fatal(err)
	}

	if _, err := mergeConfigFiles(current, reference); err != nil {
		t.Fatalf("слияние не удалось: %v", err)
	}

	info, err := os.Stat(current)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o600 {
		t.Errorf("права файла стали %o, ожидалось 600", info.Mode().Perm())
	}
}
