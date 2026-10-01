// Тесты локального архива результатов: запись zip, имя по времени, удаление старых.
package main

import (
	"archive/zip"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func newTestArchive(t *testing.T) *ResultArchive {
	a := NewResultArchive(t.TempDir(), 60, 3, 100) // запись раз в час — в тестах не мешает
	t.Cleanup(a.Close)
	return a
}

// Накопленные результаты пишутся JSON-файлом в zip с именем по времени создания.
func TestResultArchive_WritesZipNamedByTime(t *testing.T) {
	a := newTestArchive(t)
	a.Add(ActionData{ResultId: "r1"})
	a.Add(ActionData{ResultId: "r2"})

	now := time.Date(2026, 10, 1, 14, 5, 0, 0, time.Local)
	a.flush(now)

	reader, err := zip.OpenReader(filepath.Join(a.dir, "2026-10-01_14-05-00.zip"))
	if err != nil {
		t.Fatalf("архив не создан: %v", err)
	}
	defer reader.Close()
	if len(reader.File) != 1 || reader.File[0].Name != "2026-10-01_14-05-00.json" {
		t.Fatalf("в архиве ожидался один файл 2026-10-01_14-05-00.json")
	}
	entry, err := reader.File[0].Open()
	if err != nil {
		t.Fatal(err)
	}
	defer entry.Close()
	var items []ActionData
	if err := json.NewDecoder(entry).Decode(&items); err != nil {
		t.Fatalf("содержимое архива не разобрано: %v", err)
	}
	if len(items) != 2 || items[0].ResultId != "r1" || items[1].ResultId != "r2" {
		t.Errorf("ожидались результаты r1, r2, получено %d", len(items))
	}
}

// Без новых результатов архив не создаётся; два архива в одну секунду не затирают друг друга.
func TestResultArchive_EmptyAndSameSecond(t *testing.T) {
	a := newTestArchive(t)
	now := time.Date(2026, 10, 1, 14, 5, 0, 0, time.Local)

	a.flush(now)
	if entries, _ := os.ReadDir(a.dir); len(entries) != 0 {
		t.Fatalf("пустой архив создаваться не должен, файлов: %d", len(entries))
	}

	a.Add(ActionData{ResultId: "r1"})
	a.flush(now)
	a.Add(ActionData{ResultId: "r2"})
	a.flush(now)
	for _, name := range []string{"2026-10-01_14-05-00.zip", "2026-10-01_14-05-00-1.zip"} {
		if _, err := os.Stat(filepath.Join(a.dir, name)); err != nil {
			t.Errorf("ожидался архив %s: %v", name, err)
		}
	}
}

// Удаляются только архивы старше срока хранения; посторонние файлы не трогаются.
func TestResultArchive_PruneOld(t *testing.T) {
	a := newTestArchive(t)
	now := time.Date(2026, 10, 10, 12, 0, 0, 0, time.Local)

	old := now.Add(-73*time.Hour).Format(archiveNameLayout) + ".zip"
	fresh := now.Add(-71*time.Hour).Format(archiveNameLayout) + ".zip"
	foreign := "notes.zip"
	for _, name := range []string{old, fresh, foreign} {
		if err := os.WriteFile(filepath.Join(a.dir, name), []byte("x"), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	a.prune(now)

	if _, err := os.Stat(filepath.Join(a.dir, old)); !os.IsNotExist(err) {
		t.Errorf("архив старше 3 дней должен быть удалён")
	}
	for _, name := range []string{fresh, foreign} {
		if _, err := os.Stat(filepath.Join(a.dir, name)); err != nil {
			t.Errorf("файл %s должен остаться: %v", name, err)
		}
	}
}

// Close дожидается записи накопленного; выключенный архив (nil) безопасен.
func TestResultArchive_CloseFlushesAndNilIsSafe(t *testing.T) {
	a := NewResultArchive(t.TempDir(), 60, 3, 100)
	a.Add(ActionData{ResultId: "r1"})
	a.Close()
	if entries, _ := os.ReadDir(a.dir); len(entries) != 1 {
		t.Errorf("при остановке ожидался один архив, файлов: %d", len(entries))
	}

	disabled := NewResultArchive("", 5, 3, 100)
	disabled.Add(ActionData{ResultId: "r1"})
	disabled.Close()
}
