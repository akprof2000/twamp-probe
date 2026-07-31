SPI TWamp Probe (Go) — экспериментальная проба на Go
====================================================

Что это
-------
Порт пробы SPI.TWamp.Probe на Go: один статический бинарник ~6 МБ без
зависимостей (не нужен ни .NET, ни какие-либо библиотеки — работает на любом
Linux x86-64, включая CentOS 7/8/9, Rocky, Alma, Ubuntu, Debian).

Полностью совместим с сервером SPI.Twamp.Server: те же эндпоинты
api/probeinterface, тот же формат JSON, та же механика доставки результатов
с подтверждением (ACK), инкрементальное слияние задач и cron-расписания.
Сервер видит её как обычную пробу (версия «go-0.1.0» в списке проб).

Состав папки
------------
  twamp-probe        — исполняемый файл (Linux x86-64, статический)
  twping             — зонд TWAMP из пакета perfSONAR (el7, работает с CentOS 7)
  appsettings.json   — конфигурация (тот же формат, что у C#-пробы)
  twampy/            — вендоренный nokia/twampy для режима TWampy
  README-DEPLOY.txt  — этот файл

Установка на CentOS / любой Linux
---------------------------------
1. Скопируйте папку целиком на машину, например в /opt/twamp-probe-go:
     scp -r twamp-probe-go root@host:/opt/

2. Дайте права на выполнение:
     chmod +x /opt/twamp-probe-go/twamp-probe

3. Запустите (порт по умолчанию 8443 — root не нужен):
     cd /opt/twamp-probe-go && ./twamp-probe
   Порт меняется в appsettings.json ("Urls").

4. На сервере: «Статус проб» → «Опросить пробу» (http://адрес:8443) →
   «Подтвердить». Дальше всё как с обычной пробой.

Режимы зондирования
-------------------
  WinPing — системный ping (аргументы по умолчанию "-c 2" — Linux-синтаксис);
  TWamp   — утилита twping: уже лежит в папке (linux-пакет), ставить ничего
            не нужно. Это бинарник из релизного пакета perfSONAR для el7,
            поэтому работает начиная с CentOS 7. Свою версию можно подставить
            через "twamp:name" в appsettings.json;
  TWampy  — нужен только Python 3.8+ в PATH; пакет twampy уже в папке,
            PYTHONPATH проба выставляет сама.

systemd (автозапуск)
--------------------
/etc/systemd/system/twamp-probe.service:

  [Unit]
  Description=SPI TWamp Probe (Go)
  After=network-online.target

  [Service]
  WorkingDirectory=/opt/twamp-probe-go
  ExecStart=/opt/twamp-probe-go/twamp-probe
  Restart=always
  # Важно при тысячах задач: один идущий замер занимает и процесс зонда,
  # и поток пробы, ждущий его завершения. Оба считаются в LimitNPROC.
  # Штатные 4096 в CentOS/RHEL упираются в потолок на 2048 замерах —
  # проба сама снизит предел, но так она работает вполсилы.
  LimitNPROC=65535
  LimitNOFILE=1048576

  [Install]
  WantedBy=multi-user.target

  systemctl daemon-reload && systemctl enable --now twamp-probe

Файлы, создаваемые при работе
-----------------------------
  TaskInfo.json   — реестр задач по расписанию (переживает перезапуск)
  JobResult.json  — недоставленные результаты (переживают перезапуск)

Отличия от C#-пробы (экспериментальный статус)
----------------------------------------------
  - логи пишутся в stdout/stderr (journald при запуске через systemd),
    файлового NLog-лога нет;
  - нет Swagger и эндпоинта TaskStatus в веб-интерфейсе самой пробы
    (сервер проксирует TaskStatus — это работает);
  - статистика выполнения (TaskStatus) обнуляется при перезапуске — как и у C#.
