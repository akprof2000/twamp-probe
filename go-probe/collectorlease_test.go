package main

import (
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestOldScheduleCannotRestartRemovedProbe(t *testing.T) {
	api := apiServer{lease: collectorLease{route: deliveryRoute{Revision: 8}}}
	req := httptest.NewRequest("POST", "/api/probeinterface/setjobs", strings.NewReader(`[{"type":"Scheduler","id":"old"}]`))
	req.Header.Set("X-Twamp-Route-Revision", "7")
	req.Header.Set("If-Match", "\"old\"")
	response := httptest.NewRecorder()
	api.setJobs(response, req)
	if response.Code != 409 {
		t.Fatalf("removed assignment accepted a schedule: %d", response.Code)
	}
}

func TestScheduleRevisionIndependentOfOrder(t *testing.T) {
	a := TaskInfo{Id: "a", Title: "primary", Type: TypeScheduler}
	b := TaskInfo{Id: "b", Title: "secondary", Type: TypeScheduler}
	if scheduleTag([]TaskInfo{b, a}) != scheduleTag([]TaskInfo{a, b}) {
		t.Fatal("task enumeration changed the schedule version")
	}

}

func TestRetryAcknowledgesOnlyTheExactScheduleTransition(t *testing.T) {
	tasks, _ := newTestRegistry(t)
	defer tasks.ClearAll()
	api := apiServer{tasks: tasks, lease: collectorLease{route: deliveryRoute{Revision: 1, Members: []string{"a"}}}}
	post := func(tag, body string, want int) string {
		t.Helper()
		req := httptest.NewRequest("POST", "/api/probeinterface/setjobs", strings.NewReader(body))
		req.Header.Set("X-Twamp-Route-Revision", "1")
		req.Header.Set("If-Match", tag)
		response := httptest.NewRecorder()
		api.setJobs(response, req)
		if response.Code != want {
			t.Fatalf("SetJobs got %d, want %d: %s", response.Code, want, response.Body.String())
		}
		return response.Header().Get("ETag")
	}
	initial := post(scheduleTag(tasks.AllTasks()), `[{"id":"x","type":"Scheduler"},{"id":"y","type":"Scheduler"}]`, 200)
	edited := post(initial, `[{"id":"y","type":"Scheduler","title":"new"}]`, 200)
	remove := `[{"id":"x","type":"Scheduler","delete":true}]`
	final := post(edited, remove, 200)
	if tag := post(edited, remove, 200); tag != final {
		t.Fatalf("retry changed version: %s", tag)
	}
	post(initial, remove, 409)
}

func TestCollectorLeaseRejectsStaleDelivery(t *testing.T) {
	l := collectorLease{route: deliveryRoute{Revision: 3, Members: []string{"a", "b", "c"}}}
	now := time.Now()
	a := leaseCommand{Revision: 3, Collector: "a", Instance: "proc1", Action: "acquire"}
	c := leaseCommand{Revision: 3, Collector: "c", Instance: "proc3", Action: "acquire"}
	old := l.change(a, now)
	if old == "" || l.change(c, now) != "" {
		t.Fatal("overlapping owners")
	}
	later := now.Add(collectorLeaseTTL)
	current := l.change(c, later)
	if current == "" || old == current || l.valid(old, later) {
		t.Fatal("old token survived takeover")
	}
	a.Action = "release"
	a.Token = old
	if l.change(a, later) != "" || !l.valid(current, later) {
		t.Fatal("stale release removed current owner")
	}
	c.Revision = 2
	if l.change(c, later.Add(collectorLeaseTTL)) != "" {
		t.Fatal("stale configuration acquired lease")
	}
}

func TestStaleAckCannotRemoveCurrentBatch(t *testing.T) {
	store := newTestStore(t, 10)
	store.Add(ActionData{ResultId: "fixture"})
	batch := store.TakeBatch(time.Second)
	api := apiServer{cfg: &Config{ApiKey: "fixture"}, results: store}
	api.lease.route = deliveryRoute{Revision: 1, Members: []string{"a", "c"}}
	old := api.lease.change(leaseCommand{Revision: 1, Collector: "a", Instance: "first", Action: "acquire"}, time.Now().Add(-2*collectorLeaseTTL))
	current := api.lease.change(leaseCommand{Revision: 1, Collector: "c", Instance: "third", Action: "acquire"}, time.Now())
	for _, token := range []string{"", old, current} {
		req := httptest.NewRequest("POST", "/api/probeinterface/confirmdata?batchId="+batch.BatchId, nil)
		req.Header.Set("X-Twamp-Commit", "clickhouse")
		req.Header.Set(leaseHeader, token)
		response := httptest.NewRecorder()
		api.confirmData(response, req)
		if token != current {
			if response.Code != 409 || store.TakeBatch(time.Millisecond).BatchId != batch.BatchId {
				t.Fatal("stale ACK removed batch")
			}
		} else if response.Code != 200 || len(store.TakeBatch(time.Millisecond).Items) != 0 {
			t.Fatal("current owner could not acknowledge")
		}
	}
}
