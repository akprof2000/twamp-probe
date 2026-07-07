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
    }
}
