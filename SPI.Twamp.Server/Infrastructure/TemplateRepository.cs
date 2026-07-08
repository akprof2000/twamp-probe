// Ignore Spelling: SPI Twamp

using LiteDB.Async;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="ITemplateRepository"/> поверх LiteDB.
    /// </summary>
    public sealed class TemplateRepository(LiteDbContext context) : ITemplateRepository
    {
        private readonly LiteDbContext _context = context;

        /// <inheritdoc/>
        public async Task ReplaceAllAsync(IEnumerable<ProbeTemplate> templates)
        {
            // Загрузка файла шаблонов замещает предыдущий набор целиком —
            // так состояние всегда соответствует последнему загруженному файлу.
            _ = await _context.Templates.DeleteAllAsync();
            _ = await _context.Templates.InsertAsync(templates);
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<ProbeTemplate>> GetAllAsync()
        {
            IEnumerable<ProbeTemplate> data = await _context.Templates.FindAllAsync();
            return [.. data];
        }
    }
}
