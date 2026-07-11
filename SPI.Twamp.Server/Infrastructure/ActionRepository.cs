// Ignore Spelling: SPI Twamp

using LiteDB.Async;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="IActionRepository"/> поверх LiteDB.
    /// </summary>
    public sealed class ActionRepository(LiteDbContext context) : IActionRepository
    {
        private readonly LiteDbContext _context = context;

        /// <inheritdoc/>
        public async Task EnsureIndexesAsync()
        {
            _ = await _context.Actions.EnsureIndexAsync("Creation", "$.Creation");
            // Индекс для быстрой проверки дубликатов при повторной доставке пачек.
            _ = await _context.Actions.EnsureIndexAsync("ResultId", "$.ResultId");
            // Индекс для быстрого поиска последнего результата задачи (прогрев статусов).
            _ = await _context.Actions.EnsureIndexAsync("TaskId", "$.TaskId");
        }

        /// <inheritdoc/>
        public async Task<ActionData?> GetLastByTaskAsync(Guid taskId)
        {
            IEnumerable<ActionData> found = await _context.Actions
                .Query()
                .Where(x => x.TaskId == taskId)
                .OrderByDescending(x => x.Creation)
                .Limit(1)
                .ToListAsync();
            return found.FirstOrDefault();
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<ActionData>> AddRangeAsync(IEnumerable<ActionData> data)
        {
            // Доставка «минимум один раз»: после сбоя подтверждения проба пришлёт ту же
            // пачку повторно. Отбрасываем записи, чьи ResultId уже есть в БД.
            List<ActionData> fresh = [];
            foreach (ActionData item in data)
            {
                bool isDuplicate = item.ResultId != Guid.Empty &&
                    await _context.Actions.ExistsAsync(x => x.ResultId == item.ResultId);
                if (!isDuplicate)
                {
                    fresh.Add(item);
                }
            }

            if (fresh.Count > 0)
            {
                _ = await _context.Actions.InsertAsync(fresh);
            }
            return fresh;
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<ActionData>> GetByPeriodAsync(DateTime from, DateTime to)
        {
            // Учитываем записи без даты (Creation == null) — они попадают в любой период.
            IEnumerable<ActionData> data = await _context.Actions.FindAsync(
                x => x.Creation == null || (x.Creation >= from && x.Creation <= to));
            return [.. data];
        }

        /// <inheritdoc/>
        public async Task<int> DeleteOlderAsync(DateTime cutoff) =>
            await _context.Actions.DeleteManyAsync(x => x.Creation != null && x.Creation < cutoff);
    }
}
