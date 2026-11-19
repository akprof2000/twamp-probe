// Ignore Spelling: SPI Twamp Clickhouse

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Запись результатов в ClickHouse через HTTP-интерфейс базы.
    /// </summary>
    public interface IClickHouseWriter
    {
        /// <summary>Выгрузка включена в конфигурации (секция <c>ClickHouse:Enabled</c>).</summary>
        bool Enabled { get; }

        /// <summary>Адрес HTTP-интерфейса базы.</summary>
        string Url { get; }

        /// <summary>Назначение вставки в виде <c>база.таблица</c>.</summary>
        string Target { get; }

        /// <summary>Создаёт базу и таблицу, если их ещё нет.</summary>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task EnsureTableAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Отправляет файл сегмента (NDJSON) в таблицу одним запросом <c>FORMAT JSONEachRow</c>.
        /// </summary>
        /// <param name="path">Путь файла сегмента.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task InsertSegmentAsync(string path, CancellationToken cancellationToken);
    }
}
