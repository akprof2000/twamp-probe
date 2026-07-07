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
        public async Task EnsureIndexesAsync() =>
            _ = await _context.Actions.EnsureIndexAsync("Creation", "$.Creation");

        /// <inheritdoc/>
        public async Task AddRangeAsync(IEnumerable<ActionData> data) =>
            _ = await _context.Actions.InsertAsync(data);

        /// <inheritdoc/>
        public async Task<IReadOnlyList<ActionData>> GetByPeriodAsync(DateTime from, DateTime to)
        {
            // Учитываем записи без даты (Creation == null) — они попадают в любой период.
            IEnumerable<ActionData> data = await _context.Actions.FindAsync(
                x => x.Creation == null || (x.Creation >= from && x.Creation <= to));
            return [.. data];
        }
    }
}
