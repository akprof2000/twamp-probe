package main

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestQueueSurvivesRestartUntilExactAck(t *testing.T) {
	dir := t.TempDir()
	open := func() *ResultStore {
		s, err := OpenResultStore(dir, 2)
		if err != nil {
			t.Fatal(err)
		}
		return s
	}
	legacy := filepath.Join(dir, "JobResult.json")
	if err := os.WriteFile(legacy, []byte(`[{"resultId":"legacy"}]`), 0600); err != nil {
		t.Fatal(err)
	}
	s := open()
	for _, id := range []string{"second", "third", "fourth"} {
		if err := s.Add(ActionData{ResultId: id}); err != nil {
			t.Fatal(err)
		}
	}
	first := s.TakeBatch(time.Second)
	if len(first.Items) != 2 || first.Items[0].ResultId != "legacy" {
		t.Fatal(first)
	}
	s.Close()
	s = open()
	defer s.Close()
	retry := s.TakeBatch(time.Second)
	if retry.BatchId != first.BatchId || len(retry.Items) != 2 || retry.Items[1].ResultId != "second" {
		t.Fatal(retry)
	}
	if s.Confirm(NewGuid()) || s.PendingCount() != 4 {
		t.Fatal("stale ACK modified queue")
	}
	if _, err := os.Stat(legacy); err != nil {
		t.Fatal(err)
	}
	if !s.Confirm(first.BatchId) {
		t.Fatal("ACK rejected")
	}
	rest := s.TakeBatch(time.Second)
	if len(rest.Items) != 2 || rest.Items[0].ResultId != "third" || rest.Items[1].ResultId != "fourth" {
		t.Fatal(rest)
	}
	if !s.Confirm(rest.BatchId) || s.PendingCount() != 0 {
		t.Fatal("queue did not drain")
	}
	if _, err := os.Stat(legacy); !os.IsNotExist(err) {
		t.Fatal("confirmed legacy snapshot still present", err)
	}
}
