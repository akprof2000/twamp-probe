// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Mvc;
using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using System.ComponentModel.DataAnnotations;

namespace SPI.Twamp.Server.Controllers
{
    /// <summary>
    /// Веб-интерфейс оператора: управление задачами, пробами и отчётами.
    /// Контроллер тонкий — вся логика вынесена в прикладные сервисы.
    /// </summary>
    /// <seealso cref="ControllerBase" />
    [Route("api/[controller]")]
    [ApiController]
    public class UserInterface(
        Logger logger, ITaskService taskService, IClientService clientService,
        IReportService reportService, IProvisioningService provisioningService,
        IProbeStatusProvider probeStatus, IProbeClient probeClient, IChangeNotifier changeNotifier)
        : ControllerBase
    {
        private readonly Logger _logger = logger;
        private readonly ITaskService _taskService = taskService;
        private readonly IClientService _clientService = clientService;
        private readonly IReportService _reportService = reportService;
        private readonly IProvisioningService _provisioningService = provisioningService;
        private readonly IProbeStatusProvider _probeStatus = probeStatus;
        private readonly IProbeClient _probeClient = probeClient;
        private readonly IChangeNotifier _changeNotifier = changeNotifier;

        /// <summary>Возвращает полный список задач.</summary>
        [HttpGet("tasks")]
        public async Task<ActionResult<IEnumerable<TaskInfo>>> Get()
        {
            _logger.Info("Запрос полного списка задач");
            return Ok(await _taskService.GetAllAsync());
        }

        /// <summary>Возвращает задачи указанной пробы.</summary>
        /// <param name="requestInfo">Адрес пробы (RequestInfo).</param>
        [HttpGet("tasks/{RequestInfo}")]
        public async Task<ActionResult<IEnumerable<TaskInfo>>> Get([Required] string requestInfo)
        {
            _logger.Info("Запрос задач пробы {RequestInfo}", requestInfo);
            return Ok(await _taskService.GetByRequestInfoAsync(requestInfo));
        }

        /// <summary>Создаёт или обновляет задачу.</summary>
        /// <param name="task">Описание задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("tasks")]
        public async Task<ActionResult> PostAsync([FromBody][Required] TaskInfo task, CancellationToken cancellationToken)
        {
            if (task.End <= DateTime.Now)
            {
                _logger.Warn("Задача {@Task} имеет некорректную дату окончания", task);
                return BadRequest("Дата окончания не может быть раньше текущего момента");
            }

            await _taskService.AddAsync(task, cancellationToken);
            return Ok();
        }

        /// <summary>Удаляет задачу по идентификатору.</summary>
        /// <param name="id">Идентификатор задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpDelete("tasks/{id}")]
        public async Task<ActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            await _taskService.DeleteAsync(id, cancellationToken);
            return Ok();
        }

        /// <summary>Восстанавливает удалённую задачу (снимает пометку удаления).</summary>
        /// <param name="id">Идентификатор задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("tasks/{id}/restore")]
        public async Task<ActionResult> RestoreAsync(Guid id, CancellationToken cancellationToken)
        {
            bool restored = await _taskService.RestoreAsync(id, cancellationToken);
            return restored ? Ok() : NotFound("Задача не найдена или не была удалена");
        }

        /// <summary>
        /// Проверяет соответствие задачи фильтрам списка (все текстовые фильтры —
        /// «содержит», без учёта регистра).
        /// </summary>
        private static bool MatchesFilter(TaskInfo t, TaskLastResult? last,
            string? title, string? probe, string? node, string? type,
            string status, string? outcome, string? error)
        {
            static bool Has(string source, string? term) =>
                string.IsNullOrEmpty(term) || source.Contains(term, StringComparison.OrdinalIgnoreCase);

            if (!Has(t.Title, title) || !Has(t.RequestInfo, probe) || !Has(t.EndNode, node))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(type) && !t.Type.ToString().Equals(type, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (status == "active" && t.Delete)
            {
                return false;
            }
            if (status == "deleted" && !t.Delete)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(outcome))
            {
                // «none» — задачи без данных о выполнении.
                string actual = last?.Outcome ?? (last is null ? "none" : (last.HasError ? "error" : "Success"));
                if (!actual.Equals(outcome, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            if (!string.IsNullOrEmpty(error) &&
                (last?.Error is null || !last.Error.Contains(error, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
            return true;
        }

        /// <summary>Возвращает отфильтрованные задачи вместе с их последними результатами.</summary>
        private async Task<List<(TaskInfo Task, TaskLastResult? Last)>> FilterTasksAsync(
            string? title, string? probe, string? node, string? type,
            string status, string? outcome, string? error)
        {
            IReadOnlyList<TaskInfo> all = await _taskService.GetAllAsync();
            IReadOnlyDictionary<Guid, TaskLastResult> lastResults = _probeStatus.GetLastResults();

            List<(TaskInfo, TaskLastResult?)> filtered = [];
            foreach (TaskInfo t in all)
            {
                TaskLastResult? last = lastResults.TryGetValue(t.Id, out TaskLastResult? lr) ? lr : null;
                if (MatchesFilter(t, last, title, probe, node, type, status, outcome, error))
                {
                    filtered.Add((t, last));
                }
            }
            return filtered;
        }

        /// <summary>
        /// Страница списка задач с фильтрами по всем столбцам и серверной пагинацией —
        /// интерфейс не загружает десятки тысяч задач целиком.
        /// </summary>
        /// <param name="skip">Сколько задач пропустить.</param>
        /// <param name="take">Размер страницы (максимум 500).</param>
        /// <param name="title">Фильтр по названию (содержит).</param>
        /// <param name="probe">Фильтр по адресу пробы (содержит).</param>
        /// <param name="node">Фильтр по узлу (содержит).</param>
        /// <param name="type">Фильтр по типу: Scheduler или Repeater.</param>
        /// <param name="status">Статус: active / deleted / all.</param>
        /// <param name="outcome">Исход: Success / ExitCodeError / TimedOut / StartFailed / none.</param>
        /// <param name="error">Фильтр по тексту ошибки (содержит).</param>
        [HttpGet("[action]")]
        public async Task<ActionResult> TasksPage(
            [FromQuery] int skip = 0, [FromQuery] int take = 100,
            [FromQuery] string? title = null, [FromQuery] string? probe = null,
            [FromQuery] string? node = null, [FromQuery] string? type = null,
            [FromQuery] string status = "active", [FromQuery] string? outcome = null,
            [FromQuery] string? error = null)
        {
            take = Math.Clamp(take, 1, 500);
            List<(TaskInfo Task, TaskLastResult? Last)> filtered =
                await FilterTasksAsync(title, probe, node, type, status, outcome, error);

            var items = filtered
                .OrderBy(x => x.Task.Title, StringComparer.OrdinalIgnoreCase)
                .Skip(Math.Max(0, skip)).Take(take)
                .Select(x => new
                {
                    x.Task.Id,
                    x.Task.Title,
                    x.Task.RequestInfo,
                    x.Task.EndNode,
                    Type = x.Task.Type.ToString(),
                    x.Task.CronExpression,
                    x.Task.End,
                    x.Task.TimeoutSec,
                    x.Task.Delete,
                    Last = x.Last is null ? null : new
                    {
                        x.Last.Time,
                        x.Last.HasError,
                        x.Last.Outcome,
                        x.Last.ExitCode,
                        x.Last.Error
                    }
                });

            // Счётчики для кнопок массовых операций: сколько в выборке реально можно
            // удалить (активных) и восстановить (удалённых). По ним интерфейс скрывает
            // ненужную кнопку, когда действовать не над чем.
            int activeTotal = filtered.Count(x => !x.Task.Delete);
            int deletedTotal = filtered.Count(x => x.Task.Delete);

            return Ok(new { Total = filtered.Count, ActiveTotal = activeTotal, DeletedTotal = deletedTotal, Items = items });
        }

        /// <summary>
        /// Массовая операция над отфильтрованным списком задач: удаление всех
        /// совпавших с фильтром (action=delete) или восстановление (action=restore).
        /// Фильтры те же, что у TasksPage, — «удалить отфильтрованное одним нажатием».
        /// </summary>
        /// <param name="action">delete — пометить удалёнными; restore — восстановить.</param>
        /// <param name="title">Фильтр по названию (содержит).</param>
        /// <param name="probe">Фильтр по адресу пробы (содержит).</param>
        /// <param name="node">Фильтр по узлу (содержит).</param>
        /// <param name="type">Фильтр по типу задач.</param>
        /// <param name="status">Статус: active / deleted / all.</param>
        /// <param name="outcome">Исход последнего запуска.</param>
        /// <param name="error">Фильтр по тексту ошибки.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Число изменённых задач.</returns>
        [HttpPost("[action]")]
        public async Task<ActionResult> TasksBulk(
            [FromQuery][Required] string action,
            [FromQuery] string? title = null, [FromQuery] string? probe = null,
            [FromQuery] string? node = null, [FromQuery] string? type = null,
            [FromQuery] string status = "active", [FromQuery] string? outcome = null,
            [FromQuery] string? error = null,
            CancellationToken cancellationToken = default)
        {
            bool delete = action.Equals("delete", StringComparison.OrdinalIgnoreCase);
            if (!delete && !action.Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("action должен быть delete или restore");
            }

            List<(TaskInfo Task, TaskLastResult? Last)> filtered =
                await FilterTasksAsync(title, probe, node, type, status, outcome, error);

            int affected = await _taskService.SetDeletedManyAsync(
                [.. filtered.Select(x => x.Task)], delete, cancellationToken);

            _logger.Info("Массовая операция {Action}: изменено {Count} задач", action, affected);
            return Ok(new { Affected = affected });
        }

        /// <summary>Удаляет все задачи пробы по её адресу.</summary>
        /// <param name="IPAddress">Адрес пробы (RequestInfo).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpDelete("tasks")]
        public async Task<ActionResult> DeleteByIPAsync([FromQuery][Required] string IPAddress, CancellationToken cancellationToken)
        {
            await _taskService.DeleteByRequestInfoAsync(IPAddress, cancellationToken);
            return Ok();
        }

        /// <summary>Возвращает список неопознанных проб (ожидающих подтверждения).</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult<IEnumerable<Identify>>> ListNotIdentifyClients()
        {
            _logger.Info("Запрос списка неопознанных проб");
            return Ok(await _clientService.GetUnidentifiedAsync());
        }

        /// <summary>Возвращает список подтверждённых проб.</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult<IEnumerable<Client>>> ListClients()
        {
            _logger.Info("Запрос списка проб");
            return Ok(await _clientService.GetClientsAsync());
        }

        /// <summary>Подтверждает пробу и запускает её опрос.</summary>
        /// <param name="client">Данные пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("[action]")]
        public async Task<ActionResult> SetInfoClient([FromBody][Required] Client client, CancellationToken cancellationToken)
        {
            await _clientService.SetInfoAsync(client, cancellationToken);
            return Ok();
        }

        /// <summary>Регистрирует пробу (CheckIn) по её адресу.</summary>
        /// <param name="client">Базовый адрес пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("[action]")]
        public async Task<ActionResult> CheckIn([FromQuery][Required] string client, CancellationToken cancellationToken)
        {
            await _clientService.CheckInAsync(client, cancellationToken);
            return Ok();
        }

        /// <summary>
        /// Выгружает результаты зондирования за период в CSV-файл потоково:
        /// данные пишутся в ответ постранично, большой период не загружается в память.
        /// </summary>
        /// <param name="from">Начало периода (по умолчанию — 14 дней назад).</param>
        /// <param name="to">Конец периода (по умолчанию — сейчас).</param>
        /// <param name="separator">Разделитель полей.</param>
        /// <param name="decimalSeparator">Десятичный разделитель.</param>
        /// <param name="cancellationToken">Токен отмены (разрыв соединения клиентом).</param>
        [HttpGet("[action]")]
        public async Task DownloadFile(
            [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
            [FromQuery] char separator = ';', [FromQuery] char decimalSeparator = ',',
            CancellationToken cancellationToken = default)
        {
            DateTime dateTo = to?.ToDateTime(TimeOnly.MaxValue) ?? DateTime.Now;
            DateTime dateFrom = from?.ToDateTime(TimeOnly.MinValue) ?? dateTo.AddDays(-14);

            Response.ContentType = "text/csv; charset=utf-8";
            Response.Headers.ContentDisposition =
                $"attachment; filename=data_{dateFrom:yyyyMMdd}_{dateTo:yyyyMMdd}.csv";

            await using StreamWriter writer = new(Response.Body, System.Text.Encoding.UTF8, leaveOpen: true);
            await _reportService.StreamCsvAsync(dateFrom, dateTo, separator, decimalSeparator, writer, cancellationToken);
        }

        /// <summary>
        /// «Длинный опрос» изменений для веб-интерфейса: висит до 25 секунд и отвечает
        /// сразу, как только на сервере изменились задачи, результаты или состояние проб.
        /// Клиент передаёт последнюю известную версию и обновляет экран при её росте —
        /// это события вместо периодического поллинга, ожидание не держит поток.
        /// </summary>
        /// <param name="version">Версия состояния, известная клиенту (0 — первая загрузка).</param>
        /// <param name="cancellationToken">Токен отмены (разрыв соединения клиентом).</param>
        /// <returns>Актуальная версия состояния.</returns>
        [HttpGet("[action]")]
        public async Task<ActionResult> WaitChanges([FromQuery] long version = 0, CancellationToken cancellationToken = default)
        {
            long current = await _changeNotifier.WaitAsync(version, TimeSpan.FromSeconds(25), cancellationToken);
            return Ok(new { Version = current });
        }

        /// <summary>
        /// Возвращает последние результаты по задачам (момент и признак ошибки) —
        /// колонка «последний результат» в списке задач. Заполняется по мере
        /// поступления результатов после старта сервера.
        /// </summary>
        [HttpGet("[action]")]
        public ActionResult LastResults()
        {
            IReadOnlyDictionary<Guid, TaskLastResult> results = _probeStatus.GetLastResults();
            return Ok(results.Select(kv => new
            {
                TaskId = kv.Key,
                kv.Value.Time,
                kv.Value.HasError,
                kv.Value.Outcome,
                kv.Value.ExitCode,
                kv.Value.Error
            }));
        }

        /// <summary>
        /// Проксирует состояние выполнения задач с пробы: запущена ли задача сейчас,
        /// последний старт/завершение, счётчик выполнений, ближайший запуск, ошибка.
        /// Отвечает на вопрос «запустились ли задачи», не дожидаясь первых результатов.
        /// </summary>
        /// <param name="probe">Адрес пробы (RequestInfo).</param>
        /// <param name="skip">Сколько записей пропустить.</param>
        /// <param name="take">Размер страницы (максимум 500).</param>
        /// <param name="title">Фильтр по названию задачи (содержит).</param>
        /// <param name="outcome">Фильтр по исходу запуска.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpGet("[action]")]
        public async Task<ActionResult> ProbeTaskStatus(
            [FromQuery][Required] string probe,
            [FromQuery] int skip = 0, [FromQuery] int take = 100,
            [FromQuery] string? title = null, [FromQuery] string? outcome = null,
            CancellationToken cancellationToken = default)
        {
            // Пагинация и фильтры выполняются на пробе — сервер лишь проксирует ответ.
            string query = $"skip={skip}&take={take}" +
                (string.IsNullOrEmpty(title) ? "" : $"&title={Uri.EscapeDataString(title)}") +
                (string.IsNullOrEmpty(outcome) ? "" : $"&outcome={Uri.EscapeDataString(outcome)}");
            string json = await _probeClient.GetTaskStatusRawAsync(probe, query, cancellationToken);
            return Content(json, "application/json");
        }

        /// <summary>
        /// Возвращает состояние всех проб: связь (последний успешный опрос, ошибки,
        /// backoff), версию пробы и число её задач — для страницы мониторинга.
        /// </summary>
        [HttpGet("[action]")]
        public async Task<ActionResult> ProbeStatus()
        {
            IReadOnlyList<Client> clients = await _clientService.GetClientsAsync();
            IReadOnlyDictionary<string, ProbePollState> states = _probeStatus.GetStates();
            IReadOnlyList<TaskInfo> allTasks = await _taskService.GetAllAsync();

            var status = clients.Select(c =>
            {
                ProbePollState? state = states.TryGetValue(c.RequestInfo, out ProbePollState? s) ? s : null;
                return new
                {
                    c.RequestInfo,
                    c.Name,
                    c.HostName,
                    c.IPAddress,
                    c.Version,
                    LastSuccess = state?.LastSuccess,
                    LastError = state?.LastError,
                    LastErrorMessage = state?.LastErrorMessage,
                    BackoffSeconds = state?.BackoffSeconds ?? 0,
                    TotalResults = state?.TotalResults ?? 0,
                    ActiveTasks = allTasks.Count(t => t.RequestInfo == c.RequestInfo && !t.Delete),
                    DeletedTasks = allTasks.Count(t => t.RequestInfo == c.RequestInfo && t.Delete)
                };
            });

            return Ok(status);
        }

        /// <summary>Загружает задачи из CSV-файла.</summary>
        /// <param name="file">CSV-файл.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <param name="separator">Разделитель полей.</param>
        /// <param name="formatDateTime">Формат дат в файле.</param>
        /// <returns>Названия задач с некорректной датой окончания.</returns>
        [HttpPost("UploadCsv")]
        [RequestFormLimits(MultipartBodyLengthLimit = 209715200)]
        [RequestSizeLimit(209715200)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadCsv(
            IFormFile? file, CancellationToken cancellationToken,
            [FromQuery] string separator = ";", [FromQuery] string formatDateTime = "dd.MM.yyyy HH:mm")
        {
#if !DEBUG
            if (file is null || file.Length == 0)
            {
                _logger.Warn("UploadCsv: пустой файл");
                return BadRequest("Файл пуст");
            }
            string fileName = file.FileName;
#else
            // В отладке читаем локальный тестовый файл, если файл не передан.
            string fileName = file?.FileName ?? "Base test.csv";
#endif

            if (!string.Equals(".csv", Path.GetExtension(fileName), StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("Файл не является CSV");
            }

            using MemoryStream memoryStream = new();
#if !DEBUG
            _logger.Info("UploadCsv: файл {FileName}, размер {Size}", file.FileName, file.Length);
            await file.CopyToAsync(memoryStream, cancellationToken);
#else
            if (file is not null)
            {
                await file.CopyToAsync(memoryStream, cancellationToken);
            }
            else
            {
                using FileStream stream = new(fileName, FileMode.Open);
                await stream.CopyToAsync(memoryStream, cancellationToken);
            }
#endif
            memoryStream.Seek(0, SeekOrigin.Begin);

            IReadOnlyList<string> rejected =
                await _reportService.ImportCsvAsync(memoryStream, fileName, separator, formatDateTime, cancellationToken);

            return Ok(rejected);
        }

        /// <summary>
        /// Загружает файл шаблонов задач (CSV с заголовками, разделитель «;») в набор.
        /// Поддерживаемые колонки: Name, Probe, Request, Type, Repeats, Circles, Pause,
        /// Cron, Start, End, Mode, TimeOut — порядок любой, большинство необязательны.
        /// Повторная загрузка набора с тем же именем обновляет его; другие наборы не трогаются.
        /// </summary>
        /// <param name="file">CSV-файл шаблонов.</param>
        /// <param name="name">Имя набора; по умолчанию — имя файла без расширения.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Имя набора и число загруженных шаблонов.</returns>
        [HttpPost("[action]")]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult> UploadTemplates(IFormFile file, [FromQuery] string? name, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest("Файл пуст");
            }

            // Имя набора по умолчанию — имя загруженного файла без расширения.
            string setName = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(file.FileName)
                : name.Trim();

            _logger.Info("Загрузка шаблонов из {FileName} в набор «{Set}», размер {Size}", file.FileName, setName, file.Length);
            using Stream stream = file.OpenReadStream();
            int count = await _provisioningService.UploadTemplatesAsync(stream, setName, cancellationToken);
            return Ok(new { Set = setName, Templates = count });
        }

        /// <summary>Возвращает все шаблоны всех наборов.</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult<IEnumerable<ProbeTemplate>>> Templates()
        {
            return Ok(await _provisioningService.GetTemplatesAsync());
        }

        /// <summary>Возвращает список наборов шаблонов (имя и число шаблонов).</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult> TemplateSets()
        {
            IReadOnlyList<(string SetName, int Count)> sets = await _provisioningService.GetTemplateSetsAsync();
            return Ok(sets.Select(s => new { Set = s.SetName, Templates = s.Count }));
        }

        /// <summary>Удаляет набор шаблонов целиком.</summary>
        /// <param name="set">Имя набора.</param>
        [HttpDelete("templates")]
        public async Task<ActionResult> DeleteTemplates([FromQuery][Required] string set)
        {
            int removed = await _provisioningService.DeleteTemplateSetAsync(set);
            return removed > 0 ? Ok(new { Set = set, Removed = removed }) : NotFound($"Набор «{set}» не найден");
        }

        /// <summary>
        /// Загружает файл со списком маршрутизаторов, накладывает на него набор шаблонов
        /// и сразу создаёт задачи (маршрутизаторы × шаблоны). Имя задачи —
        /// «устройство-IP-шаблон», время создания — момент вызова.
        /// </summary>
        /// <param name="file">CSV-файл маршрутизаторов.</param>
        /// <param name="set">Имя применяемого набора шаблонов; пусто — все наборы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Отчёт: маршрутизаторов, шаблонов, создано задач, ошибки разбора.</returns>
        [HttpPost("[action]")]
        [RequestFormLimits(MultipartBodyLengthLimit = 209715200)]
        [RequestSizeLimit(209715200)]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult> UploadRouters(IFormFile file, [FromQuery] string? set, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest("Файл пуст");
            }

            _logger.Info("Загрузка маршрутизаторов из {FileName} (набор «{Set}»), размер {Size}", file.FileName, set ?? "все", file.Length);
            using Stream stream = file.OpenReadStream();
            ProvisioningResult result = await _provisioningService.GenerateAsync(stream, set, cancellationToken);

            // Пакетная заливка: одна запись в БД и один SetJobs на пачку для каждой пробы.
            await _taskService.AddRangeAsync(result.Tasks, cancellationToken);

            return Ok(new
            {
                Routers = result.Routers,
                Templates = result.Templates,
                Created = result.Tasks.Count,
                Rejected = result.Rejected
            });
        }

        /// <summary>
        /// Загружает файл со списком маршрутизаторов и возвращает сгенерированный CSV
        /// формата «Base test.csv» БЕЗ создания задач — для предварительной проверки.
        /// Полученный файл можно загрузить существующим методом UploadCsv.
        /// </summary>
        /// <param name="file">CSV-файл маршрутизаторов.</param>
        /// <param name="set">Имя применяемого набора шаблонов; пусто — все наборы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>CSV-файл с задачами.</returns>
        [HttpPost("[action]")]
        [RequestFormLimits(MultipartBodyLengthLimit = 209715200)]
        [RequestSizeLimit(209715200)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> PreviewRouters(IFormFile file, [FromQuery] string? set, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest("Файл пуст");
            }

            _logger.Info("Предпросмотр задач из {FileName} (набор «{Set}»), размер {Size}", file.FileName, set ?? "все", file.Length);
            using Stream stream = file.OpenReadStream();
            ProvisioningResult result = await _provisioningService.GenerateAsync(stream, set, cancellationToken);

            byte[] csv = _provisioningService.BuildCsv(result.Tasks);
            string name = $"tasks_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            return File(csv, "text/csv", name);
        }
    }
}
