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

        /// <summary>Забирает у пробы накопленные результаты (CheckData, длинный опрос).</summary>
        /// <param name="probeUrl">Базовый адрес пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<ActionData[]> GetResultsAsync(string probeUrl, CancellationToken cancellationToken);
    }
}
