package main

import "testing"

func TestSystemRunCap_ReturnsSaneLimit(t *testing.T) {
	// Потолок обязан быть положительным и не выше бюджета потоков Go:
	// превысить жёсткий предел в 10000 потоков — это fatal error, которую
	// нельзя перехватить, служба просто умирает.
	limit, reason := systemRunCap()

	if limit <= 0 {
		t.Fatalf("потолок = %d, ожидалось положительное число", limit)
	}
	if limit > goThreadBudget {
		t.Errorf("потолок = %d выше бюджета потоков %d", limit, goThreadBudget)
	}
	if reason == "" {
		t.Error("не указано, чем ограничен потолок — в журнале это главное")
	}
	t.Logf("потолок системы: %d запусков (%s)", limit, reason)
}

func TestSystemRunCap_ProtectsAgainstDefaultRhelLimit(t *testing.T) {
	// Проверка расчёта на типовом значении CentOS/RHEL: ulimit -u = 4096.
	// Каждый зонд стоит двух единиц (дочерний процесс и поток ожидания),
	// поэтому без запаса проба упирается ровно на 2048 запусках — и падает.
	// Наш потолок обязан оказаться ниже этой границы.
	const rhelDefault = 4096
	got := nprocRunCap(rhelDefault)

	if got >= rhelDefault/unitsPerRun {
		t.Errorf("потолок %d не ниже опасной границы %d", got, rhelDefault/unitsPerRun)
	}
	t.Logf("при ulimit -u = %d потолок = %d запусков (граница падения — %d)",
		rhelDefault, got, rhelDefault/unitsPerRun)
}
