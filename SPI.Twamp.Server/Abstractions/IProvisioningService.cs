// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Итог генерации задач из файла маршрутизаторов и шаблонов.
    /// </summary>
    /// <param name="Tasks">Сгенерированные задачи (маршрутизаторы × шаблоны).</param>
    /// <param name="Routers">Число распознанных маршрутизаторов.</param>
    /// <param name="Templates">Число применённых шаблонов.</param>
    /// <param name="Rejected">Строки/шаблоны, которые не удалось разобрать (с причиной).</param>
    public record ProvisioningResult(
        IReadOnlyList<TaskInfo> Tasks,
        int Routers,
        int Templates,
        IReadOnlyList<string> Rejected);

    /// <summary>
    /// Сервис массового создания задач: хранит шаблоны и накладывает их на файл
    /// со списком маршрутизаторов (задач = маршрутизаторы × шаблоны).
    /// </summary>
    public interface IProvisioningService
    {
        /// <summary>
        /// Загружает файл шаблонов (CSV с заголовками, «;»), замещая предыдущий набор.
        /// </summary>
        /// <param name="csv">Поток с содержимым CSV.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Число загруженных шаблонов.</returns>
        Task<int> UploadTemplatesAsync(Stream csv, CancellationToken cancellationToken);

        /// <summary>Возвращает текущий набор шаблонов.</summary>
        Task<IReadOnlyList<ProbeTemplate>> GetTemplatesAsync();

        /// <summary>
        /// Разбирает файл маршрутизаторов и накладывает на него сохранённые шаблоны.
        /// Время создания задач — момент вызова; Start/End вычисляются из шаблона.
        /// </summary>
        /// <param name="routersFile">Поток с файлом маршрутизаторов.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<ProvisioningResult> GenerateAsync(Stream routersFile, CancellationToken cancellationToken);

        /// <summary>
        /// Формирует CSV формата «Base test.csv» из сгенерированных задач
        /// (для предпросмотра или последующей загрузки через UploadCsv).
        /// </summary>
        /// <param name="tasks">Сгенерированные задачи.</param>
        /// <returns>Содержимое CSV-файла.</returns>
        byte[] BuildCsv(IReadOnlyList<TaskInfo> tasks);
    }
}
