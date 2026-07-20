// Хранилище результатов с подтверждением доставки (ACK) — аналог C# ResultStore.
//
// Результаты копятся в очереди; сервер забирает их пачкой (TakeBatch), пачка
// «в полёте» хранится до подтверждения (Confirm) и при потере связи выдаётся
// повторно — дубликаты сервер отбрасывает по ResultId. Очередь ограничена:
// при переполнении вытесняются самые старые. Снимок недоставленного пишется
// на диск по таймеру (не на каждый результат) — дисковая нагрузка минимальна.
package main

import (
	"encoding/json"
	"log"
	"os"
	"slices"
	"sync"
	"time"
)

// resultsFileName — файл недоставленных результатов между перезапусками.
const resultsFileName = "JobResult.json"

// ResultStore — очередь результатов с подтверждением и сохранением на диск.
type ResultStore struct {
	mu         sync.Mutex
	pending    []ActionData
	inFlightId string
	inFlight   []ActionData
	maxPending int
	dropped    int64

	signal  chan struct{} // сигнал «появились данные» для длинного опроса
	dirty   bool
	persist time.Duration
	stop    chan struct{}
}

// NewResultStore создаёт хранилище и запускает фоновый цикл сохранения.
func NewResultStore(maxPending, persistSec int) *ResultStore {
	s := &ResultStore{
		maxPending: maxPending,
		signal:     make(chan struct{}, 1),
		persist:    time.Duration(persistSec) * time.Second,
		stop:       make(chan struct{}),
	}
	go s.persistLoop()
	return s
}

// Add ставит результат в очередь (с вытеснением самых старых при переполнении).
func (s *ResultStore) Add(result ActionData) {
	s.mu.Lock()
	s.pending = append(s.pending, result)
	if len(s.pending) > s.maxPending {
		s.pending = s.pending[1:]
		s.dropped++
		if s.dropped == 1 || s.dropped%1000 == 0 {
			log.Printf("Очередь результатов переполнена (лимит %d) — всего вытеснено %d", s.maxPending, s.dropped)
		}
	}
	s.dirty = true
	s.mu.Unlock()

	select {
	case s.signal <- struct{}{}:
	default: // сигнал уже стоит — потребитель и так проснётся
	}
}

// TakeBatch выдаёт пачку результатов, ожидая появления данных до timeout
// («длинный опрос»). Неподтверждённая пачка выдаётся повторно.
func (s *ResultStore) TakeBatch(timeout time.Duration) ResultBatch {
	deadline := time.After(timeout)
	for {
		s.mu.Lock()
		if s.inFlight != nil {
			batch := ResultBatch{BatchId: s.inFlightId, Items: slices.Clone(s.inFlight)} // slices.Clone — Go 1.21
			s.mu.Unlock()
			return batch
		}
		if len(s.pending) > 0 {
			s.inFlight = s.pending
			s.pending = nil
			s.inFlightId = NewGuid()
			batch := ResultBatch{BatchId: s.inFlightId, Items: slices.Clone(s.inFlight)}
			s.mu.Unlock()
			return batch
		}
		s.mu.Unlock()

		select {
		case <-s.signal: // появились данные — попробуем снова
		case <-deadline:
			return ResultBatch{BatchId: EmptyGuid, Items: []ActionData{}}
		}
	}
}

// Confirm подтверждает запись пачки сервером — только теперь она удаляется.
func (s *ResultStore) Confirm(batchId string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.inFlight == nil || s.inFlightId != batchId {
		return false
	}
	s.inFlight = nil
	s.inFlightId = ""
	s.dirty = true
	return true
}

// Load восстанавливает недоставленные результаты после перезапуска.
func (s *ResultStore) Load() {
	data, err := os.ReadFile(resultsFileName)
	if err != nil {
		return // файла нет — чистый старт
	}
	var saved []ActionData
	if err := json.Unmarshal(data, &saved); err != nil {
		log.Printf("Не удалось загрузить %s: %v", resultsFileName, err)
		return
	}
	s.mu.Lock()
	s.pending = append(saved, s.pending...)
	s.mu.Unlock()
	if len(saved) > 0 {
		log.Printf("Загружено %d недоставленных результатов из %s", len(saved), resultsFileName)
		select {
		case s.signal <- struct{}{}:
		default:
		}
	}
}

// persistLoop периодически пишет снимок недоставленного на диск (только при изменениях).
func (s *ResultStore) persistLoop() {
	ticker := time.NewTicker(s.persist)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			s.flush()
		case <-s.stop:
			s.flush() // финальный снимок при остановке
			return
		}
	}
}

// flush атомарно записывает снимок «в полёте + очередь» через временный файл.
func (s *ResultStore) flush() {
	s.mu.Lock()
	if !s.dirty {
		s.mu.Unlock()
		return
	}
	s.dirty = false
	snapshot := slices.Concat(s.inFlight, s.pending) // slices.Concat — Go 1.22
	s.mu.Unlock()

	if len(snapshot) == 0 {
		_ = os.Remove(resultsFileName)
		return
	}
	data, err := json.Marshal(snapshot)
	if err != nil {
		log.Printf("Ошибка сериализации результатов: %v", err)
		return
	}
	tmp := resultsFileName + ".tmp"
	if err := os.WriteFile(tmp, data, 0o644); err != nil {
		log.Printf("Ошибка сохранения результатов: %v", err)
		return
	}
	if err := os.Rename(tmp, resultsFileName); err != nil {
		log.Printf("Ошибка замены файла результатов: %v", err)
	}
}

// Close останавливает фоновый цикл и делает финальный снимок.
func (s *ResultStore) Close() { close(s.stop) }
