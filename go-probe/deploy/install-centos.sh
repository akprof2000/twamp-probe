#!/bin/bash
# Установка SPI TWamp Probe (Go) на CentOS/RHEL с настройками под максимум
# одновременных замеров.
#
# Что делает:
#   1. копирует папку пробы в /opt/twamp-probe-go;
#   2. ставит настройки ядра (/etc/sysctl.d/99-twamp-probe.conf) и применяет их;
#   3. ставит службу systemd с поднятыми лимитами и включает автозапуск;
#   4. показывает, какой потолок замеров получился и чем он ограничен.
#
# Запуск от root:  ./install-centos.sh
# Повторный запуск безопасен: настройки просто перезаписываются.

set -euo pipefail

DEST=/opt/twamp-probe-go
SRC=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" != "0" ]; then
    echo "Нужны права root: sudo $0" >&2
    exit 1
fi

echo "=== 1. Файлы пробы → $DEST"
mkdir -p "$DEST"
# Конфигурацию не затираем: на работающей пробе там свои настройки.
if [ -f "$DEST/appsettings.json" ]; then
    echo "    appsettings.json уже есть — оставляем как есть"
    find "$SRC" -maxdepth 1 -mindepth 1 ! -name appsettings.json -exec cp -r {} "$DEST/" \;
else
    cp -r "$SRC"/. "$DEST/"
fi
chmod +x "$DEST/twamp-probe"
[ -f "$DEST/twping" ] && chmod +x "$DEST/twping"

echo "=== 2. Настройки ядра"
install -m 0644 "$SRC/99-twamp-probe.conf" /etc/sysctl.d/99-twamp-probe.conf
sysctl --system >/dev/null
echo "    kernel.pid_max     = $(cat /proc/sys/kernel/pid_max)"
echo "    kernel.threads-max = $(cat /proc/sys/kernel/threads-max)"
echo "    диапазон портов    = $(cat /proc/sys/net/ipv4/ip_local_port_range)"

echo "=== 3. Служба systemd"
install -m 0644 "$SRC/twamp-probe.service" /etc/systemd/system/twamp-probe.service
systemctl daemon-reload
systemctl enable --now twamp-probe
sleep 2

echo "=== 4. Результат"
if ! systemctl is-active --quiet twamp-probe; then
    echo "    Служба не запустилась. Журнал:" >&2
    journalctl -u twamp-probe -n 20 --no-pager >&2
    exit 1
fi

PID=$(systemctl show -p MainPID --value twamp-probe)
echo "    служба работает, PID $PID"
echo "    лимит процессов службы: $(grep 'Max processes' "/proc/$PID/limits" | awk '{print $3}')"
echo "    лимит дескрипторов:     $(grep 'Max open files' "/proc/$PID/limits" | awk '{print $4}')"
echo
echo "Потолок одновременных замеров (из журнала пробы):"
journalctl -u twamp-probe -n 50 --no-pager | grep -E "Потолок|Проба запускается" | tail -2 || true
echo
echo "Готово. Дальше на сервере: «Статус проб» → «Опросить пробу» → «Подтвердить»."
