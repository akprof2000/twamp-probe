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
    /// Сервис массового создания задач: хранит именованные наборы шаблонов и
    /// накладывает выбранный набор на файл маршрутизаторов
    /// (задач = маршрутизаторы × шаблоны набора).
    /// </summary>
    public interface IProvisioningService
    {
        /// <summary>
        /// Загружает файл шаблонов (CSV с заголовками, «;») в набор с указанным именем.
        /// Повторная загрузка с тем же именем обновляет набор; другие наборы не трогаются.
        /// </summary>
        /// <param name="csv">Поток с содержимым CSV.</param>
        /// <param name="setName">Имя набора (по умолчанию — имя файла).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Число загруженных шаблонов.</returns>
        Task<int> UploadTemplatesAsync(Stream csv, string setName, CancellationToken cancellationToken);

        /// <summary>Возвращает все шаблоны всех наборов.</summary>
        Task<IReadOnlyList<ProbeTemplate>> GetTemplatesAsync();

        /// <summary>Возвращает список наборов шаблонов (имя и число шаблонов).</summary>
        Task<IReadOnlyList<(string SetName, int Count)>> GetTemplateSetsAsync();

        /// <summary>Удаляет набор шаблонов целиком.</summary>
        /// <returns>Число удалённых шаблонов.</returns>
        Task<int> DeleteTemplateSetAsync(string setName);

        /// <summary>
        /// Разбирает файл маршрутизаторов и накладывает на него шаблоны.
        /// Время создания задач — момент вызова; Start/End вычисляются из шаблона.
        /// </summary>
        /// <param name="routersFile">Поток с файлом маршрутизаторов.</param>
        /// <param name="setName">Имя применяемого набора; пусто — все наборы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<ProvisioningResult> GenerateAsync(Stream routersFile, string? setName, CancellationToken cancellationToken);

        /// <summary>
        /// Формирует CSV формата «Base test.csv» из сгенерированных задач
        /// (для предпросмотра или последующей загрузки через UploadCsv).
        /// </summary>
        /// <param name="tasks">Сгенерированные задачи.</param>
        /// <returns>Содержимое CSV-файла.</returns>
        byte[] BuildCsv(IReadOnlyList<TaskInfo> tasks);
    }
}
