// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Репозиторий разобранной статистики замеров (парсинг выполняется при приёме
    /// результатов, выгрузка читает готовые записи постранично).
    /// </summary>
    public interface IStatRepository
    {
        /// <summary>Создаёт необходимые индексы коллекции (вызывается при старте).</summary>
        Task EnsureIndexesAsync();

        /// <summary>Сохраняет пачку записей статистики.</summary>
        Task AddRangeAsync(IEnumerable<StatRecord> records);

        /// <summary>Возвращает число записей за период.</summary>
        Task<int> CountByPeriodAsync(DateTime from, DateTime to);

        /// <summary>
        /// Возвращает страницу записей за период (для потоковой выгрузки без
        /// загрузки всего периода в память).
        /// </summary>
        /// <param name="from">Начало периода.</param>
        /// <param name="to">Конец периода.</param>
        /// <param name="skip">Сколько записей пропустить.</param>
        /// <param name="take">Размер страницы.</param>
        Task<IReadOnlyList<StatRecord>> GetPageAsync(DateTime from, DateTime to, int skip, int take);

        /// <summary>Удаляет записи старше указанной даты (ретенция).</summary>
        /// <returns>Число удалённых записей.</returns>
        Task<int> DeleteOlderAsync(DateTime cutoff);
    }
}
