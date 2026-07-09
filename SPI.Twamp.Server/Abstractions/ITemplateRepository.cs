// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Репозиторий шаблонов задач. Шаблоны объединены в именованные наборы:
    /// каждый загруженный CSV-файл — отдельный набор, который можно применять
    /// к файлам маршрутизаторов и удалять независимо от других.
    /// </summary>
    public interface ITemplateRepository
    {
        /// <summary>
        /// Заменяет содержимое набора с указанным именем (повторная загрузка файла
        /// обновляет набор); остальные наборы не затрагиваются.
        /// </summary>
        /// <param name="setName">Имя набора.</param>
        /// <param name="templates">Шаблоны набора.</param>
        Task ReplaceSetAsync(string setName, IEnumerable<ProbeTemplate> templates);

        /// <summary>Возвращает все шаблоны всех наборов.</summary>
        Task<IReadOnlyList<ProbeTemplate>> GetAllAsync();

        /// <summary>Возвращает шаблоны одного набора.</summary>
        Task<IReadOnlyList<ProbeTemplate>> GetBySetAsync(string setName);

        /// <summary>Возвращает имена наборов с числом шаблонов в каждом.</summary>
        Task<IReadOnlyList<(string SetName, int Count)>> GetSetsAsync();

        /// <summary>Удаляет набор целиком.</summary>
        /// <returns>Число удалённых шаблонов.</returns>
        Task<int> DeleteSetAsync(string setName);
    }
}
