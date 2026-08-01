#!/bin/bash
# Установка SPI TWamp Server на CentOS/RHEL как службы systemd.
#
# Что делает:
#   1. заводит системного пользователя twamp (сервер не требует root);
#   2. копирует файлы в /opt/twamp-server;
#   3. ставит службу systemd и включает автозапуск;
#   4. при необходимости открывает порт в firewalld;
#   5. проверяет, что сервер отвечает.
#
# Запуск от root:  ./install-centos.sh
# Повторный запуск безопасен: appsettings.json и база данных не затираются.

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
                echo "    $name уже есть — оставляем"
                continue
            fi
            ;;
    esac
    cp -r "$item" "$DEST/"
done
chmod +x "$DEST/SPI.Twamp.Server"
chown -R "$USER_NAME:$USER_NAME" "$DEST"

echo "=== 3. Служба systemd"
install -m 0644 "$SRC/twamp-server.service" /etc/systemd/system/twamp-server.service
systemctl daemon-reload
systemctl enable twamp-server >/dev/null
# Именно restart, а не «enable --now»: при обновлении версии служба уже
# работает, и «--now» оставил бы в памяти старый процесс со старыми файлами.
systemctl restart twamp-server
sleep 3

echo "=== 4. Порт"
PORT=$(grep -oE '"Urls"[^,]*' "$DEST/appsettings.json" | grep -oE '[0-9]+' | tail -1)
PORT=${PORT:-9000}
if command -v firewall-cmd >/dev/null && firewall-cmd --state >/dev/null 2>&1; then
    firewall-cmd --permanent --add-port="${PORT}/tcp" >/dev/null
    firewall-cmd --reload >/dev/null
    echo "    firewalld: порт $PORT/tcp открыт"
else
    echo "    firewalld не запущен — порт $PORT открывать не потребовалось"
fi

echo "=== 5. Проверка"
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
echo "Готово. Веб-интерфейс: http://$(hostname -I | awk '{print $1}'):${PORT}/"
echo "Журнал: journalctl -u twamp-server -f"
