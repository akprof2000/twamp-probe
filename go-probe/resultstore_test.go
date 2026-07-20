// Тесты хранилища результатов: ACK-доставка, повторная выдача, вытеснение, очистка.
package main

import (
	"testing"
	"time"
)

func newTestStore(maxPending int) *ResultStore {
	s := NewResultStore(maxPending, 3600) // персист раз в час — в тестах не мешает
	t := s
	return t
}

// Пачка выдаётся повторно до подтверждения; после подтверждения — удаляется.
func TestResultStore_AckRedelivery(t *testing.T) {
	s := newTestStore(100)
	defer s.Close()

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

// При переполнении вытесняются самые старые результаты.
func TestResultStore_DropOldest(t *testing.T) {
	s := newTestStore(2)
	defer s.Close()

	s.Add(ActionData{ResultId: "old"})
	s.Add(ActionData{ResultId: "mid"})
	s.Add(ActionData{ResultId: "new"})

	batch := s.TakeBatch(time.Second)
	if len(batch.Items) != 2 {
		t.Fatalf("лимит 2: ожидалось 2 результата, получено %d", len(batch.Items))
	}
	if batch.Items[0].ResultId != "mid" || batch.Items[1].ResultId != "new" {
		t.Errorf("должны остаться два самых новых, получено %s, %s",
			batch.Items[0].ResultId, batch.Items[1].ResultId)
	}
}

// Clear убирает и очередь, и неподтверждённую пачку.
func TestResultStore_Clear(t *testing.T) {
	s := newTestStore(100)
	defer s.Close()

	s.Add(ActionData{ResultId: "r1"})
	_ = s.TakeBatch(time.Second) // пачка «в полёте»
	s.Add(ActionData{ResultId: "r2"})

	s.Clear()

	empty := s.TakeBatch(50 * time.Millisecond)
	if len(empty.Items) != 0 {
		t.Errorf("после Clear результатов быть не должно, получено %d", len(empty.Items))
	}
}
