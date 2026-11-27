package main

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
	"time"

	bolt "go.etcd.io/bbolt"
)

func TestCompactDrainedQueue(t *testing.T) {
	dir := t.TempDir()
	open := func() *ResultStore {
		s, err := OpenResultStore(dir, 8)
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(s.Close)
		return s
	}
	s := open()
	if err := s.update(func(tx *bolt.Tx) error { return tx.Bucket(metaBucket).Put(routeKey, []byte("route-7")) }); err != nil {
		t.Fatal(err)
	}
	size := func() (int64, int64) {
		info, err := os.Stat(s.path)
		if err != nil {
			t.Fatal(err)
		}
		return info.Size(), info.Sys().(*syscall.Stat_t).Blocks * 512
	}
	fill := func(n int) {
		for range n {
			if err := s.Add(ActionData{ResultId: NewGuid(), Console: strings.Repeat("measurement ", 8192)}); err != nil {
				t.Fatal(err)
			}
		}
	}
	drain := func() string {
		last := ""
		for s.PendingCount() > 0 {
			last = s.TakeBatch(time.Second).BatchId
			ok, err := s.confirm(last, "primary")
			if err != nil || !ok {
				t.Fatalf("ack: %v %v", ok, err)
			}
		}
		return last
	}
	fill(80)
	before, physical := size()
	batch := s.TakeBatch(time.Second)
	const threshold = 1 << 20
	if err := s.compactIfEmpty(threshold); err != nil {
		t.Fatal(err)
	}
	if retry := s.TakeBatch(time.Second); retry.BatchId != batch.BatchId || s.PendingCount() != 80 {
		t.Fatal("changed pending batch")
	}
	drain()
	drained, _ := size()
	fill(80)
	reused, _ := size()
	last := drain()
	// Simulate a failed attempt. A nonempty staging directory must not be removed.
	tmp := s.path + ".compacting"
	if err := os.Mkdir(tmp, 0700); err != nil {
		t.Fatal(err)
	}
	marker := filepath.Join(tmp, "keep")
	if err := os.WriteFile(marker, []byte("test"), 0600); err != nil {
		t.Fatal(err)
	}
	if s.compactIfEmpty(threshold) == nil {
		t.Fatal("unexpected replacement")
	}
	if err := os.Remove(marker); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(tmp); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s = open()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() { defer close(done); s.runCompaction(ctx, threshold) }()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if n, _ := size(); n < threshold {
			break
		}
		time.Sleep(10 * time.Millisecond)
	}
	cancel()
	<-done
	after, allocated := size()
	t.Logf("bytes: before ACK=%d, drained without compaction=%d, refilled=%d, compacted=%d; allocated blocks in bytes: before=%d after=%d", before, drained, reused, after, physical, allocated)
	if after >= threshold || allocated >= physical {
		t.Fatal("disk space was not reclaimed")
	}
	s.Close()
	s = open()
	count, ack, err := s.deliveryState()
	if err != nil || count != 0 || ack == nil || ack.Batch != last || ack.Collector != "primary" {
		t.Fatalf("lost delivery metadata: %v %v %v", count, ack, err)
	}
	if err := s.view(func(tx *bolt.Tx) error {
		if string(tx.Bucket(metaBucket).Get(routeKey)) != "route-7" {
			t.Fatal("lost route")
		}
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	fill(1)
	if s.PendingCount() != 1 || drain() == last {
		t.Fatal("sequence did not survive compaction")
	}
}
