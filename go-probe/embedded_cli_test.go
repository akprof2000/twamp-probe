package main

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"
)

// TestEmbeddedTwampy_CompareWithOriginal — не автотест, а инструмент сверки:
// выполняет замер встроенным отправителем по адресу из EMBEDDED_TARGET и печатает
// таблицу, чтобы сравнить её с выводом python-версии на том же рефлекторе.
func TestEmbeddedTwampy_CompareWithOriginal(t *testing.T) {
	target := os.Getenv("EMBEDDED_TARGET")
	if target == "" {
		t.Skip("EMBEDDED_TARGET не задан — сверка пропущена")
	}
	args := append([]string{target}, strings.Fields(os.Getenv("EMBEDDED_ARGS"))...)

	output, errText, _ := runEmbeddedTwampy(context.Background(), args, time.Now().Add(60*time.Second))
	if errText != "" {
		t.Fatalf("замер не удался: %s", errText)
	}
	t.Log("\n" + output)
}
