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
        IReportService reportService, IProvisioningService provisioningService)
        : ControllerBase
    {
        private readonly Logger _logger = logger;
        private readonly ITaskService _taskService = taskService;
        private readonly IClientService _clientService = clientService;
        private readonly IReportService _reportService = reportService;
        private readonly IProvisioningService _provisioningService = provisioningService;

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

        /// <summary>Выгружает результаты зондирования за период в CSV-файл.</summary>
        /// <param name="from">Начало периода (по умолчанию — 14 дней назад).</param>
        /// <param name="to">Конец периода (по умолчанию — сейчас).</param>
        /// <param name="separator">Разделитель полей.</param>
        /// <param name="decimalSeparator">Десятичный разделитель.</param>
        [HttpGet("[action]")]
        public async Task<IActionResult> DownloadFile(
            [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
            [FromQuery] char separator = ';', [FromQuery] char decimalSeparator = ',')
        {
            DateTime dateTo = to?.ToDateTime(TimeOnly.MaxValue) ?? DateTime.Now;
            DateTime dateFrom = from?.ToDateTime(TimeOnly.MinValue) ?? dateTo.AddDays(-14);

            (byte[] content, string fileName) = await _reportService.BuildCsvAsync(dateFrom, dateTo, separator, decimalSeparator);
            return File(content, "text/csv", fileName);
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
        /// Загружает файл шаблонов задач (CSV с заголовками, разделитель «;»).
        /// Поддерживаемые колонки: Name, Probe, Request, Type, Repeats, Circles, Pause,
        /// Cron, Start, End, Mode, TimeOut — порядок любой, большинство необязательны.
        /// Загрузка замещает предыдущий набор шаблонов.
        /// </summary>
        /// <param name="file">CSV-файл шаблонов.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Число загруженных шаблонов.</returns>
        [HttpPost("[action]")]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult> UploadTemplates(IFormFile file, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest("Файл пуст");
            }

            _logger.Info("Загрузка шаблонов из {FileName}, размер {Size}", file.FileName, file.Length);
            using Stream stream = file.OpenReadStream();
            int count = await _provisioningService.UploadTemplatesAsync(stream, cancellationToken);
            return Ok(new { Templates = count });
        }

        /// <summary>Возвращает текущий набор шаблонов.</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult<IEnumerable<ProbeTemplate>>> Templates()
        {
            return Ok(await _provisioningService.GetTemplatesAsync());
        }

        /// <summary>
        /// Загружает файл со списком маршрутизаторов, накладывает на него сохранённые
        /// шаблоны и сразу создаёт задачи (маршрутизаторы × шаблоны). Имя задачи —
        /// «устройство-шаблон», время создания — момент вызова.
        /// </summary>
        /// <param name="file">Файл маршрутизаторов (строки «ИМЯ|IP:адрес …»).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Отчёт: маршрутизаторов, шаблонов, создано задач, ошибки разбора.</returns>
        [HttpPost("[action]")]
        [RequestFormLimits(MultipartBodyLengthLimit = 209715200)]
        [RequestSizeLimit(209715200)]
        [Consumes("multipart/form-data")]
        public async Task<ActionResult> UploadRouters(IFormFile file, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest("Файл пуст");
            }

            _logger.Info("Загрузка маршрутизаторов из {FileName}, размер {Size}", file.FileName, file.Length);
            using Stream stream = file.OpenReadStream();
            ProvisioningResult result = await _provisioningService.GenerateAsync(stream, cancellationToken);

            foreach (TaskInfo task in result.Tasks)
            {
                await _taskService.AddAsync(task, cancellationToken);
            }

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
        /// <param name="file">Файл маршрутизаторов (строки «ИМЯ|IP:адрес …»).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>CSV-файл с задачами.</returns>
        [HttpPost("[action]")]
        [RequestFormLimits(MultipartBodyLengthLimit = 209715200)]
        [RequestSizeLimit(209715200)]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> PreviewRouters(IFormFile file, CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                return BadRequest("Файл пуст");
            }

            _logger.Info("Предпросмотр задач из {FileName}, размер {Size}", file.FileName, file.Length);
            using Stream stream = file.OpenReadStream();
            ProvisioningResult result = await _provisioningService.GenerateAsync(stream, cancellationToken);

            byte[] csv = _provisioningService.BuildCsv(result.Tasks);
            string name = $"tasks_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            return File(csv, "text/csv", name);
        }
    }
}
