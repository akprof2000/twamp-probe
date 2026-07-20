// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Репозиторий проб (клиентов) и «неопознанных» проб, ожидающих подтверждения.
    /// <para>
    /// Коллекция clients — подтверждённые пробы, которые сервер опрашивает;
    /// коллекция identify — пробы, отметившиеся (CheckIn), но ещё не заведённые оператором.
    /// </para>
    /// </summary>
    public interface IClientRepository
    {
        /// <summary>Возвращает все подтверждённые пробы.</summary>
        Task<IReadOnlyList<Client>> GetAllAsync();

        /// <summary>Возвращает пробу по её адресу (RequestInfo) или <c>null</c>.</summary>
        Task<Client?> GetByRequestInfoAsync(string requestInfo);

        /// <summary>Проверяет, существует ли подтверждённая проба с указанным адресом.</summary>
        Task<bool> ExistsAsync(string requestInfo);

        /// <summary>Добавляет новую подтверждённую пробу.</summary>
        Task InsertAsync(Client client);

        /// <summary>Обновляет данные существующей пробы.</summary>
        Task UpdateAsync(Client client);

        /// <summary>Удаляет подтверждённую пробу. Возвращает <c>true</c>, если проба существовала.</summary>
        Task<bool> DeleteAsync(string requestInfo);

        /// <summary>Возвращает список неопознанных проб (ожидающих подтверждения).</summary>
        Task<IReadOnlyList<Identify>> GetUnidentifiedAsync();

        /// <summary>Проверяет наличие неопознанной пробы с указанным адресом.</summary>
        Task<bool> IdentifyExistsAsync(string requestInfo);

        /// <summary>Возвращает запись неопознанной пробы по адресу или <c>null</c>.</summary>
        Task<Identify?> GetIdentifyAsync(string requestInfo);

        /// <summary>Добавляет неопознанную пробу в очередь на подтверждение.</summary>
        Task AddIdentifyAsync(Identify identify);

        /// <summary>Удаляет пробу из списка неопознанных (например, после подтверждения).</summary>
        Task RemoveIdentifyAsync(string requestInfo);

        /// <summary>Регистрирует отложенную очистку удалённой пробы (перезаписывает существующую).</summary>
        Task AddCleanupAsync(PendingProbeCleanup cleanup);

        /// <summary>Возвращает все отложенные очистки удалённых проб.</summary>
        Task<IReadOnlyList<PendingProbeCleanup>> GetCleanupsAsync();

        /// <summary>Снимает отложенную очистку (выполнена или истёк срок ожидания).</summary>
        Task RemoveCleanupAsync(string requestInfo);
    }
}
