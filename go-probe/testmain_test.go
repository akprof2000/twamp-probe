package main

import (
	"fmt"
	"os"
	"testing"
)

var testSources string

// Run the entire test process outside the checkout, including subprocesses and timers.
func TestMain(m *testing.M) {
	var err error
	testSources, err = os.Getwd()
	if err != nil {
		panic(err)
	}
	dir, err := os.MkdirTemp("", "twamp-probe-test-*")
	if err != nil {
		panic(err)
	}
	if err := os.Chdir(dir); err != nil {
		panic(err)
	}
	code := m.Run()
	if err := os.Chdir(testSources); err != nil {
		panic(err)
	}
	if err := os.RemoveAll(dir); err != nil {
		fmt.Fprintln(os.Stderr, err)
		code = 1
	}
	os.Exit(code)
}

func newTestStore(t *testing.T, batchSize int) *ResultStore {
	t.Helper()
	store, err := OpenResultStore(t.TempDir(), batchSize)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(store.Close)
	return store
}
