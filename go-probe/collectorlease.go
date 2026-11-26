package main

import (
	"encoding/json"
	"net/http"
	"slices"
	"sync"
	"time"

	bolt "go.etcd.io/bbolt"
)

const collectorLeaseTTL = 15 * time.Second
const leaseHeader = "X-Twamp-Lease"

type deliveryRoute struct {
	Revision int64       `json:"revision"`
	Members  []string    `json:"members"`
	Peers    []routePeer `json:"peers"`
	Name     string      `json:"name"`
}
type routePeer struct {
	ID  string `json:"id"`
	URL string `json:"url"`
}
type leaseCommand struct {
	Action    string
	Collector string
	Instance  string
	Token     string
	Revision  int64
}

// Only the incoming request can change ownership. The probe never contacts a collector.
type collectorLease struct {
	mu                     sync.Mutex
	route                  deliveryRoute
	instance, owner, token string
	expires                time.Time
}

func (l *collectorLease) valid(token string, now time.Time) bool {
	return token != "" && l.token == token && now.Before(l.expires)
}

func (l *collectorLease) change(cmd leaseCommand, now time.Time) string {
	l.mu.Lock()
	defer l.mu.Unlock()
	if cmd.Revision == 0 || cmd.Revision != l.route.Revision || !slices.Contains(l.route.Members, cmd.Collector) {
		return ""
	}
	switch cmd.Action {
	case "release", "renew":
		if !l.valid(cmd.Token, now) || l.owner != cmd.Collector || l.instance != cmd.Instance {
			return ""
		}
		if cmd.Action == "release" {
			l.token = ""
		} else {
			l.expires = now.Add(collectorLeaseTTL)
		}
		return cmd.Token
	case "acquire":
		if cmd.Instance == "" || l.valid(l.token, now) {
			return ""
		}
		l.instance, l.owner, l.token = cmd.Instance, cmd.Collector, NewGuid()
		l.expires = now.Add(collectorLeaseTTL)
		return l.token
	}
	return ""
}

var routeKey = []byte("collectorRouteV1")

func (a *apiServer) loadRoute() error {
	return a.results.db.View(func(tx *bolt.Tx) error {
		data := tx.Bucket(metaBucket).Get(routeKey)
		if data == nil {
			return nil
		}
		return json.Unmarshal(data, &a.lease.route)
	})
}
func (a *apiServer) configureRoute(w http.ResponseWriter, r *http.Request) {
	if a.cfg.RequireQuorumAck {
		http.Error(w, "disable Probe:RequireQuorumAck for ordered collectors", 412)
		return
	}
	if a.cfg.ApiKey == "" {
		http.Error(w, "configure Auth:ApiKey for ordered collectors", 403)
		return
	}
	if r.Method == http.MethodGet {
		a.lease.mu.Lock()
		route := a.lease.route
		a.lease.mu.Unlock()
		writeJSON(w, route)
		return
	}
	var input struct {
		deliveryRoute
		ExpectedRevision *int64 `json:"expectedRevision"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 16384)).Decode(&input); err != nil || input.ExpectedRevision == nil || input.Revision <= *input.ExpectedRevision || len(input.Members) > 3 || len(input.Peers) != len(input.Members) {
		http.Error(w, "invalid route", 400)
		return
	}
	route := input.deliveryRoute
	for i, id := range route.Members {
		if id == "" || slices.Contains(route.Members[:i], id) || route.Peers[i].ID != id || route.Peers[i].URL == "" {
			http.Error(w, "invalid collector IDs", 400)
			return
		}
	}
	l := &a.lease
	l.mu.Lock()
	defer l.mu.Unlock()
	if route.Revision == l.route.Revision && (!slices.Equal(route.Peers, l.route.Peers) || route.Name != l.route.Name) ||
		route.Revision != l.route.Revision && *input.ExpectedRevision != l.route.Revision {
		http.Error(w, "stale route", 409)
		return
	}
	if route.Revision != l.route.Revision {
		data, err := json.Marshal(route)
		if err == nil {
			err = a.results.db.Update(func(tx *bolt.Tx) error { return tx.Bucket(metaBucket).Put(routeKey, data) })
		}
		if err != nil {
			http.Error(w, "could not persist route", 503)
			return
		}
		l.route = route
		l.token = ""
		a.tracker.autonomous.Store(len(route.Members) > 0)
	}
	if len(route.Members) == 0 {
		a.tasks.ClearAll()
	}
	writeJSON(w, route.Revision)
}
func (a *apiServer) leaseRequest(w http.ResponseWriter, r *http.Request) {
	if a.cfg.ApiKey == "" {
		http.Error(w, "configure Auth:ApiKey for ordered collectors", 403)
		return
	}
	if r.Method == http.MethodGet {
		a.lease.mu.Lock()
		rev := a.lease.route.Revision
		a.lease.mu.Unlock()
		writeJSON(w, map[string]int64{"revision": rev})
		return
	}
	var cmd leaseCommand
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&cmd); err != nil {
		http.Error(w, "invalid lease", 400)
		return
	}
	if token := a.lease.change(cmd, time.Now()); token != "" {
		writeJSON(w, map[string]string{"token": token})
	} else {
		http.Error(w, "collector lease unavailable", 409)
	}
}

// Caller holds the mutex across the acknowledgement and its queue transaction.
func (a *apiServer) allowDelivery(w http.ResponseWriter, r *http.Request) bool {
	if a.lease.route.Revision != 0 && a.cfg.ApiKey == "" {
		http.Error(w, "configure Auth:ApiKey for ordered collectors", 403)
		return false
	}
	token := r.Header.Get(leaseHeader)
	if a.lease.route.Revision == 0 && token == "" || a.lease.valid(token, time.Now()) {
		return true
	}
	http.Error(w, "current collector lease required", 409)
	return false
}
