package main

import (
	"encoding/json"
	"fmt"
	"os"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"testing"
)

// Пул портов и эфемерный диапазон ядра лежат в разных файлах пакета, и оба
// задают одно и то же — из каких номеров берутся порты. Разъехаться им проще
// простого, поэтому проверяем, что они остаются согласованными.
//
// Пересечение здесь допустимо и сделано намеренно: ядро раздаёт из этого
// диапазона исходящие TCP-соединения, а управляющий канал twping нужен каждому
// замеру TWamp. Отдай ядру участок в стороне от пула — и число замеров TWamp
// упёрлось бы в размер этого участка. Плата за совмещение — редкие коллизии
// по UDP с чужими сокетами машины, их отрабатывает карантин.
func TestDeployConfig_PortRangeMatchesKernelSetting(t *testing.T) {
	pool, err := os.ReadFile("deploy/appsettings.json")
	if err != nil {
		t.Fatalf("не удалось прочитать appsettings.json: %v", err)
	}
	var config struct {
		Probe struct {
			PortRange string
		}
	}
	if err := json.Unmarshal(pool, &config); err != nil {
		t.Fatalf("appsettings.json — некорректный JSON: %v", err)
	}

	poolFrom, poolTo, err := parseRange(config.Probe.PortRange, "-")
	if err != nil {
		t.Fatalf("Probe:PortRange = %q: %v", config.Probe.PortRange, err)
	}
	if config.Probe.PortRange != defaultPortRange {
		t.Errorf("в appsettings.json диапазон %q, а в коде по умолчанию %q — "+
			"проба без файла настроек возьмёт не то, что в пакете",
			config.Probe.PortRange, defaultPortRange)
	}

	sysctl, err := os.ReadFile("deploy/99-twamp-probe.conf")
	if err != nil {
		t.Fatalf("не удалось прочитать 99-twamp-probe.conf: %v", err)
	}
	setting := regexp.MustCompile(`(?m)^\s*net\.ipv4\.ip_local_port_range\s*=\s*(\d+)\s+(\d+)`).
		FindStringSubmatch(string(sysctl))
	if setting == nil {
		t.Fatal("в 99-twamp-probe.conf нет net.ipv4.ip_local_port_range")
	}
	kernelFrom, kernelTo, err := parseRange(setting[1]+" "+setting[2], " ")
	if err != nil {
		t.Fatalf("ip_local_port_range: %v", err)
	}

	// Два допустимых расклада, и оба осмысленны: диапазоны либо совпадают
	// (замерам TWamp доступен весь пул под управляющие соединения), либо
	// разведены полностью (коллизии по UDP невозможны). Частичное перекрытие
	// не даёт ни того, ни другого — это всегда чья-то невнимательная правка.
	same := poolFrom == kernelFrom && poolTo == kernelTo
	apart := poolTo < kernelFrom || kernelTo < poolFrom
	if !same && !apart {
		t.Errorf("пул пробы %d-%d и эфемерный диапазон ядра %d-%d перекрываются частично: "+
			"либо совмещайте их полностью, либо разводите",
			poolFrom, poolTo, kernelFrom, kernelTo)
	}

	// Резервировать пул списком нельзя: ip_local_reserved_ports вычитает номера
	// и из TCP тоже, а значит отнимет их у управляющих соединений twping.
	if regexp.MustCompile(`(?m)^\s*net\.ipv4\.ip_local_reserved_ports`).Match(sysctl) {
		t.Error("в 99-twamp-probe.conf появилось ip_local_reserved_ports — " +
			"оно вычтет номера и из TCP, оставив замеры TWamp без управляющих соединений")
	}
}

// parseRange разбирает «нижний<sep>верхний».
func parseRange(value, sep string) (int, int, error) {
	low, high, ok := strings.Cut(strings.TrimSpace(value), sep)
	if !ok {
		return 0, 0, fmt.Errorf("ожидался диапазон вида «нижний%sверхний», получено %q", sep, value)
	}
	from, err := strconv.Atoi(strings.TrimSpace(low))
	if err != nil {
		return 0, 0, err
	}
	to, err := strconv.Atoi(strings.TrimSpace(high))
	if err != nil {
		return 0, 0, err
	}
	return from, to, nil
}

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
