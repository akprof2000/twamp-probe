// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Репозиторий задач: доступ к коллекции заданий в хранилище (LiteDB).
    /// Инкапсулирует все операции чтения/записи задач, скрывая детали БД от бизнес-логики.
    /// </summary>
    public interface ITaskRepository
    {
        /// <summary>Добавляет новую задачу или обновляет существующую (по идентификатору).</summary>
        Task UpsertAsync(TaskInfo task);

        /// <summary>Возвращает задачу по идентификатору или <c>null</c>, если её нет.</summary>
        Task<TaskInfo?> GetByIdAsync(Guid id);

        /// <summary>Возвращает все задачи, привязанные к указанной пробе (RequestInfo).</summary>
        Task<IReadOnlyList<TaskInfo>> GetByRequestInfoAsync(string requestInfo);

        /// <summary>Возвращает полный список задач.</summary>
        Task<IReadOnlyList<TaskInfo>> GetAllAsync();

        /// <summary>Помечает все задачи указанной пробы как удалённые (Delete = true).</summary>
        Task MarkDeletedByRequestInfoAsync(string requestInfo);
    }
}
