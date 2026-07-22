[← К обзору проекта (README)](../README.md) · [Вся документация](README.md)

---

## HTTP API

Все методы — под префиксом `api/userinterface` (сервер) и `api/probeinterface` (проба). При включённом ключе передавайте заголовок `X-Api-Key`.

### Сервер — управление задачами

| Метод | Путь | Назначение |
|---|---|---|
| GET | `/api/userinterface/tasks` | все задачи |
| GET | `/api/userinterface/tasks/{requestInfo}` | задачи одной пробы |
| POST | `/api/userinterface/tasks` | создать/обновить задачу (JSON `TaskInfo`) |
| DELETE | `/api/userinterface/tasks/{id}` | удалить задачу |
| DELETE | `/api/userinterface/tasks?IPAddress=…` | удалить все задачи пробы |

### Сервер — пробы и мониторинг

| Метод | Путь | Назначение |
|---|---|---|
| POST | `/api/userinterface/checkin?client=http://проба:8443` | опросить пробу (регистрация) |
| GET | `/api/userinterface/listnotidentifyclients` | пробы, ожидающие подтверждения |
| POST | `/api/userinterface/setinfoclient` | подтвердить пробу (запускает опрос) |
| GET | `/api/userinterface/listclients` | подтверждённые пробы |
| DELETE | `/api/userinterface/clients?requestInfo=…&deleteTasks=…` | удалить пробу: остановить опрос, убрать из списка; `deleteTasks=true` — удалить и её задачи |
| DELETE | `/api/userinterface/unidentified?requestInfo=…` | отклонить неопознанную пробу (пустой адрес — вычистить битые записи) |
| GET | `/api/userinterface/probestatus` | статус связи, версия, число задач по каждой пробе |
| GET | `/api/userinterface/probetaskstatus?probe=…` | живой статус выполнения задач на пробе (запущена ли, следующий запуск, ошибка) |
| GET | `/api/userinterface/lastresults` | последние результаты по задачам (момент + признак ошибки) |
| GET | `/api/userinterface/waitchanges?version=N` | длинный опрос изменений (до 25 с): ответ сразу при изменении задач/результатов/проб |
| GET | `/api/userinterface/taskspage?skip&take&title&probe&node&type&status&outcome&error` | страница задач с фильтрами по всем столбцам |
| POST | `/api/userinterface/tasksbulk?action=delete\|restore&…фильтры` | массовое удаление/восстановление всего отфильтрованного |
| POST | `/api/userinterface/tasks/{id}/restore` | восстановить удалённую задачу |

### Сервер — массовая заливка и отчёты

| Метод | Путь | Назначение |
|---|---|---|
| POST | `/api/userinterface/uploadtemplates?name=…` | загрузить набор шаблонов (multipart, поле `file`; имя по умолчанию — имя файла) |
| GET | `/api/userinterface/templatesets` | список наборов (имя + число шаблонов) |
| GET | `/api/userinterface/templates` | все шаблоны всех наборов |
| DELETE | `/api/userinterface/templates?set=…` | удалить набор шаблонов |
| POST | `/api/userinterface/uploadrouters?set=…` | файл маршрутизаторов → **создать** задачи (набор × строки; без `set` — все наборы) |
| POST | `/api/userinterface/previewrouters?set=…` | то же, но вернуть CSV **без создания** |
| POST | `/api/userinterface/UploadCsv` | загрузить готовый CSV задач |
| GET | `/api/userinterface/downloadfile?from=…&to=…` | потоковая выгрузка отчёта CSV |

### Проба (используется сервером)

| Метод | Путь | Назначение |
|---|---|---|
| POST | `/api/probeinterface/checkin?requestInfo=…` | идентификация пробы (адреса, версия) |
| POST | `/api/probeinterface/setjobs` | принять **изменившиеся** задачи (инкрементальное слияние) |
| GET | `/api/probeinterface/taskids` | идентификаторы задач, известных пробе (для сверки) |
| GET | `/api/probeinterface/tasks` | полные определения задач по расписанию (сервер забирает для восстановления БД после потери данных) |
| GET | `/api/probeinterface/taskstatus` | состояние выполнения задач (running, старт/финиш, следующий запуск, ошибка) |
| GET | `/api/probeinterface/checkdata` | длинный опрос: пачка результатов с `batchId` |
| POST | `/api/probeinterface/confirmdata?batchId=…` | подтвердить запись пачки (проба удаляет её) |

---

---

[← К обзору проекта (README)](../README.md) · [Вся документация](README.md)

---
