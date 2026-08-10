#!/bin/bash
# Установка и обновление SPI TWamp Probe (Go) на Linux с systemd — одним
# скриптом. Первый запуск ставит, каждый следующий обновляет; отличать их
# самому не нужно.
#
# Что делает:
#   1. копирует папку пробы в /opt/twamp-probe-go;
#   2. сливает настройки: ваши значения остаются, новые ключи добавляются;
#   3. ставит настройки ядра (/etc/sysctl.d/99-twamp-probe.conf) и применяет их;
#   4. ставит службу systemd с поднятыми лимитами и включает автозапуск;
#   5. показывает, какой потолок замеров получился и чем он ограничен.
#
# Запуск от root:  ./install.sh

set -euo pipefail

DEST=/opt/twamp-probe-go
SRC=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" != "0" ]; then
    echo "Нужны права root: sudo $0" >&2
    exit 1
fi

# Установка и обновление — один и тот же путь, разница только в том, что
# сообщать человеку: при обновлении важно, что стало с его настройками.
if [ -f "$DEST/appsettings.json" ]; then
    MODE=обновление
else
    MODE=установка
fi
echo "=== SPI TWamp Probe: $MODE"

echo "=== 1. Файлы пробы → $DEST"
# Работающую службу надо остановить: иначе копирование поверх запущенного
# файла падает с «Text file busy», и обновление версии не проходит.
if systemctl is-active --quiet twamp-probe 2>/dev/null; then
    echo "    останавливаем работающую службу"
    systemctl stop twamp-probe
fi
mkdir -p "$DEST"
# Файл настроек копируем отдельно — ниже он не заменяется, а дополняется.
find "$SRC" -maxdepth 1 -mindepth 1 ! -name appsettings.json -exec cp -r {} "$DEST/" \;
chmod +x "$DEST/twamp-probe"
[ -f "$DEST/twping" ] && chmod +x "$DEST/twping"

echo "=== 2. Настройки"
# Затирать чужой файл эталонным нельзя — там ключ API, адреса, диапазоны. Но и
# оставлять как есть мало: в новой версии появляются настройки, о которых иначе
# никто не узнает. Поэтому слияние: значения администратора остаются, новые
# ключи добавляются рядом. Разбирает JSON сама проба — jq на минимальной
# системе может не быть, а бинарник лежит рядом всегда.
if [ -f "$DEST/appsettings.json" ]; then
    ADDED=$("$DEST/twamp-probe" --merge-config "$DEST/appsettings.json" "$SRC/appsettings.json")
    if [ -n "$ADDED" ]; then
        echo "    ваши значения сохранены, добавлены новые настройки:"
        echo "$ADDED" | sed 's/^/        /'
    else
        echo "    ваш appsettings.json уже полон — оставлен без изменений"
    fi
else
    cp "$SRC/appsettings.json" "$DEST/appsettings.json"
    echo "    appsettings.json взят из пакета"
fi

echo "=== 3. Настройки ядра"
install -m 0644 "$SRC/99-twamp-probe.conf" /etc/sysctl.d/99-twamp-probe.conf
sysctl --system >/dev/null
echo "    kernel.pid_max     = $(cat /proc/sys/kernel/pid_max)"
echo "    kernel.threads-max = $(cat /proc/sys/kernel/threads-max)"
echo "    диапазон портов    = $(cat /proc/sys/net/ipv4/ip_local_port_range)"

echo "=== 4. Служба systemd"
install -m 0644 "$SRC/twamp-probe.service" /etc/systemd/system/twamp-probe.service
systemctl daemon-reload
systemctl enable twamp-probe >/dev/null
# Именно restart: при обновлении версии служба уже работает, и «--now»
# оставил бы в памяти старый процесс.
systemctl restart twamp-probe
sleep 2

echo "=== 5. Результат"
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
echo "Готово ($MODE). Дальше на сервере: «Статус проб» → «Опросить пробу» → «Подтвердить»."
