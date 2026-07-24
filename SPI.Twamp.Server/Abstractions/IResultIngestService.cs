// Ignore Spelling: SPI Twamp Clickhouse

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Приём результатов от проб: разбор вывода зонда в строки формата экспорта
    /// и запись их в буфер, откуда они уезжают в ClickHouse.
    /// </summary>
    public interface IResultIngestService
    {
        /// <summary>
        /// Буфер переполнен (ClickHouse долго недоступен) — приём новых результатов
        /// приостановлен, опрашивать пробы не следует.
        /// </summary>
        bool IsBackpressured { get; }

        /// <summary>
        /// Разбирает пачку результатов и укладывает её в буфер. Возвращает число
        /// записанных строк. Метод завершается только после сброса данных на диск —
        /// подтверждать пачку пробе можно лишь после его успешного завершения.
        /// </summary>
        /// <param name="items">Сырые результаты от пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task<int> IngestAsync(IReadOnlyList<ActionData> items, CancellationToken cancellationToken);
    }
}
