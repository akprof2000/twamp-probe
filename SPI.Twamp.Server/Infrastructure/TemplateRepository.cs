// Ignore Spelling: SPI Twamp

using LiteDB.Async;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="ITemplateRepository"/> поверх LiteDB (наборы шаблонов).
    /// </summary>
    public sealed class TemplateRepository(LiteDbContext context) : ITemplateRepository
    {
        private readonly LiteDbContext _context = context;

        /// <inheritdoc/>
        public async Task ReplaceSetAsync(string setName, IEnumerable<ProbeTemplate> templates)
        {
            // Повторная загрузка файла обновляет только свой набор.
            _ = await _context.Templates.DeleteManyAsync(x => x.SetName == setName);
            List<ProbeTemplate> list = [.. templates];
            foreach (ProbeTemplate template in list)
            {
                template.SetName = setName;
            }
            if (list.Count > 0)
            {
                _ = await _context.Templates.InsertAsync(list);
            }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<ProbeTemplate>> GetAllAsync()
        {
            IEnumerable<ProbeTemplate> data = await _context.Templates.FindAllAsync();
            return [.. data];
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<ProbeTemplate>> GetBySetAsync(string setName)
        {
            IEnumerable<ProbeTemplate> data = await _context.Templates.FindAsync(x => x.SetName == setName);
            return [.. data];
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<(string SetName, int Count)>> GetSetsAsync()
        {
            IReadOnlyList<ProbeTemplate> all = await GetAllAsync();
            return [.. all
                .GroupBy(t => t.SetName)
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count()))];
        }

        /// <inheritdoc/>
        public async Task<int> DeleteSetAsync(string setName) =>
            await _context.Templates.DeleteManyAsync(x => x.SetName == setName);
    }
}
