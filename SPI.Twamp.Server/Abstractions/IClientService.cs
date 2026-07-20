// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Прикладной сервис управления пробами (клиентами): регистрация, подтверждение,
    /// перечисление и запуск опроса.
    /// </summary>
    public interface IClientService
    {
        /// <summary>Возвращает список подтверждённых проб.</summary>
        Task<IReadOnlyList<Client>> GetClientsAsync();

        /// <summary>Возвращает список неопознанных проб (ожидающих подтверждения).</summary>
        Task<IReadOnlyList<Identify>> GetUnidentifiedAsync();

        /// <summary>
        /// Обращается к пробе по адресу (CheckIn) и регистрирует её как неопознанную,
        /// если она ещё не подтверждена.
        /// </summary>
        Task CheckInAsync(string probeUrl, CancellationToken cancellationToken);

        /// <summary>
        /// Подтверждает пробу: сохраняет её как клиента и запускает фоновый опрос.
        /// Для уже существующей пробы обновляет её данные.
        /// </summary>
        Task SetInfoAsync(Client client, CancellationToken cancellationToken);

        /// <summary>
        /// Удаляет подтверждённую пробу: останавливает её опрос, убирает из БД
        /// и (по флагу) помечает удалёнными все её задачи.
        /// </summary>
        /// <param name="requestInfo">Адрес пробы (RequestInfo).</param>
        /// <param name="deleteTasks">Удалить ли вместе с пробой все её задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns><c>true</c>, если проба существовала и удалена.</returns>
        Task<bool> DeleteAsync(string requestInfo, bool deleteTasks, CancellationToken cancellationToken);

        /// <summary>
        /// Отклоняет неопознанную пробу — убирает её из очереди на подтверждение.
        /// </summary>
        /// <param name="requestInfo">Адрес пробы (RequestInfo).</param>
        Task RejectUnidentifiedAsync(string requestInfo);
    }
}
