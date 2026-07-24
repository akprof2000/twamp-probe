// Ignore Spelling: SPI Twamp

using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.TypeConversion;
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
    public sealed class ReportService(Logger logger, IResultSpool spool, ITaskService taskService)
        : IReportService
    {
        /// <summary>Как часто сбрасывать накопленное в поток ответа, строк.</summary>
        private const int FlushEvery = 5000;

        private readonly Logger _logger = logger;
        private readonly IResultSpool _spool = spool;
        private readonly ITaskService _taskService = taskService;

        /// <inheritdoc/>
        public async Task StreamCsvAsync(
            char separator, char decimalSeparator, TextWriter writer, CancellationToken cancellationToken)
        {
            _logger.Info("Потоковая выгрузка CSV из буфера: сегментов в очереди {Queued}, строк в текущем {Rows}",
                _spool.SealedCount, _spool.CurrentRows);

            await writer.WriteLineAsync(ExportRow.CsvHeader(separator));

            int written = 0;
            await foreach (ExportRow row in _spool.ReadPendingAsync(cancellationToken))
            {
                await writer.WriteLineAsync(row.ToCsvLine(separator, decimalSeparator));

                if (++written % FlushEvery == 0)
                {
                    await writer.FlushAsync(cancellationToken);
                }
            }

            await writer.FlushAsync(cancellationToken);
            _logger.Info("Выгружено строк: {Count}", written);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> ImportCsvAsync(
            Stream csv, string sourceName, string delimiter, string dateTimeFormat, CancellationToken cancellationToken)
        {
            CsvRow[] rows = await ReadRowsAsync(csv, delimiter, dateTimeFormat, cancellationToken);
            _logger.Info("Импорт CSV {File}: строк {Count}", sourceName, rows.Length);

            DateTime now = DateTime.Now;
            List<string> rejected = [];
            List<TaskInfo> parsedTasks = [];

            foreach (CsvRow row in rows)
            {
                TaskInfo task = MapToTask(row, sourceName, now);

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
            Stream csv, string delimiter, string dateTimeFormat, CancellationToken cancellationToken)
        {
            using StreamReader reader = new(csv, Encoding.UTF8);

            CsvConfiguration config = new(CultureInfo.InvariantCulture)
            {
                Delimiter = delimiter,
                PrepareHeaderForMatch = args => args.Header.ToLower()
            };

            using CsvReader csvReader = new(reader, config);
            csvReader.Context.TypeConverterOptionsCache.AddOptions<DateTime>(
                new TypeConverterOptions { Formats = [dateTimeFormat] });

            return await csvReader.GetRecordsAsync<CsvRow>(cancellationToken).ToArrayAsync(cancellationToken);
        }

        /// <summary>Преобразует строку CSV в задачу.</summary>
        private static TaskInfo MapToTask(CsvRow row, string sourceName, DateTime createdAt) => new()
        {
            Circles = row.Circles,
            ContinueIfError = true,
            Create = createdAt,
            CronExpression = row.Cron,
            CronWithSeconds = false,
            Delete = false,
            // Kind=Local: при передаче пробе в другом часовом поясе JSON будет
            // содержать смещение, и время корректно пересчитается на её стороне.
            Start = DateTime.SpecifyKind(row.Start, DateTimeKind.Local),
            End = DateTime.SpecifyKind(row.End, DateTimeKind.Local),
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
