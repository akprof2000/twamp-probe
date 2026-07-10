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
        IProbeStatusProvider probeStatus, IProbeClient probeClient)
        : ControllerBase
    {
        private readonly Logger _logger = logger;
        private readonly ITaskService _taskService = taskService;
        private readonly IClientService _clientService = clientService;
        private readonly IReportService _reportService = reportService;
        private readonly IProvisioningService _provisioningService = provisioningService;
        private readonly IProbeStatusProvider _probeStatus = probeStatus;
        private readonly IProbeClient _probeClient = probeClient;

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
                kv.Value.HasError
            }));
        }

        /// <summary>
        /// Проксирует состояние выполнения задач с пробы: запущена ли задача сейчас,
        /// последний старт/завершение, счётчик выполнений, ближайший запуск, ошибка.
        /// Отвечает на вопрос «запустились ли задачи», не дожидаясь первых результатов.
        /// </summary>
        /// <param name="probe">Адрес пробы (RequestInfo).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpGet("[action]")]
        public async Task<ActionResult> ProbeTaskStatus([FromQuery][Required] string probe, CancellationToken cancellationToken)
        {
            string json = await _probeClient.GetTaskStatusRawAsync(probe, cancellationToken);
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
