// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Репозиторий шаблонов задач: хранение набора шаблонов, загружаемых из CSV.
    /// </summary>
    public interface ITemplateRepository
    {
        /// <summary>
        /// Полностью заменяет набор шаблонов новым (загрузка файла шаблонов
        /// замещает предыдущий набор).
        /// </summary>
        /// <param name="templates">Новый набор шаблонов.</param>
        Task ReplaceAllAsync(IEnumerable<ProbeTemplate> templates);

        /// <summary>Возвращает все сохранённые шаблоны.</summary>
        Task<IReadOnlyList<ProbeTemplate>> GetAllAsync();
    }
}
