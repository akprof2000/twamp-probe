// Локальный архив результатов — копия всех замеров на диске пробы.
//
// Архив не связан с доставкой серверу: результат попадает сюда сразу после
// замера и остаётся, даже когда сервер его уже забрал. Раз в заданный период
// накопленное пишется одним JSON-файлом в zip; имя архива — дата и время его
// создания. Архивы старше срока хранения удаляются.
package main

import (
	"archive/zip"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// archiveNameLayout — имя архива: дата и время создания (без двоеточий —
// они недопустимы в именах файлов Windows).
const archiveNameLayout = "2006-01-02_15-04-05"

// ResultArchive копит результаты и периодически сбрасывает их в zip-архивы.
type ResultArchive struct {
	mu          sync.Mutex
	buffer      []ActionData
	maxBuffered int
	dropped     int64

	dir       string
	interval  time.Duration
	retention time.Duration
	stop      chan struct{}
	done      chan struct{}
}

// NewResultArchive создаёт архив и запускает фоновый цикл записи.
// Пустая папка или неположительный период означают «архив выключен» —
// возвращается nil, все методы на нём безопасны.
func NewResultArchive(dir string, intervalMin, retentionDays, maxBuffered int) *ResultArchive {
	if dir == "" || intervalMin <= 0 {
		return nil
	}
	a := &ResultArchive{
		maxBuffered: maxBuffered,
		dir:         dir,
		interval:    time.Duration(intervalMin) * time.Minute,
		retention:   time.Duration(retentionDays) * 24 * time.Hour,
		stop:        make(chan struct{}),
		done:        make(chan struct{}),
	}
	go a.loop()
	return a
}

// Add запоминает результат до ближайшей записи архива.
func (a *ResultArchive) Add(result ActionData) {
	if a == nil {
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	a.buffer = append(a.buffer, result)
	// Буфер растёт сверх обычного, только когда архив не удаётся записать
	// (нет места, нет прав) — тогда старое вытесняется, память не утекает.
	if a.maxBuffered > 0 && len(a.buffer) > a.maxBuffered {
		a.buffer = a.buffer[1:]
		a.dropped++
		if a.dropped == 1 || a.dropped%1000 == 0 {
			logResults.Error("Буфер архива результатов переполнен — старые записи вытесняются",
				"лимит", a.maxBuffered, "всего_вытеснено", a.dropped)
		}
	}
}

// Close останавливает фоновый цикл и дожидается записи последнего архива.
func (a *ResultArchive) Close() {
	if a == nil {
		return
	}
	close(a.stop)
	<-a.done
}

// loop пишет архив по таймеру и при остановке, попутно удаляя устаревшие.
func (a *ResultArchive) loop() {
	defer close(a.done)
	a.prune(time.Now()) // пока проба была выключена, архивы могли устареть

	ticker := time.NewTicker(a.interval)
	defer ticker.Stop()
	for {
		select {
		case now := <-ticker.C:
			a.flush(now)
			a.prune(now)
		case <-a.stop:
			a.flush(time.Now()) // накопленное с последней записи не теряем
			return
		}
	}
}

// flush записывает накопленные результаты в новый архив. Если записать не
// удалось, результаты возвращаются в буфер и уйдут в следующий архив.
func (a *ResultArchive) flush(now time.Time) {
	a.mu.Lock()
	items := a.buffer
	a.buffer = nil
	a.mu.Unlock()

	if len(items) == 0 {
		return
	}
	path, err := a.write(items, now)
	if err != nil {
		logResults.Error("Не удалось записать архив результатов", "папка", a.dir, "ошибка", err)
		a.mu.Lock()
		a.buffer = append(items, a.buffer...)
		a.mu.Unlock()
		return
	}
	logResults.Info("Архив результатов записан", "файл", path, "результатов", len(items))
}

// write создаёт zip с одним JSON-файлом; пишет через временный файл, чтобы
// в папке не оказалось недописанного архива.
func (a *ResultArchive) write(items []ActionData, now time.Time) (string, error) {
	if err := os.MkdirAll(a.dir, 0o755); err != nil {
		return "", err
	}
	name := a.freeName(now)
	path := filepath.Join(a.dir, name+".zip")
	tmp := path + ".tmp"

	file, err := os.Create(tmp)
	if err != nil {
		return "", err
	}
	archive := zip.NewWriter(file)
	entry, err := archive.CreateHeader(&zip.FileHeader{
		Name: name + ".json", Method: zip.Deflate, Modified: now,
	})
	if err == nil {
		err = json.NewEncoder(entry).Encode(items)
	}
	if closeErr := archive.Close(); err == nil {
		err = closeErr
	}
	if closeErr := file.Close(); err == nil {
		err = closeErr
	}
	if err == nil {
		err = os.Rename(tmp, path)
	}
	if err != nil {
		_ = os.Remove(tmp)
		return "", err
	}
	return path, nil
}

// freeName возвращает имя архива по времени создания. Два архива в одну
// секунду (таймер и остановка) не должны затереть друг друга — второму
// добавляется номер.
func (a *ResultArchive) freeName(now time.Time) string {
	base := now.Format(archiveNameLayout)
	name := base
	for n := 1; ; n++ {
		if _, err := os.Stat(filepath.Join(a.dir, name+".zip")); err != nil {
			return name
		}
		name = fmt.Sprintf("%s-%d", base, n)
	}
}

// prune удаляет архивы старше срока хранения. Возраст берётся из имени, и
// трогаются только файлы с именем архива — посторонние файлы в папке целы.
func (a *ResultArchive) prune(now time.Time) {
	if a.retention <= 0 {
		return // срок хранения 0 — архивы не удаляются
	}
	entries, err := os.ReadDir(a.dir)
	if err != nil {
		return // папки ещё нет — удалять нечего
	}
	for _, entry := range entries {
		created, ok := archiveCreated(entry.Name())
		if entry.IsDir() || !ok || now.Sub(created) <= a.retention {
			continue
		}
		path := filepath.Join(a.dir, entry.Name())
		if err := os.Remove(path); err != nil {
			logResults.Error("Не удалось удалить устаревший архив результатов", "файл", path, "ошибка", err)
			continue
		}
		logResults.Info("Устаревший архив результатов удалён", "файл", path)
	}
}

// archiveCreated разбирает время создания из имени архива.
func archiveCreated(fileName string) (time.Time, bool) {
	if !strings.HasSuffix(fileName, ".zip") || len(fileName) < len(archiveNameLayout) {
		return time.Time{}, false
	}
	created, err := time.ParseInLocation(archiveNameLayout, fileName[:len(archiveNameLayout)], time.Local)
	return created, err == nil
}
