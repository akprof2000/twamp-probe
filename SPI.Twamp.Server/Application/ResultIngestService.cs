// Ignore Spelling: SPI Twamp Clickhouse

using Microsoft.Extensions.Caching.Memory;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Parser;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Реализация <see cref="IResultIngestService"/>: вывод зонда разбирается сразу при
    /// приёме, готовые строки складываются в буфер. В базе сервера результаты больше
    /// не хранятся — постоянное место их жизни это ClickHouse.
    /// </summary>
    public sealed class ResultIngestService(
        IResultSpool spool, ITaskRepository tasks, IMemoryCache cache) : IResultIngestService
    {
        /// <summary>Срок хранения названия задачи в кэше.</summary>
        private static readonly TimeSpan TitleCacheDuration = TimeSpan.FromMinutes(60);

        private readonly IResultSpool _spool = spool;
        private readonly ITaskRepository _tasks = tasks;
        private readonly IMemoryCache _cache = cache;

        /// <inheritdoc/>
        public bool IsBackpressured => _spool.IsFull;

        /// <inheritdoc/>
        public async Task<int> IngestAsync(IReadOnlyList<ActionData> items, CancellationToken cancellationToken)
        {
            List<ExportRow> rows = [];
            foreach (ActionData action in items)
            {
                string title = await GetTitleAsync(action.TaskId);
                int rowNo = 0;

                // Парсер выбирается по режиму задачи (у twampy свой формат вывода);
                // один запуск зонда может дать несколько блоков статистики.
                foreach (TwPingStats parsed in
                         ProbeOutputParser.Parse(action.Mode, action.Console, action.ErrorConsole, action.TaskId))
                {
                    rows.Add(ExportRow.Create(action, parsed, rowNo++, title));
                }
            }

            await _spool.AppendAsync(rows, cancellationToken);
            return rows.Count;
        }

        /// <summary>Возвращает название задачи по идентификатору, кэшируя результат.</summary>
        private async Task<string> GetTitleAsync(Guid id)
        {
            if (_cache.TryGetValue(id, out string? cached))
            {
                return cached ?? "";
            }

            TaskInfo? task = await _tasks.GetByIdAsync(id);
            string title = task?.Title ?? "not find";

            // Найденное имя держим дольше, «не найдено» — недолго, вдруг задача появится.
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
