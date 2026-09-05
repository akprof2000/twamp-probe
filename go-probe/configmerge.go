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
// Файл правят руками, поэтому бережём и его вид: порядок ключей сохраняется,
// значения переписываются тем же текстом, каким были (encoding/json в map их
// перемешал бы по алфавиту и заэкранировал «&», «<», «>»), права на файл
// остаются прежними.
//
// Выполняет это сама проба, а не установщик: разбирать JSON в shell-скрипте
// нечем — jq на минимальной CentOS нет, python тоже может не быть, а бинарник
// пробы лежит рядом всегда.
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"os"
	"sort"
)

// configObject — объект JSON, помнящий порядок ключей. Значение — либо
// вложенный *configObject, либо json.RawMessage с исходным текстом.
type configObject struct {
	keys   []string
	values map[string]any
}

// set добавляет ключ в конец или заменяет значение существующего.
func (o *configObject) set(key string, value any) {
	if _, exists := o.values[key]; !exists {
		o.keys = append(o.keys, key)
	}
	o.values[key] = value
}

// mergeConfigFiles добавляет в текущий конфиг ключи, появившиеся в эталонном.
// Возвращает список добавленного — установщику есть что показать.
//
// Текущего файла может не быть вовсе: это первая установка, тогда он просто
// копируется из эталона.
func mergeConfigFiles(currentPath, referencePath string) ([]string, error) {
	reference, err := readConfigObject(referencePath)
	if err != nil {
		return nil, err
	}

	current, err := readConfigObject(currentPath)
	if os.IsNotExist(err) {
		// Первая установка: эталон и есть итог.
		if err := writeConfigObject(currentPath, reference); err != nil {
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
	if err := writeConfigObject(currentPath, current); err != nil {
		return nil, err
	}
	return added, nil
}

// mergeInto дополняет current ключами из reference, не меняя существующие.
// Путь ключа копится в prefix, чтобы добавленное можно было назвать целиком.
func mergeInto(current, reference *configObject, prefix string, added *[]string) {
	for _, key := range reference.keys {
		refValue := reference.values[key]
		path := key
		if prefix != "" {
			path = prefix + ":" + key
		}

		curValue, exists := current.values[key]
		if !exists {
			current.set(key, refValue)
			*added = append(*added, path)
			continue
		}

		// Вложенные секции сливаем дальше: в «Probe» могла появиться одна новая
		// настройка, и заменять всю секцию целиком нельзя.
		refSection, refIsSection := refValue.(*configObject)
		curSection, curIsSection := curValue.(*configObject)
		if refIsSection && curIsSection {
			mergeInto(curSection, refSection, path, added)
		}
		// Во всех прочих случаях значение администратора остаётся нетронутым —
		// в том числе если тип разошёлся: его выбор важнее нашего эталона.
	}
}

// readConfigObject читает файл настроек в объект с порядком ключей.
func readConfigObject(path string) (*configObject, error) {
	data, err := readConfigFile(path)
	if err != nil {
		return nil, err
	}
	object, err := parseConfigObject(data)
	if err != nil {
		return nil, fmt.Errorf("не удалось разобрать %s: %w", path, err)
	}
	return object, nil
}

// parseConfigObject разбирает объект JSON, сохраняя порядок ключей и исходный
// текст значений. Вложенные объекты разбираются так же, всё прочее (строки,
// числа, массивы) остаётся как есть — менять их нам незачем.
func parseConfigObject(data []byte) (*configObject, error) {
	decoder := json.NewDecoder(bytes.NewReader(data))

	if token, err := decoder.Token(); err != nil {
		return nil, err
	} else if token != json.Delim('{') {
		return nil, fmt.Errorf("ожидался объект, а не %v", token)
	}

	object := &configObject{values: map[string]any{}}
	for decoder.More() {
		token, err := decoder.Token()
		if err != nil {
			return nil, err
		}
		key, _ := token.(string) // после '{' и между значениями — только имена

		var raw json.RawMessage
		if err := decoder.Decode(&raw); err != nil {
			return nil, err
		}

		var value any = raw
		if bytes.HasPrefix(bytes.TrimSpace(raw), []byte{'{'}) {
			if value, err = parseConfigObject(raw); err != nil {
				return nil, err
			}
		}
		object.set(key, value)
	}

	if _, err := decoder.Token(); err != nil { // закрывающая '}'
		return nil, err
	}
	return object, nil
}

// MarshalJSON пишет ключи в сохранённом порядке, а значения — исходным текстом.
func (o *configObject) MarshalJSON() ([]byte, error) {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false) // имя ключа с «&» или «<» оставляем читаемым

	buffer.WriteByte('{')
	for i, key := range o.keys {
		if i > 0 {
			buffer.WriteByte(',')
		}
		if err := encoder.Encode(key); err != nil {
			return nil, err
		}
		buffer.Truncate(buffer.Len() - 1) // Encode добавляет перевод строки
		buffer.WriteByte(':')
		// Значение — тем же Encoder: json.Marshal заэкранировал бы «&<>»
		// и в исходном тексте RawMessage.
		if err := encoder.Encode(o.values[key]); err != nil {
			return nil, err
		}
		buffer.Truncate(buffer.Len() - 1)
	}
	buffer.WriteByte('}')
	return buffer.Bytes(), nil
}

// writeConfigObject записывает объект с отступами — файл читают и правят руками.
//
// Пишем через временный файл рядом: обрыв на середине записи оставил бы пробу
// вовсе без настроек, а так старый файл заменяется целиком и разом. Права
// прежнего файла переносятся на новый: администратор мог закрыть его от чужих
// глаз из-за ключа API.
func writeConfigObject(path string, object *configObject) error {
	// Именно Encoder, а не json.Marshal: тот пропускает даже готовый текст
	// через экранирование «&<>», а Encoder умеет его выключить.
	var data bytes.Buffer
	encoder := json.NewEncoder(&data)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(object); err != nil { // Encode завершает перевод строки
		return err
	}

	mode := os.FileMode(0o644)
	if info, err := os.Stat(path); err == nil {
		mode = info.Mode().Perm()
	}

	temp := path + ".new"
	if err := os.WriteFile(temp, data.Bytes(), mode); err != nil {
		return err
	}
	// WriteFile учитывает umask, поэтому права выставляем явно.
	if err := os.Chmod(temp, mode); err != nil {
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
