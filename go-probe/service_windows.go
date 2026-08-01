//go:build windows

// Работа пробы в качестве службы Windows.
//
// Служба Windows устроена не так, как демон Unix: остановка приходит не
// сигналом, а командой диспетчера служб (SCM), и на неё нужно ответить —
// сначала «останавливаюсь», потом «остановлена». Просто игнорировать это
// нельзя: SCM решит, что служба зависла, и убьёт процесс, не дав пробе
// прервать зонды и сохранить недоставленные результаты.
//
// Тот же исполняемый файл продолжает работать и из консоли: если процесс
// запущен не диспетчером служб, всё идёт по обычному пути с Ctrl+C.
package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	"golang.org/x/sys/windows/svc"
)

// serviceName — имя службы в диспетчере (совпадает с install-windows.ps1).
const serviceName = "TwampProbe"

// probeFinished закрывается, когда проба завершила остановку: прервала зонды
// и сохранила результаты. До этого момента службе рано сообщать SCM, что она
// остановлена.
var probeFinished = make(chan struct{})

// markProbeFinished сообщает, что работа завершена (вызывается при выходе из main).
func markProbeFinished() {
	select {
	case <-probeFinished: // уже закрыт
	default:
		close(probeFinished)
	}
}

// prepareServiceEnvironment переводит службу в каталог с исполняемым файлом.
//
// Служба Windows стартует с рабочим каталогом C:\Windows\System32: там проба
// не нашла бы ни appsettings.json, ни реестр задач, а журнал и результаты
// писала бы в системную папку. Из консоли рабочий каталог не трогаем — он
// выбран пользователем осознанно.
func prepareServiceEnvironment() {
	isService, err := svc.IsWindowsService()
	if err != nil || !isService {
		return
	}
	exe, err := os.Executable()
	if err != nil {
		return
	}
	if err := os.Chdir(filepath.Dir(exe)); err != nil {
		fmt.Fprintln(os.Stderr, "Не удалось перейти в каталог службы:", err)
	}
}

// shutdownContext возвращает контекст, который отменяется при остановке:
// по команде диспетчера служб — если проба запущена службой, по Ctrl+C или
// сигналу — если запущена из консоли.
func shutdownContext() (context.Context, context.CancelFunc) {
	isService, err := svc.IsWindowsService()
	if err != nil || !isService {
		return signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	}

	ctx, cancel := context.WithCancel(context.Background())
	go func() {
		// svc.Run возвращает управление, когда служба остановлена. Если
		// диспетчер отказал (запуск не как служба), просто снимаем контекст —
		// проба завершится штатно, а причина уйдёт в журнал.
		if err := svc.Run(serviceName, &probeService{cancel: cancel}); err != nil {
			logMain.Error("Не удалось подключиться к диспетчеру служб", "ошибка", err)
			cancel()
		}
	}()
	return ctx, cancel
}

// probeService — обработчик команд диспетчера служб.
type probeService struct {
	cancel context.CancelFunc
}

// Execute отвечает диспетчеру на команды жизненного цикла службы.
func (s *probeService) Execute(_ []string, requests <-chan svc.ChangeRequest,
	status chan<- svc.Status) (bool, uint32) {

	const accepted = svc.AcceptStop | svc.AcceptShutdown

	status <- svc.Status{State: svc.StartPending}
	status <- svc.Status{State: svc.Running, Accepts: accepted}

	for request := range requests {
		switch request.Cmd {
		case svc.Interrogate:
			status <- request.CurrentStatus

		case svc.Stop, svc.Shutdown:
			// Останавливаться проба может долго: она обрывает зонды и ждёт,
			// пока ядро снимет процессы. Держим SCM в курсе, иначе он сочтёт
			// службу зависшей и убьёт её вместе с недоставленными результатами.
			status <- svc.Status{State: svc.StopPending, WaitHint: uint32(serviceStopHint.Milliseconds())}
			s.cancel()

			select {
			case <-probeFinished:
			case <-time.After(serviceStopHint):
				logMain.Warn("Остановка затянулась — сообщаем диспетчеру служб о завершении",
					"ждали", serviceStopHint)
			}
			status <- svc.Status{State: svc.Stopped}
			return false, 0
		}
	}
	return false, 0
}

// serviceStopHint — сколько диспетчер служб ждёт нашей остановки. Берём с
// запасом относительно ожидания зондов внутри пробы (shutdownWait).
const serviceStopHint = 30 * time.Second
