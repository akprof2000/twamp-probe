// Дисковая очередь пробы. Данные, граница пачки и её ACK меняются транзакционно.
package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	bolt "go.etcd.io/bbolt"
)

var rowsBucket = []byte("results")
var metaBucket = []byte("delivery")

type ResultStore struct {
	db         *bolt.DB
	batchSize  int
	signal     chan struct{}
	legacyPath string
	closeOnce  sync.Once
}

func number(n uint64) []byte { return binary.BigEndian.AppendUint64(nil, n) }
func readNumber(b []byte) uint64 {
	if len(b) == 0 {
		return 0
	}
	return binary.BigEndian.Uint64(b)
}

func OpenResultStore(dir string, batchSize int) (*ResultStore, error) {
	db, err := bolt.Open(filepath.Join(dir, "results.db"), 0600, &bolt.Options{Timeout: time.Second})
	if err != nil {
		return nil, err
	}
	s := &ResultStore{db: db, batchSize: max(1, batchSize), signal: make(chan struct{}, 1), legacyPath: filepath.Join(dir, "JobResult.json")}
	err = db.Update(func(tx *bolt.Tx) error {
		rows, err := tx.CreateBucketIfNotExists(rowsBucket)
		if err != nil {
			return err
		}
		meta, err := tx.CreateBucketIfNotExists(metaBucket)
		if err != nil {
			return err
		}
		if meta.Get([]byte("legacyEnd")) != nil {
			return nil
		}
		data, err := os.ReadFile(s.legacyPath)
		if errors.Is(err, os.ErrNotExist) {
			return nil
		}
		if err != nil {
			return err
		}
		var items []json.RawMessage
		if err = json.Unmarshal(data, &items); err != nil {
			return fmt.Errorf("JobResult.json: %w", err)
		}
		for _, raw := range items {
			var record ActionData
			if err = json.Unmarshal(raw, &record); err != nil {
				return err
			}
			if record.ResultId == "" || record.ResultId == EmptyGuid {
				record.ResultId = NewGuid()
				raw, err = json.Marshal(record)
				if err != nil {
					return err
				}
			}
			if err = appendRow(rows, raw); err != nil {
				return err
			}
		}
		hash := sha256.Sum256(data)
		if err := meta.Put([]byte("legacyHash"), hash[:]); err != nil {
			return err
		}
		return meta.Put([]byte("legacyEnd"), number(rows.Sequence()))
	})
	if err != nil {
		db.Close()
		return nil, err
	}
	s.cleanupLegacy()
	return s, nil
}

func appendRow(rows *bolt.Bucket, data []byte) error {
	key, err := rows.NextSequence()
	if err != nil {
		return err
	}
	return rows.Put(number(key), data)
}

// Batch groups concurrent producers into a single fsync. A successful return means durable.
func (s *ResultStore) Add(result ActionData) error {
	data, err := json.Marshal(result)
	if err != nil {
		return err
	}
	err = s.db.Batch(func(tx *bolt.Tx) error { return appendRow(tx.Bucket(rowsBucket), data) })
	if err == nil {
		select {
		case s.signal <- struct{}{}:
		default:
		}
	}
	return err
}

func (s *ResultStore) readBatch(timeout time.Duration) (ResultBatch, error) {
	deadline := time.NewTimer(timeout)
	defer deadline.Stop()
	for {
		empty := ResultBatch{BatchId: EmptyGuid, Items: []ActionData{}}
		var pending bool
		err := s.db.View(func(tx *bolt.Tx) error {
			key, _ := tx.Bucket(rowsBucket).Cursor().First()
			pending = key != nil
			return nil
		})
		if err != nil {
			return empty, err
		}
		if pending {
			// Persist the upper key before exposing the batch, so retries after restart are identical.
			err = s.db.Update(func(tx *bolt.Tx) error {
				rows, meta := tx.Bucket(rowsBucket), tx.Bucket(metaBucket)
				if meta.Get([]byte("batch")) != nil {
					return nil
				}
				c := rows.Cursor()
				k, _ := c.First()
				if k == nil {
					return nil
				}
				end := min(rows.Sequence(), readNumber(k)+uint64(s.batchSize)-1)
				if err := meta.Put([]byte("end"), number(end)); err != nil {
					return err
				}
				return meta.Put([]byte("batch"), []byte(NewGuid()))
			})
			if err != nil {
				return empty, err
			}
			batch := empty
			err = s.db.View(func(tx *bolt.Tx) error {
				meta := tx.Bucket(metaBucket)
				id := meta.Get([]byte("batch"))
				if id == nil {
					return nil
				}
				batch.BatchId = string(id)
				end := meta.Get([]byte("end"))
				c := tx.Bucket(rowsBucket).Cursor()
				for k, v := c.First(); k != nil && bytes.Compare(k, end) <= 0; k, v = c.Next() {
					var item ActionData
					if err := json.Unmarshal(v, &item); err != nil {
						return err
					}
					batch.Items = append(batch.Items, item)
				}
				return nil
			})
			if err != nil {
				return empty, err
			}
			if len(batch.Items) > 0 {
				return batch, nil
			}
			continue
		}
		select {
		case <-s.signal:
		case <-deadline.C:
			return empty, nil
		}
	}
}

func (s *ResultStore) confirm(id string) (bool, error) {
	confirmed := false
	err := s.db.Update(func(tx *bolt.Tx) error {
		rows, meta := tx.Bucket(rowsBucket), tx.Bucket(metaBucket)
		if id == "" || string(meta.Get([]byte("batch"))) != id {
			return nil
		}
		end := meta.Get([]byte("end"))
		// Copy the key before mutating the bucket.
		last := readNumber(end)
		first, _ := rows.Cursor().First()
		if first != nil {
			for n := readNumber(first); n <= last; n++ {
				if err := rows.Delete(number(n)); err != nil {
					return err
				}
			}
		}
		if err := meta.Put([]byte("acked"), number(last)); err != nil {
			return err
		}
		if err := meta.Delete([]byte("end")); err != nil {
			return err
		}
		confirmed = true
		return meta.Delete([]byte("batch"))
	})
	if err == nil && confirmed {
		s.cleanupLegacy()
	}
	return confirmed && err == nil, err
}

func (s *ResultStore) cleanupLegacy() {
	// Keep the import marker until the old file is unlinked. If the process
	// exits between unlink and commit, retrying is safe.
	err := s.db.Update(func(tx *bolt.Tx) error {
		m := tx.Bucket(metaBucket)
		end := m.Get([]byte("legacyEnd"))
		if end == nil || readNumber(m.Get([]byte("acked"))) < readNumber(end) {
			return nil
		}
		data, err := os.ReadFile(s.legacyPath)
		if err == nil {
			hash := sha256.Sum256(data)
			if !bytes.Equal(hash[:], m.Get([]byte("legacyHash"))) {
				return fmt.Errorf("файл JobResult.json изменён после импорта, сохранён на диске")
			}
			err = os.Remove(s.legacyPath)
		}
		if err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
		if err = m.Delete([]byte("legacyHash")); err != nil {
			return err
		}
		return m.Delete([]byte("legacyEnd"))
	})
	if err != nil {
		logResults.Error("Очистка старого снимка", "ошибка", err)
	}
}

func (s *ResultStore) PendingCount() int {
	count := 0
	_ = s.db.View(func(tx *bolt.Tx) error {
		count = int(tx.Bucket(rowsBucket).Sequence() - readNumber(tx.Bucket(metaBucket).Get([]byte("acked"))))
		return nil
	})
	return count
}
func (s *ResultStore) Close() { s.closeOnce.Do(func() { _ = s.db.Close() }) }
