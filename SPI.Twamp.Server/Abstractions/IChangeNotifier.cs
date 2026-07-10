// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Шина уведомлений об изменениях состояния сервера (задачи, результаты, пробы)
    /// для «длинного опроса» веб-интерфейса: вместо периодического поллинга клиент
    /// висит на <see cref="WaitAsync"/> и получает ответ сразу при изменении.
    /// </summary>
    public interface IChangeNotifier
    {
        /// <summary>Текущая версия состояния (монотонно растёт при каждом изменении).</summary>
        long Version { get; }

        /// <summary>Фиксирует изменение состояния и будит всех ожидающих клиентов.</summary>
        void Notify();

        /// <summary>
        /// Ожидает изменения состояния: если текущая версия уже больше
        /// <paramref name="knownVersion"/> — возвращается немедленно, иначе ждёт
        /// уведомления или таймаута. Ожидание асинхронное, поток пула не удерживается.
        /// </summary>
        /// <param name="knownVersion">Версия, известная клиенту.</param>
        /// <param name="timeout">Максимальное время ожидания.</param>
        /// <param name="cancellationToken">Токен отмены (разрыв соединения клиентом).</param>
        /// <returns>Актуальная версия состояния.</returns>
        Task<long> WaitAsync(long knownVersion, TimeSpan timeout, CancellationToken cancellationToken);
    }
}
