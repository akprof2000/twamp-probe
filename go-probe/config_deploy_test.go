package main

import (
	"encoding/json"
	"os"
	"regexp"
	"sort"
	"testing"
)

// Настройка, которой нет в поставляемом appsettings.json, для администратора
// не существует: он её не найдёт и не поправит, а проба молча возьмёт значение
// по умолчанию. Именно так случилось с Probe:QueueCapacity — ключ был в коде
// и в документации, но не в файле, и очередь задач осталась стотысячной, когда
// все остальные величины уже понизили.
func TestDeployConfig_ContainsEveryProbeSetting(t *testing.T) {
	source, err := os.ReadFile("config.go")
	if err != nil {
		t.Fatalf("не удалось прочитать config.go: %v", err)
	}

	// Ключи, которые проба читает из секции Probe.
	pattern := regexp.MustCompile(`"Probe:([A-Za-z]+)"`)
	expected := map[string]bool{}
	for _, m := range pattern.FindAllStringSubmatch(string(source), -1) {
		expected[m[1]] = true
	}
	if len(expected) == 0 {
		t.Fatal("в config.go не найдено ни одного ключа Probe: — проверка бесполезна")
	}

	for _, file := range []string{"deploy/appsettings.json", "deploy/appsettings.windows.json"} {
		t.Run(file, func(t *testing.T) {
			data, err := os.ReadFile(file)
			if err != nil {
				t.Fatalf("не удалось прочитать %s: %v", file, err)
			}
			var config struct {
				Probe map[string]any `json:"Probe"`
			}
			if err := json.Unmarshal(data, &config); err != nil {
				t.Fatalf("%s — некорректный JSON: %v", file, err)
			}

			var missing []string
			for key := range expected {
				if _, ok := config.Probe[key]; !ok {
					missing = append(missing, key)
				}
			}
			sort.Strings(missing)
			if len(missing) > 0 {
				t.Errorf("в %s нет настроек, которые проба читает: %v", file, missing)
			}
		})
	}
}
