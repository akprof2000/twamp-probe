// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// HTTP-клиент для взаимодействия с удалённой пробой.
    /// Инкапсулирует все обращения к API пробы (адреса эндпоинтов, таймауты, сериализацию).
    /// </summary>
    public interface IProbeClient
    {
        /// <summary>Регистрирует пробу (CheckIn) и возвращает её идентификационные данные.</summary>
        /// <param name="probeUrl">Базовый адрес пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<Identify> CheckInAsync(string probeUrl, CancellationToken cancellationToken);

        /// <summary>Передаёт пробе актуальный список задач (SetJobs).</summary>
        /// <param name="probeUrl">Базовый адрес пробы.</param>
        /// <param name="tasks">Список задач для пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task PushTasksAsync(string probeUrl, IEnumerable<TaskInfo> tasks, CancellationToken cancellationToken);

        /// <summary>
        /// Забирает у пробы пачку результатов (CheckData, длинный опрос).
        /// Пачку нужно подтвердить через <see cref="ConfirmResultsAsync"/> после записи в БД.
        /// </summary>
        /// <param name="probeUrl">Базовый адрес пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<ProbeResultBatch> GetResultsAsync(string probeUrl, CancellationToken cancellationToken);

        /// <summary>
        /// Подтверждает пробе доставку пачки результатов — только после этого
        /// проба удаляет данные у себя.
        /// </summary>
        /// <param name="probeUrl">Базовый адрес пробы.</param>
        /// <param name="batchId">Идентификатор пачки из ответа CheckData.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task ConfirmResultsAsync(string probeUrl, Guid batchId, CancellationToken cancellationToken);

        /// <summary>
        /// Запрашивает у пробы идентификаторы задач по расписанию, которые она знает.
        /// Нужно для сверки и досылки недостающих задач (в т. ч. на чистую пробу).
        /// </summary>
        /// <param name="probeUrl">Базовый адрес пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<Guid[]> GetTaskIdsAsync(string probeUrl, CancellationToken cancellationToken);

        /// <summary>
        /// Запрашивает у пробы состояние выполнения задач (запущена ли сейчас,
        /// последний старт/финиш, ближайший запуск, последняя ошибка).
        /// Ответ отдаётся как есть (сырой JSON) — сервер лишь проксирует его в UI.
        /// </summary>
        /// <param name="probeUrl">Базовый адрес пробы.</param>
        /// <param name="query">Строка запроса с фильтрами и пагинацией (без «?»), может быть пустой.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<string> GetTaskStatusRawAsync(string probeUrl, string query, CancellationToken cancellationToken);
    }
}
