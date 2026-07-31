package main

import (
	"context"
	"os/exec"
	"runtime"
	"testing"
	"time"
)

func TestWaitForRuns_ReturnsImmediatelyWhenNothingRuns(t *testing.T) {
	l := NewAdaptiveLimiter(4, 1, 4)

	started := time.Now()
	waitForRuns(l, 2*time.Second)

	if elapsed := time.Since(started); elapsed > 300*time.Millisecond {
		t.Errorf("ожидание заняло %v, хотя запусков не было", elapsed)
	}
}

func TestWaitForRuns_WaitsUntilRunsFinish(t *testing.T) {
	// Остановка обязана дождаться работающих зондов: выйти раньше — значит
	// оставить их сиротами доживать замер.
	l := NewAdaptiveLimiter(4, 1, 4)
	if !l.Acquire(context.Background()) {
		t.Fatal("не удалось занять слот")
	}

	go func() {
		time.Sleep(200 * time.Millisecond)
		l.Release()
	}()

	started := time.Now()
	waitForRuns(l, 5*time.Second)

	if l.InFlight() != 0 {
		t.Errorf("ожидание закончилось, а выполняется ещё %d запусков", l.InFlight())
	}
	if elapsed := time.Since(started); elapsed < 150*time.Millisecond {
		t.Errorf("ожидание заняло всего %v — запуск не был дождан", elapsed)
	}
}

func TestWaitForRuns_GivesUpAfterTimeout(t *testing.T) {
	// Зависший зонд не должен держать остановку службы бесконечно.
	l := NewAdaptiveLimiter(4, 1, 4)
	l.Acquire(context.Background())

	started := time.Now()
	waitForRuns(l, 300*time.Millisecond)

	if elapsed := time.Since(started); elapsed > 2*time.Second {
		t.Errorf("ожидание затянулось на %v вместо отведённых 300 мс", elapsed)
	}
}

func TestProcessGroup_CancelKillsRunningProbe(t *testing.T) {
	// Проверка самого механизма: отмена контекста обязана снимать процесс
	// зонда, а не оставлять его работать после ухода пробы.
	sleeper := exec.Command("sleep", "60")
	if runtime.GOOS == "windows" {
		sleeper = exec.Command("ping", "-n", "60", "127.0.0.1")
	}
	if _, err := exec.LookPath(sleeper.Path); err != nil {
		t.Skipf("нет утилиты для проверки: %v", err)
	}

	ctx, cancel := context.WithCancel(context.Background())
	cmd := exec.CommandContext(ctx, sleeper.Path, sleeper.Args[1:]...)
	configureProcessGroup(cmd)
	if err := cmd.Start(); err != nil {
		t.Fatalf("не удалось запустить процесс: %v", err)
	}

	done := make(chan struct{})
	go func() { _ = cmd.Wait(); close(done) }()

	cancel() // как при остановке пробы
	select {
	case <-done:
	case <-time.After(5 * time.Second):
		_ = cmd.Process.Kill()
		t.Fatal("процесс зонда пережил отмену контекста — остался бы сиротой")
	}
}
