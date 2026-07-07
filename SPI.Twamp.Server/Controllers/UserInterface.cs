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
        Logger logger, ITaskService taskService, IClientService clientService, IReportService reportService)
        : ControllerBase
    {
        private readonly Logger _logger = logger;
        private readonly ITaskService _taskService = taskService;
        private readonly IClientService _clientService = clientService;
        private readonly IReportService _reportService = reportService;

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
    }
}
