@echo off
rem Прогон тестов Go-пробы в CentOS — той системе, где проба и работает.
rem
rem Зачем не «go test» на рабочей машине: половина проверок про сокеты и порты,
rem а Windows ведёт себя иначе — два UDP-сокета уживаются на одном порту, свой
rem эфемерный диапазон, нет /proc/net/udp. Всё, что касается аренды портов,
rem достоверно только на Linux.
rem
rem Нагрузочные проверки (TestLoad_) меняют net.ipv4.ip_local_port_range, поэтому
rem контейнеру нужен --privileged: настройка сетевая, правится в своём
rem пространстве имён и хозяйскую машину не задевает.
rem
rem Примеры:
rem   run-tests-centos.cmd                        — весь набор
rem   run-tests-centos.cmd -test.run TestLoad_ -test.v — только нагрузочные
setlocal
set IMAGE=twamp-probe-tests:centos9
set BIN=%TEMP%\twamp-probe.test

echo === Сборка тестового бинарника под Linux ===
set CGO_ENABLED=0
set GOOS=linux
set GOARCH=amd64
go test -c -o "%BIN%" . || exit /b 1

echo === Образ %IMAGE% ===
docker build -q -t %IMAGE% -f Dockerfile.tests . || exit /b 1

echo === Прогон ===
rem Каталог проекта монтируется только для чтения: часть проверок сверяет
rem поставляемые appsettings.json и 99-twamp-probe.conf с тем, что в коде.
docker run --rm --privileged ^
  -v "%BIN%:/twamp-probe.test:ro" ^
  -v "%CD%:/work:ro" ^
  -e PROBE_LOADTEST=1 ^
  -w /work ^
  %IMAGE% /twamp-probe.test -test.timeout 900s %*
