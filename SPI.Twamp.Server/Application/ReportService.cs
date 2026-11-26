// Ignore Spelling: SPI Twamp

using CsvHelper;
using CsvRow = SPI.Twamp.Server.Contracts.CsvRow;
using Microsoft.Extensions.Caching.Memory;
using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Parser;
using System.Globalization;
using System.Text;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Реализация <see cref="IReportService"/>: потоковая выгрузка результатов в CSV
    /// и импорт задач из CSV.
    /// </summary>
    public sealed class ReportService(Logger logger, SPI.Twamp.Server.Infrastructure.CollectorRegistry collectors, ITaskService taskService)
        : IReportService
    {
        private readonly Logger _logger = logger;
        private readonly ITaskService _taskService = taskService;

        /// <inheritdoc/>
        public async Task StreamCsvAsync(
            char separator, char decimalSeparator, TextWriter writer, CancellationToken cancellationToken, string? collectorId = null)
        {
            using HttpClient client = await collectors.ConnectAsync(collectorId);
            string url = $"report?separator={Uri.EscapeDataString(separator.ToString())}&decimalSeparator={Uri.EscapeDataString(decimalSeparator.ToString())}";
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
            char[] buffer = new char[16384];
            int count;
            while ((count = await reader.ReadAsync(buffer, cancellationToken)) != 0)
                await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> ImportCsvAsync(
            Stream csv, string sourceName, string delimiter, string dateTimeFormat, CancellationToken cancellationToken)
        {
            CsvRow[] rows = await ReadRowsAsync(csv, delimiter, cancellationToken);
            _logger.Info("Импорт CSV {File}: строк {Count}", sourceName, rows.Length);

            DateTime now = DateTime.Now;
            List<string> rejected = [];
            List<TaskInfo> parsedTasks = [];

            foreach (CsvRow row in rows)
            {
                TaskInfo task = MapToTask(row, sourceName, now, dateTimeFormat);

                // Задачи с датой окончания в прошлом не имеют смысла — сообщаем о них,
                // но (сохраняя исходное поведение) всё равно передаём в обработку.
                if (task.End <= now)
                {
                    _logger.Warn("Некорректная дата окончания у задачи {@Task}", task);
                    rejected.Add(task.Title);
                }

                parsedTasks.Add(task);
            }

            // Пакетная заливка: одна запись в БД и один SetJobs на пачку для каждой пробы.
            await _taskService.AddRangeAsync(parsedTasks, cancellationToken);

            return rejected;
        }

        /// <summary>Читает и разбирает строки CSV в модели <see cref="CsvRow"/>.</summary>
        private static async Task<CsvRow[]> ReadRowsAsync(
            Stream csv, string delimiter, CancellationToken cancellationToken)
        {
            using var input = new CsvSeparatorStream(csv, delimiter, leaveOpen: true);
            var options = new CsvReaderOptions
            {
                Separator = input.Replacement,
                FormatProvider = CultureInfo.InvariantCulture,
                NormalizeHeaderName = header => header.ToLowerInvariant()
            };
            using var csvReader = CsvReader.Create(input, options);
            return await csvReader.GetRecordsAsync<CsvRow>(cancellationToken).ToArrayAsync(cancellationToken);
        }

        /// <summary>Преобразует строку CSV в задачу.</summary>
        private static TaskInfo MapToTask(CsvRow row, string sourceName, DateTime createdAt, string dateTimeFormat) => new()
        {
            Circles = row.Circles,
            ContinueIfError = true,
            Create = createdAt,
            CronExpression = row.Cron,
            CronWithSeconds = false,
            Delete = false,
            // Kind=Local: при передаче пробе в другом часовом поясе JSON будет
            // содержать смещение, и время корректно пересчитается на её стороне.
            Start = DateTime.SpecifyKind(DateTime.ParseExact(row.Start, dateTimeFormat, CultureInfo.InvariantCulture), DateTimeKind.Local),
            End = DateTime.SpecifyKind(DateTime.ParseExact(row.End, dateTimeFormat, CultureInfo.InvariantCulture), DateTimeKind.Local),
            EndNode = row.Ip,
            Mode = row.Mode,
            Parameters = row.Request is null
                ? []
                : new Dictionary<string, string> { { "all", row.Request } },
            PauseSec = row.Pause,
            TimeoutSec = row.Timeout, // индивидуальный таймаут задачи (0 — без ограничения)
            Repeats = row.Repeats,
            RequestInfo = row.Probe,
            Type = row.Type,
            Title = $"Task from {sourceName} by {row.Name} created {createdAt:dd.MM.yyyy HH:mm}"
        };

    }
}
