// Слияние конфигураций при обновлении.
//
// Задача простая на словах и неприятная на деле: в новой версии появляются
// настройки, которых в файле администратора нет, — и он о них не узнает, пока
// не полезет в документацию. А затирать его файл эталонным нельзя: там пароли,
// адреса, диапазоны и всё, ради чего проба и настраивалась.
//
// Поэтому обновление делает слияние: значения администратора остаются как есть,
// новые ключи из эталона добавляются рядом. Ничего не удаляется — настройка,
// которой в новой версии больше нет, безвредна и может пригодиться при откате.
//
// Выполняет это сама проба, а не установщик: разбирать JSON в shell-скрипте
// нечем — jq на минимальной CentOS нет, python тоже может не быть, а бинарник
// пробы лежит рядом всегда.
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"sort"
	"strings"
)

// mergeConfigFiles добавляет в текущий конфиг ключи, появившиеся в эталонном.
// Возвращает список добавленного — установщику есть что показать.
//
// Текущего файла может не быть вовсе: это первая установка, тогда он просто
// копируется из эталона.
func mergeConfigFiles(currentPath, referencePath string) ([]string, error) {
	reference, err := readJSONObject(referencePath)
	if err != nil {
		return nil, err
	}

	current, err := readJSONObject(currentPath)
	if os.IsNotExist(err) {
		// Первая установка: эталон и есть итог.
		if err := writeJSONObject(currentPath, reference); err != nil {
			return nil, err
		}
		return nil, nil
	}
	if err != nil {
		return nil, err
	}

	var added []string
	mergeInto(current, reference, "", &added)
	if len(added) == 0 {
		return nil, nil // всё на месте, файл не трогаем
	}

	sort.Strings(added)
	if err := writeJSONObject(currentPath, current); err != nil {
		return nil, err
	}
	return added, nil
}

// mergeInto дополняет current ключами из reference, не меняя существующие.
// Путь ключа копится в prefix, чтобы добавленное можно было назвать целиком.
func mergeInto(current, reference map[string]any, prefix string, added *[]string) {
	for key, refValue := range reference {
		path := key
		if prefix != "" {
			path = prefix + ":" + key
		}

		curValue, exists := current[key]
		if !exists {
			current[key] = refValue
			*added = append(*added, path)
			continue
		}

		// Вложенные секции сливаем дальше: в «Probe» могла появиться одна новая
		// настройка, и заменять всю секцию целиком нельзя.
		refSection, refIsSection := refValue.(map[string]any)
		curSection, curIsSection := curValue.(map[string]any)
		if refIsSection && curIsSection {
			mergeInto(curSection, refSection, path, added)
		}
		// Во всех прочих случаях значение администратора остаётся нетронутым —
		// в том числе если тип разошёлся: его выбор важнее нашего эталона.
	}
}

// readJSONObject читает объект JSON из файла.
func readJSONObject(path string) (map[string]any, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	// В файле, правленном под Windows, может оказаться UTF-8 BOM — разбор
	// JSON его не переваривает.
	data = []byte(strings.TrimPrefix(string(data), "\uFEFF"))

	object := map[string]any{}
	if err := json.Unmarshal(data, &object); err != nil {
		return nil, fmt.Errorf("не удалось разобрать %s: %w", path, err)
	}
	return object, nil
}

// writeJSONObject записывает объект с отступами — файл читают и правят руками.
//
// Пишем через временный файл рядом: обрыв на середине записи оставил бы пробу
// вовсе без настроек, а так старый файл заменяется целиком и разом.
func writeJSONObject(path string, object map[string]any) error {
	data, err := json.MarshalIndent(object, "", "  ")
	if err != nil {
		return err
	}
	data = append(data, '\n')

	temp := path + ".new"
	if err := os.WriteFile(temp, data, 0o644); err != nil {
		return err
	}
	return os.Rename(temp, path)
}

// runConfigMerge — точка входа подкоманды «--merge-config».
//
// Вызывается установщиком при обновлении:
//
//	twamp-probe --merge-config <текущий> <эталонный>
func runConfigMerge(args []string) int {
	if len(args) != 2 {
		fmt.Fprintln(os.Stderr, "использование: twamp-probe --merge-config <текущий.json> <эталонный.json>")
		return 2
	}

	added, err := mergeConfigFiles(args[0], args[1])
	if err != nil {
		fmt.Fprintln(os.Stderr, "слияние настроек не удалось:", err)
		return 1
	}
	for _, key := range added {
		fmt.Println(key)
	}
	return 0
}
