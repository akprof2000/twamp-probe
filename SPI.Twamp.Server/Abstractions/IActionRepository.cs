// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Репозиторий результатов зондирования (ActionData), полученных от проб.
    /// </summary>
    public interface IActionRepository
    {
        /// <summary>Создаёт необходимые индексы коллекции (вызывается при старте).</summary>
        Task EnsureIndexesAsync();

        /// <summary>Сохраняет пачку результатов, полученных от пробы.</summary>
        Task AddRangeAsync(IEnumerable<ActionData> data);

        /// <summary>Возвращает результаты за период (по дате создания).</summary>
        Task<IReadOnlyList<ActionData>> GetByPeriodAsync(DateTime from, DateTime to);
    }
}
