// Ignore Spelling: SPI Twamp

using LiteDB.Async;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="IStatRepository"/> поверх LiteDB (коллекция stats).
    /// </summary>
    public sealed class StatRepository(LiteDbContext context) : IStatRepository
    {
        private readonly LiteDbContext _context = context;

        /// <inheritdoc/>
        public async Task EnsureIndexesAsync() =>
            _ = await _context.Stats.EnsureIndexAsync("Creation", "$.Creation");

        /// <inheritdoc/>
        public async Task AddRangeAsync(IEnumerable<StatRecord> records)
        {
            List<StatRecord> list = [.. records];
            if (list.Count > 0)
            {
                _ = await _context.Stats.InsertAsync(list);
            }
        }

        /// <inheritdoc/>
        public async Task<int> CountByPeriodAsync(DateTime from, DateTime to) =>
            await _context.Stats.CountAsync(x => x.Creation >= from && x.Creation <= to);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<StatRecord>> GetPageAsync(DateTime from, DateTime to, int skip, int take)
        {
            IEnumerable<StatRecord> page = await _context.Stats
                .Query()
                .Where(x => x.Creation >= from && x.Creation <= to)
                .OrderBy(x => x.Creation)
                .Skip(skip)
                .Limit(take)
                .ToListAsync();
            return [.. page];
        }

        /// <inheritdoc/>
        public async Task<int> DeleteOlderAsync(DateTime cutoff) =>
            await _context.Stats.DeleteManyAsync(x => x.Creation < cutoff);
    }
}
