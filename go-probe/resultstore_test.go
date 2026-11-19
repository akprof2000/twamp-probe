// Тесты хранилища результатов: ACK-доставка, повторная выдача, вытеснение, очистка.
package main

import (
	"testing"
	"time"
)

// Пачка выдаётся повторно до подтверждения; после подтверждения — удаляется.
func TestResultStore_AckRedelivery(t *testing.T) {
	s := newTestStore(t, 100)

	s.Add(ActionData{ResultId: "r1"})
	first := s.TakeBatch(time.Second)
	if len(first.Items) != 1 || first.BatchId == EmptyGuid {
		t.Fatalf("ожидалась пачка из 1 результата, получено %d", len(first.Items))
	}

	second := s.TakeBatch(time.Millisecond)
	if second.BatchId != first.BatchId {
		t.Errorf("до подтверждения должна выдаваться та же пачка: %s != %s", second.BatchId, first.BatchId)
	}

	if !s.Confirm(first.BatchId) {
		t.Error("подтверждение существующей пачки должно вернуть true")
	}
	if s.Confirm(first.BatchId) {
		t.Error("повторное подтверждение должно вернуть false")
	}

	empty := s.TakeBatch(50 * time.Millisecond)
	if empty.BatchId != EmptyGuid || len(empty.Items) != 0 {
		t.Errorf("после подтверждения очередь должна быть пуста, получено %d", len(empty.Items))
	}
}

// Test helpers fail immediately on storage errors.
func (s *ResultStore) TakeBatch(timeout time.Duration) ResultBatch {
	batch, err := s.readBatch(timeout)
	if err != nil {
		panic(err)
	}
	return batch
}
func (s *ResultStore) Confirm(id string) bool {
	ok, err := s.confirm(id)
	if err != nil {
		panic(err)
	}
	return ok
}
