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

        /// <summary>
        /// Сохраняет пачку результатов, отбрасывая дубликаты по <see cref="ActionData.ResultId"/>
        /// (повторная доставка той же пачки после сбоя подтверждения).
        /// </summary>
        /// <param name="data">Результаты для сохранения.</param>
        /// <returns>Фактически добавленные (новые) записи.</returns>
        Task<IReadOnlyList<ActionData>> AddRangeAsync(IEnumerable<ActionData> data);

        /// <summary>Возвращает результаты за период (по дате создания).</summary>
        Task<IReadOnlyList<ActionData>> GetByPeriodAsync(DateTime from, DateTime to);

        /// <summary>
        /// Возвращает последний по времени результат задачи (для восстановления
        /// «последнего результата» после перезапуска сервера) или <c>null</c>.
        /// </summary>
        /// <param name="taskId">Идентификатор задачи.</param>
        Task<ActionData?> GetLastByTaskAsync(Guid taskId);

        /// <summary>Удаляет результаты старше указанной даты (ретенция).</summary>
        /// <returns>Число удалённых записей.</returns>
        Task<int> DeleteOlderAsync(DateTime cutoff);
    }
}
