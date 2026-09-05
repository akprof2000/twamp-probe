#!/bin/bash
# Установка SPI TWamp Server на CentOS/RHEL как службы systemd.
#
# Что делает:
#   1. заводит системного пользователя twamp (сервер не требует root);
#   2. копирует файлы в /opt/twamp-server;
#   3. сливает настройки: ваши значения остаются, новые ключи добавляются;
#   4. ставит службу systemd и включает автозапуск;
#   5. при необходимости открывает порт в firewalld;
#   6. проверяет, что сервер отвечает.
#
# Запуск от root:  ./install.sh
#
# Первый запуск ставит, каждый следующий обновляет — отличать их самому не
# нужно. При обновлении база и буфер не трогаются, а appsettings.json
# дополняется появившимися настройками: ваши значения остаются как есть.

set -euo pipefail

DEST=/opt/twamp-server
USER_NAME=twamp
SRC=$(cd "$(dirname "$0")" && pwd)

if [ "$(id -u)" != "0" ]; then
    echo "Нужны права root: sudo $0" >&2
    exit 1
fi

# Вариант framework требует установленного .NET; самодостаточному он не нужен.
if [ ! -f "$SRC/SPI.Twamp.Server" ]; then
    echo "Рядом со скриптом нет файлов сервера — запускайте его из папки публикации" >&2
    exit 1
fi
if ls "$SRC"/*.dll >/dev/null 2>&1 && [ ! -f "$SRC/libcoreclr.so" ]; then
    if ! command -v dotnet >/dev/null; then
        echo "Это сборка framework — нужен .NET 10 Runtime (ASP.NET Core):" >&2
        echo "  dnf install -y aspnetcore-runtime-10.0" >&2
        echo "Либо возьмите архив selfcontained — ему .NET не нужен." >&2
        exit 1
    fi
fi

# Установка и обновление — один и тот же путь, разница только в том, что
# сообщать человеку: при обновлении важно, что стало с его настройками.
if [ -f "$DEST/appsettings.json" ]; then
    MODE=обновление
else
    MODE=установка
fi
echo "=== SPI TWamp Server: $MODE"

echo "=== 1. Пользователь $USER_NAME"
if id "$USER_NAME" >/dev/null 2>&1; then
    echo "    уже есть"
else
    useradd --system --no-create-home --shell /sbin/nologin "$USER_NAME"
    echo "    создан"
fi

echo "=== 2. Файлы сервера → $DEST"
# Работающую службу надо остановить: иначе копирование поверх запущенного
# файла падает с «Text file busy», и обновление версии не проходит.
if systemctl is-active --quiet twamp-server 2>/dev/null; then
    echo "    останавливаем работающую службу"
    systemctl stop twamp-server
fi
mkdir -p "$DEST"
# Конфигурацию и базу не трогаем: на работающем сервере там боевые данные.
KEEP="appsettings.json TWamp.db TWamp-log.db spool"
for item in "$SRC"/*; do
    name=$(basename "$item")
    case " $KEEP " in
        *" $name "*)
            if [ -e "$DEST/$name" ]; then
                # appsettings.json ниже дополняется новыми ключами, остальное
                # (база, буфер) — боевые данные, их не трогаем вовсе.
                if [ "$name" = appsettings.json ]; then
                    MERGE_CONFIG=1  # дополним ниже, значения администратора сохранив
                else
                    echo "    $name уже есть — оставляем"
                fi
                continue
            fi
            ;;
    esac
    cp -r "$item" "$DEST/"
done
chmod +x "$DEST/SPI.Twamp.Server"

echo "=== 3. Настройки"
# Затирать чужой файл эталонным нельзя — там строка подключения, ключ API,
# адреса. Но и оставлять как есть мало: в новой версии появляются настройки,
# о которых иначе никто не узнает. Поэтому слияние: значения администратора
# остаются, новые ключи добавляются рядом. Разбирает JSON сам сервер — jq на
# минимальной системе может не быть, а исполняемый файл лежит рядом всегда.
# Код возврата 3 — файл с комментариями: сервер их сохранить не умеет и
# файл не трогает, а новые ключи перечисляет для ручного добавления.
if [ -n "${MERGE_CONFIG:-}" ]; then
    STATUS=0
    ADDED=$("$DEST/SPI.Twamp.Server" --merge-config "$DEST/appsettings.json" "$SRC/appsettings.json") || STATUS=$?
    if [ "$STATUS" = 3 ]; then
        echo "    в вашем appsettings.json есть комментарии — при переписывании они бы"
        echo "    пропали, поэтому файл не тронут. Добавьте новые настройки вручную:"
        echo "$ADDED" | sed 's/^/        /'
    elif [ "$STATUS" != 0 ]; then
        exit "$STATUS"
    elif [ -n "$ADDED" ]; then
        echo "    ваши значения сохранены, добавлены новые настройки:"
        echo "$ADDED" | sed 's/^/        /'
    else
        echo "    ваш appsettings.json уже полон — оставлен без изменений"
    fi
else
    echo "    appsettings.json взят из пакета"
fi

chown -R "$USER_NAME:$USER_NAME" "$DEST"

echo "=== 4. Служба systemd"
install -m 0644 "$SRC/twamp-server.service" /etc/systemd/system/twamp-server.service
systemctl daemon-reload
systemctl enable twamp-server >/dev/null
# Именно restart, а не «enable --now»: при обновлении версии служба уже
# работает, и «--now» оставил бы в памяти старый процесс со старыми файлами.
systemctl restart twamp-server
sleep 3

echo "=== 5. Порт"
PORT=$(grep -oE '"Urls"[^,]*' "$DEST/appsettings.json" | grep -oE '[0-9]+' | tail -1)
PORT=${PORT:-9000}
if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="${PORT}/tcp" >/dev/null
    firewall-cmd --reload >/dev/null
    echo "    firewalld: порт $PORT/tcp открыт"
else
    echo "    firewalld не запущен — порт $PORT открывать не потребовалось"
fi

echo "=== 6. Проверка"
if ! systemctl is-active --quiet twamp-server; then
    echo "    Служба не запустилась. Журнал:" >&2
    journalctl -u twamp-server -n 30 --no-pager >&2
    exit 1
fi
echo "    служба работает, PID $(systemctl show -p MainPID --value twamp-server)"

if command -v curl >/dev/null; then
    code=$(curl -s -o /dev/null -w '%{http_code}' -m 10 "http://127.0.0.1:${PORT}/" || true)
    echo "    ответ веб-интерфейса: HTTP $code"
fi

echo
echo "Готово ($MODE). Веб-интерфейс: http://$(hostname -I | awk '{print $1}'):${PORT}/"
echo "Журнал: journalctl -u twamp-server -f"
