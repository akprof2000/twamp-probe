// Встроенный зонд TWamp: клиент twping работает прямо в процессе пробы.
//
// Зачем: замер внешней утилитой стоит системе процесса и потока, который его
// ждёт, — именно этим ограничено число одновременных замеров
// (см. docs/parallelism.md). Встроенный клиент не запускает процесс вовсе:
// замер идёт в горутине, поэтому предел «процесс + поток» на него не действует.
//
// Код клиента не дублируется: это библиотека github.com/akprof2000/twping-go,
// та же самая, из которой собирается утилита twping. Вывод получается
// побайтово тот же, что у внешнего вызова, поэтому серверный парсер и отчёты
// не отличают один режим от другого.
package main

import (
	"bytes"
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/akprof2000/twping-go/twping"
)

// runEmbeddedTwping выполняет замер встроенным клиентом TWamp.
//
// Аргументы — те же, что у внешней утилиты (адрес узла последним). Возвращает
// текст вывода и текст ошибки; пустая ошибка означает успешный замер.
func runEmbeddedTwping(ctx context.Context, args []string, deadline time.Time) (output, errText string) {
	// Индивидуальный таймаут задачи — тот же контекст, что и отмена: клиент
	// прерывается и на подключении, и во время сессии.
	if !deadline.IsZero() {
		var cancel context.CancelFunc
		ctx, cancel = context.WithDeadline(ctx, deadline)
		defer cancel()
	}

	var out, errOut bytes.Buffer
	err := twping.Run(ctx, args, &out, &errOut)

	output = out.String()
	switch {
	case err == nil:
		return output, strings.TrimSpace(errOut.String())

	case ctx.Err() == context.DeadlineExceeded:
		return output, errTimeout.Error()

	case ctx.Err() == context.Canceled:
		return output, errCancelled.Error()
	}

	// Диагностика клиента информативнее самой ошибки, поэтому показываем обе.
	message := fmt.Sprintf("Ошибка встроенного twping: %v", err)
	if extra := strings.TrimSpace(errOut.String()); extra != "" {
		message += ": " + extra
	}
	return output, message + portExhaustionHint(message)
}

// portExhaustionHint добавляет подсказку, когда замеры упёрлись в эфемерные
// порты. Сама ошибка ядра об этом не говорит: «address already in use» при
// привязке к порту 0 выглядит как конфликт, хотя означает, что свободных
// портов в диапазоне не осталось.
//
// Каждый замер TWAMP занимает два порта — TCP для управляющего соединения и
// UDP для тестового, — поэтому штатные 32768–60999 (около 28 тысяч) кончаются
// на нескольких тысячах одновременных замеров.
func portExhaustionHint(message string) string {
	if !strings.Contains(message, "address already in use") {
		return ""
	}
	return ". Похоже, закончились свободные порты: каждый замер занимает два. " +
		"Расширьте диапазон (sysctl net.ipv4.ip_local_port_range = 1024 65535 — " +
		"он есть в 99-twamp-probe.conf из пакета) или уменьшите Probe:MaxParallel"
}
