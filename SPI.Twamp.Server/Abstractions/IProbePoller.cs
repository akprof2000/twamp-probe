// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Управление фоновым опросом проб. Позволяет запустить опрос вновь
    /// зарегистрированной пробы, не дожидаясь перезапуска сервиса.
    /// </summary>
    public interface IProbePoller
    {
        /// <summary>
        /// Запускает фоновый цикл опроса указанной пробы.
        /// Повторный запуск для уже опрашиваемой пробы игнорируется.
        /// </summary>
        /// <param name="client">Проба, которую нужно начать опрашивать.</param>
        void StartPolling(Client client);
    }
}
