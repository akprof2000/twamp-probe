// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Прикладной сервис управления задачами: связывает хранилище задач с их
    /// доставкой на пробу. Изменения передаются пробе инкрементально (только
    /// изменившиеся задачи), а фоновая сверка досылает недостающее.
    /// </summary>
    public interface ITaskService
    {
        /// <summary>Возвращает полный список задач.</summary>
        Task<IReadOnlyList<TaskInfo>> GetAllAsync();

        /// <summary>Возвращает задачи, привязанные к указанной пробе.</summary>
        Task<IReadOnlyList<TaskInfo>> GetByRequestInfoAsync(string requestInfo);

        /// <summary>Сохраняет задачу и отправляет обновлённый список задач её пробе.</summary>
        Task AddAsync(TaskInfo task, CancellationToken cancellationToken);

        /// <summary>
        /// Массово сохраняет задачи и рассылает их пробам пакетами: задачи группируются
        /// по адресу пробы, и каждой пробе уходит один SetJobs на пачку вместо
        /// отдельного HTTP-запроса на каждую задачу.
        /// </summary>
        /// <param name="tasks">Задачи для сохранения и доставки.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task AddRangeAsync(IReadOnlyList<TaskInfo> tasks, CancellationToken cancellationToken);

        /// <summary>Помечает задачу удалённой и отправляет обновлённый список её пробе.</summary>
        Task DeleteAsync(Guid id, CancellationToken cancellationToken);

        /// <summary>
        /// Восстанавливает удалённую задачу: снимает пометку удаления и заново
        /// отправляет задачу её пробе.
        /// </summary>
        /// <param name="id">Идентификатор задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns><c>true</c>, если задача найдена и восстановлена.</returns>
        Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken);

        /// <summary>
        /// Массово помечает задачи удалёнными (<paramref name="deleted"/> = true)
        /// или восстанавливает их (false) и рассылает изменения пробам пакетами.
        /// Используется для операций над отфильтрованным списком.
        /// </summary>
        /// <param name="tasks">Задачи для изменения.</param>
        /// <param name="deleted">Новое состояние пометки удаления.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Число фактически изменённых задач.</returns>
        Task<int> SetDeletedManyAsync(IReadOnlyList<TaskInfo> tasks, bool deleted, CancellationToken cancellationToken);

        /// <summary>Помечает удалёнными все задачи пробы и отправляет ей обновлённый список.</summary>
        Task DeleteByRequestInfoAsync(string requestInfo, CancellationToken cancellationToken);

        /// <summary>
        /// Окончательно удаляет из БД все задачи пробы (очистка после удаления пробы,
        /// не вышедшей на связь). Возвращает идентификаторы удалённых задач.
        /// </summary>
        /// <param name="requestInfo">Адрес пробы (RequestInfo).</param>
        Task<IReadOnlyList<Guid>> PurgeByRequestInfoAsync(string requestInfo);

        /// <summary>
        /// Сверяет состояние пробы с хранилищем и приводит его в соответствие:
        /// досылает недостающие активные задачи (в т. ч. на чистую перезалитую пробу),
        /// удаляет с пробы устаревшие и помеченные на удаление. Устаревшие задачи
        /// (с истёкшей датой окончания) исключаются из передачи и помечаются удалёнными.
        /// </summary>
        /// <param name="requestInfo">Адрес пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task ReconcileAsync(string requestInfo, CancellationToken cancellationToken);
    }
}
