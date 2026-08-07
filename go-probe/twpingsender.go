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
	"strconv"
	"strings"
	"time"

	"github.com/akprof2000/twping-go/twping"
)

// runEmbeddedTwping выполняет замер встроенным клиентом TWamp.
//
// Аргументы — те же, что у внешней утилиты (адрес узла последним). Возвращает
// текст вывода и текст ошибки; пустая ошибка означает успешный замер.
func runEmbeddedTwping(ctx context.Context, args []string, deadline time.Time) (output, errText string, leaked bool) {
	// Индивидуальный таймаут задачи — тот же контекст, что и отмена: клиент
	// прерывается и на подключении, и во время сессии.
	if !deadline.IsZero() {
		var cancel context.CancelFunc
		ctx, cancel = context.WithDeadline(ctx, deadline)
		defer cancel()
	}

	// Клиент запускается под сторожем, а не напрямую. Контекст останавливает
	// его не везде: время ограничено только у подключения (30 секунд у Dialer),
	// а дальше — чтение приветствия и весь обмен по управляющему каналу — идёт
	// без дедлайнов и на отмену не смотрит. Достаточно рефлектору принять
	// TCP-соединение и замолчать, и замер повисает навсегда: ни таймаут задачи,
	// ни её удаление его не снимут.
	//
	// Чем это кончается, видно по боевой пробе: замеры копятся, задачи вечно
	// числятся выполняющимися (новые запуски пропускаются), а их порты остаются
	// в аренде — пул исчерпывается, и в нём не остаётся свободных номеров.
	//
	// Поэтому ждём ровно столько, сколько отпущено задаче, и возвращаемся.
	// Настоящее лечение — дедлайны на управляющем канале в twping-go; здесь
	// проба защищает себя тем, что ей доступно.
	var out, errOut bytes.Buffer
	done := make(chan error, 1)
	go func() { done <- twping.Run(ctx, args, &out, &errOut) }()

	var err error
	select {
	case err = <-done:
	case <-ctx.Done():
		// Отмена контекста закрывает управляющее соединение, и клиент сворачивается
		// сам — но не мгновенно. Дадим ему это доделать: тогда сокеты закрыты,
		// и порт возвращается в пул как обычно.
		timedOut := ctx.Err() == context.DeadlineExceeded
		select {
		case <-done:
		case <-time.After(probeWrapUp):
			// Не успел — значит держит сокеты до сих пор. Порт занят, сколько бы
			// учёт ни думал иначе: признак leaked уводит номер в карантин.
			if timedOut {
				return out.String(), errTimeout.Error(), true
			}
			return out.String(), errCancelled.Error(), true
		}
		if timedOut {
			return out.String(), errTimeout.Error(), false
		}
		return out.String(), errCancelled.Error(), false
	}

	output = out.String()
	switch {
	case err == nil:
		return output, strings.TrimSpace(errOut.String()), false

	case ctx.Err() == context.DeadlineExceeded:
		// Клиент вернулся сам — сокеты закрыты, порт свободен.
		return output, errTimeout.Error(), false

	case ctx.Err() == context.Canceled:
		return output, errCancelled.Error(), false
	}

	// Диагностика клиента информативнее самой ошибки, поэтому показываем обе.
	message := fmt.Sprintf("Ошибка встроенного twping: %v", err)
	if extra := strings.TrimSpace(errOut.String()); extra != "" {
		message += ": " + extra
	}
	return output, message + portExhaustionHint(message, args), false
}

// probeWrapUp — сколько ждать, пока прерванный клиент закроет свои сокеты.
//
// Отмена контекста закрывает управляющее соединение; клиенту остаётся выйти из
// чтения и свернуть сессию — это доли секунды. Секунды хватает с запасом, а
// дольше ждать незачем: замер всё равно уже не состоится, а порт лучше увести
// в карантин, чем держать замер в очереди.
const probeWrapUp = time.Second

// portExhaustionHint добавляет подсказку, когда замеры упёрлись в порты. Сама
// ошибка ядра об этом не говорит: «address already in use» при привязке к
// порту 0 выглядит как конфликт, хотя означает, что свободных портов в
// диапазоне не осталось.
//
// Подсказки две, и путать их нельзя. Если диапазон зонду задала проба (пул
// выдал один номер, отсюда «-P N-N»), кончиться там нечему: занят ровно этот
// номер, и виноват чужой сокет, а не размер эфемерного диапазона. Совет
// «расширьте ip_local_port_range» в этом случае уводит не туда — лечится
// такое разведением диапазонов пула и ядра.
//
// Если же диапазон не задан, порт выбирает ядро, и тогда действительно могли
// закончиться эфемерные порты: каждый замер TWAMP занимает два — TCP для
// управляющего соединения и UDP для тестового, — поэтому штатные 32768–60999
// (около 28 тысяч) кончаются на нескольких тысячах одновременных замеров.
func portExhaustionHint(message string, args []string) string {
	if !strings.Contains(message, "address already in use") {
		return ""
	}
	if port, single := singlePortRange(args); single {
		return fmt.Sprintf(". Порт %d выдан пулом пробы (Probe:PortRange) и занят"+
			" посторонним процессом: проба уведёт его в карантин и повторит замер"+
			" с другим номером. Если это повторяется, диапазон пула пересекается"+
			" с эфемерным диапазоном ядра — разведите их"+
			" (sysctl net.ipv4.ip_local_port_range либо ip_local_reserved_ports,"+
			" см. 99-twamp-probe.conf из пакета)", port)
	}
	return ". Похоже, закончились свободные порты: каждый замер занимает два. " +
		"Расширьте диапазон (sysctl net.ipv4.ip_local_port_range = 1024 65535 — " +
		"он есть в 99-twamp-probe.conf из пакета) или уменьшите Probe:MaxParallel"
}

// singlePortRange распознаёт вырожденный диапазон «-P N-N» — признак того, что
// номер зонду выдал пул пробы, а не выбрал администратор.
func singlePortRange(args []string) (int, bool) {
	idx := indexOf(args, "-P")
	if idx < 0 || idx+1 >= len(args) {
		return 0, false
	}

	low, high, ok := strings.Cut(args[idx+1], "-")
	if !ok || low != high {
		return 0, false
	}
	port, err := strconv.Atoi(low)
	if err != nil {
		return 0, false
	}
	return port, true
}
