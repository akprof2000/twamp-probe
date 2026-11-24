package main

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestQuorumModeRejectsLegacyCommit(t *testing.T) {
	api := apiServer{cfg: &Config{RequireQuorumAck: true}}
	req := httptest.NewRequest(http.MethodPost, "/api/probeinterface/confirmdata", nil)
	req.Header.Set("X-Twamp-Commit", "clickhouse")
	response := httptest.NewRecorder()
	api.confirmData(response, req)
	if response.Code != http.StatusPreconditionRequired {
		t.Fatalf("expected 428 for a single-replica commit, got %d", response.Code)
	}
}
