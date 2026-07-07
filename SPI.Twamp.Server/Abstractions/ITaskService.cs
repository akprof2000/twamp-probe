// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Прикладной сервис управления задачами: связывает хранилище задач с их
    /// доставкой на пробу. После любого изменения актуальный список задач пробы
    /// отправляется ей повторно.
    /// </summary>
    public interface ITaskService
    {
        /// <summary>Возвращает полный список задач.</summary>
        Task<IReadOnlyList<TaskInfo>> GetAllAsync();

        /// <summary>Возвращает задачи, привязанные к указанной пробе.</summary>
        Task<IReadOnlyList<TaskInfo>> GetByRequestInfoAsync(string requestInfo);

        /// <summary>Сохраняет задачу и отправляет обновлённый список задач её пробе.</summary>
        Task AddAsync(TaskInfo task, CancellationToken cancellationToken);

        /// <summary>Помечает задачу удалённой и отправляет обновлённый список её пробе.</summary>
        Task DeleteAsync(Guid id, CancellationToken cancellationToken);

        /// <summary>Помечает удалёнными все задачи пробы и отправляет ей обновлённый список.</summary>
        Task DeleteByRequestInfoAsync(string requestInfo, CancellationToken cancellationToken);
    }
}
