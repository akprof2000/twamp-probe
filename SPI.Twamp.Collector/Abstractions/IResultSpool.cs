// Ignore Spelling: SPI Twamp Clickhouse ndjson

using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>Запечатанный сегмент буфера, ожидающий отправки.</summary>
    /// <param name="Path">Путь файла сегмента.</param>
    /// <param name="Rows">Число строк в нём.</param>
    public record SpoolSegment(string Path, int Rows);

    /// <summary>
    /// Буфер результатов между пробами и ClickHouse: строки копятся в текущем сегменте,
    /// по достижении лимита строк или срока сегмент «запечатывается», а фоновая выгрузка
    /// отправляет запечатанные сегменты в ClickHouse и удаляет их.
    /// </summary>
    public interface IResultSpool
    {
        /// <summary>
        /// Достигнут предел числа запечатанных сегментов: ClickHouse недоступен слишком
        /// долго. Пока это так, опрос проб приостанавливается — данные ждут на пробах.
        /// </summary>
        bool IsFull { get; }

        /// <summary>Число запечатанных (ожидающих отправки) сегментов.</summary>
        int SealedCount { get; }

        /// <summary>Число строк в текущем (заполняемом) сегменте.</summary>
        int CurrentRows { get; }

        /// <summary>Всего строк ждёт отправки: запечатанные сегменты плюс текущий.</summary>
        long PendingRows { get; }

        /// <summary>Предел числа сегментов, после которого приём приостанавливается.</summary>
        int MaxSegments { get; }

        /// <summary>Предел числа строк в сегменте.</summary>
        int BatchRows { get; }

        /// <summary>Срок накопления сегмента, минут.</summary>
        int FlushMinutes { get; }

        /// <summary>
        /// Дописывает строки в текущий сегмент и гарантирует их попадание на диск.
        /// Запечатывает сегмент, если он достиг предела по числу строк.
        /// </summary>
        /// <param name="rows">Строки результатов.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task AppendAsync(IReadOnlyList<ExportRow> rows, CancellationToken cancellationToken);

        /// <summary>
        /// Запечатывает текущий сегмент, если он непустой и старше заданного срока.
        /// </summary>
        /// <returns><c>true</c>, если сегмент был запечатан.</returns>
        Task<bool> SealIfDueAsync();

        /// <summary>Запечатанные сегменты в порядке их создания.</summary>
        IReadOnlyList<SpoolSegment> GetSealedSegments();

        /// <summary>Удаляет отправленный в ClickHouse сегмент.</summary>
        /// <param name="path">Путь сегмента, полученный из <see cref="GetSealedSegments"/>.</param>
        void DeleteSegment(string path);

        /// <summary>
        /// Читает все строки, ещё не уехавшие в ClickHouse (запечатанные сегменты и текущий) —
        /// источник CSV-отчёта.
        /// </summary>
        /// <param name="cancellationToken">Токен отмены.</param>
        IAsyncEnumerable<ExportRow> ReadPendingAsync(CancellationToken cancellationToken);
    }
}
