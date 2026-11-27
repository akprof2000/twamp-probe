package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"time"

	bolt "go.etcd.io/bbolt"
)

// Run on Linux, where the open and locked replacement can be renamed atomically.
func (s *ResultStore) runCompaction(ctx context.Context, minBytes int64) {
	if runtime.GOOS != "linux" || minBytes <= 0 {
		return
	}
	retry := time.NewTicker(time.Minute)
	defer retry.Stop()
	for ctx.Err() == nil {
		wake := s.drained
		if err := s.compactIfEmpty(minBytes); err != nil {
			logResults.Warn("Не удалось уменьшить results.db", "ошибка", err)
			wake = nil // Retry once a minute when storage is unavailable.
		}
		select {
		case <-ctx.Done():
			return
		case <-wake:
		case <-retry.C:
		}
	}
}

func (s *ResultStore) compactIfEmpty(minBytes int64) error {
	info, err := os.Stat(s.path)
	if err != nil || info.Size() < minBytes {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	var empty bool
	err = s.db.View(func(tx *bolt.Tx) error {
		rows, meta := tx.Bucket(rowsBucket), tx.Bucket(metaBucket)
		first, _ := rows.Cursor().First()
		empty = first == nil && rows.Sequence() == readNumber(meta.Get([]byte("acked"))) &&
			meta.Get([]byte("batch")) == nil
		return nil
	})
	if err != nil || !empty {
		return err
	}

	dir, err := os.Open(filepath.Dir(s.path))
	if err != nil {
		return err
	}
	defer dir.Close()
	tmp := s.path + ".compacting"
	// Only the holder of the live database lock reaches here. A crash before rename
	// may leave this scratch file. The original results.db remains authoritative.
	if err := os.Remove(tmp); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	next, err := bolt.Open(tmp, info.Mode().Perm()&0600, &bolt.Options{Timeout: time.Second})
	if err != nil {
		return err
	}
	installed := false
	defer func() {
		if !installed {
			_ = next.Close()
			_ = os.Remove(tmp)
		}
	}()
	// Copy all buckets and sequence numbers, including route, ACK and migration markers.
	if err := bolt.Compact(next, s.db, 0); err != nil {
		return err
	}
	if err := next.Sync(); err != nil {
		return err
	}
	size, err := os.Stat(tmp)
	if err != nil || size.Size() >= info.Size() {
		return err
	}
	if err := os.Rename(tmp, s.path); err != nil {
		return err
	}
	old := s.db
	s.db = next
	installed = true
	syncErr := dir.Sync()
	closeErr := old.Close()
	if err := errors.Join(syncErr, closeErr); err != nil {
		return fmt.Errorf("после замены results.db: %w", err)
	}
	logResults.Info("Размер results.db уменьшен", "было_байт", info.Size(), "стало_байт", size.Size())
	return nil
}
