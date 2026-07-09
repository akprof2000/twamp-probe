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
    public sealed class ReportService(
        Logger logger, IActionRepository actions, IStatRepository stats,
        ITaskRepository tasks, ITaskService taskService, IMemoryCache cache)
        : IReportService
    {
        /// <summary>Срок хранения названия задачи в кэше.</summary>
        private static readonly TimeSpan TitleCacheDuration = TimeSpan.FromMinutes(60);

        /// <summary>Размер страницы при потоковом чтении статистики из БД.</summary>
        private const int ExportPageSize = 5000;

        private readonly Logger _logger = logger;
        private readonly IActionRepository _actions = actions;
        private readonly IStatRepository _stats = stats;
        private readonly ITaskRepository _tasks = tasks;
        private readonly ITaskService _taskService = taskService;
        private readonly IMemoryCache _cache = cache;

        /// <inheritdoc/>
        public async Task StreamCsvAsync(
            DateTime from, DateTime to, char separator, char decimalSeparator,
            TextWriter writer, CancellationToken cancellationToken)
        {
            _logger.Info("Потоковая выгрузка CSV за период {From} — {To}", from, to);
            await writer.WriteLineAsync(TwPingParser.CsvHeader(separator));

            int total = await _stats.CountByPeriodAsync(from, to);
            if (total > 0)
            {
                // Основной путь: статистика уже разобрана при приёме — читаем постранично.
                for (int skip = 0; skip < total; skip += ExportPageSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<StatRecord> page = await _stats.GetPageAsync(from, to, skip, ExportPageSize);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    foreach (StatRecord record in page)
                    {
                        record.Stats.CallLine = record.CallLine;
                        record.Stats.Title = await GetTitleAsync(record.TaskId);
                        await writer.WriteLineAsync(TwPingParser.ToCsvLine(record.Stats, separator, decimalSeparator));
                    }
                    await writer.FlushAsync(cancellationToken);
                }
                return;
            }

            // Совместимость: данные, накопленные до внедрения разбора при приёме,
            // разбираем по-старому из сырых ответов.
            IReadOnlyList<ActionData> raw = await _actions.GetByPeriodAsync(from, to);
            foreach (ActionData action in raw)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (TwPingStats row in TwPingParser.ParseMany(action.Console, action.ErrorConsole, action.TaskId))
                {
                    row.CallLine = action.CallLine;
                    row.Title = await GetTitleAsync(row.Id ?? Guid.Empty);
                    await writer.WriteLineAsync(TwPingParser.ToCsvLine(row, separator, decimalSeparator));
                }
            }
            await writer.FlushAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> ImportCsvAsync(
            Stream csv, string sourceName, string delimiter, string dateTimeFormat, CancellationToken cancellationToken)
        {
            CsvRow[] rows = await ReadRowsAsync(csv, delimiter, dateTimeFormat, cancellationToken);
            _logger.Info("Импорт CSV {File}: строк {Count}", sourceName, rows.Length);

            DateTime now = DateTime.Now;
            List<string> rejected = [];
            List<TaskInfo> tasks = [];

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

                tasks.Add(task);
            }

            // Пакетная заливка: одна запись в БД и один SetJobs на пачку для каждой пробы.
            await _taskService.AddRangeAsync(tasks, cancellationToken);

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

        /// <summary>Возвращает название задачи по идентификатору, кэшируя результат.</summary>
        private async Task<string> GetTitleAsync(Guid id)
        {
            if (_cache.TryGetValue(id, out string? cached))
            {
                return cached ?? "";
            }

            TaskInfo? task = await _tasks.GetByIdAsync(id);
            string title = task?.Title ?? "not find";

            // Найденное имя держим дольше, «не найдено» — короткое время, вдруг задача появится.
            TimeSpan lifetime = task is null ? TimeSpan.FromMinutes(1) : TitleCacheDuration;
            _ = _cache.Set(id, title, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = lifetime,
                Priority = CacheItemPriority.High,
                Size = 1
            });

            return title;
        }
    }
}
