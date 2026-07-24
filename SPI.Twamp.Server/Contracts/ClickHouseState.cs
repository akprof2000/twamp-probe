// Ignore Spelling: SPI Twamp Clickhouse

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Состояние переноса результатов в ClickHouse — то, что показывает
    /// вкладка «Хранилище» веб-интерфейса.
    /// </summary>
    /// <param name="Enabled">Выгрузка включена в конфигурации.</param>
    /// <param name="Url">Адрес HTTP-интерфейса базы.</param>
    /// <param name="Target">База и таблица назначения (<c>база.таблица</c>).</param>
    /// <param name="Online">Последняя попытка обращения к базе была успешной; <c>null</c> — обращений ещё не было.</param>
    /// <param name="LastError">Текст последней ошибки базы (<c>null</c> — ошибок нет).</param>
    /// <param name="LastErrorAt">Когда произошла последняя ошибка.</param>
    /// <param name="LastUploadAt">Когда последний сегмент успешно уехал в базу.</param>
    /// <param name="SegmentsUploaded">Сколько сегментов выгружено с момента запуска сервера.</param>
    /// <param name="RowsUploaded">Сколько строк выгружено с момента запуска сервера.</param>
    /// <param name="CurrentRows">Строк в заполняемом сегменте.</param>
    /// <param name="PendingRows">Строк всего ждёт отправки (очередь + заполняемый).</param>
    /// <param name="SealedSegments">Запечатанных сегментов в очереди.</param>
    /// <param name="MaxSegments">Предел числа сегментов (за ним — пауза приёма).</param>
    /// <param name="BatchRows">Порог строк, по которому сегмент запечатывается.</param>
    /// <param name="FlushMinutes">Срок, по которому сегмент запечатывается, минут.</param>
    /// <param name="Backpressured">Приём результатов с проб приостановлен: буфер полон.</param>
    public record ClickHouseState(
        bool Enabled,
        string Url,
        string Target,
        bool? Online,
        string? LastError,
        DateTime? LastErrorAt,
        DateTime? LastUploadAt,
        long SegmentsUploaded,
        long RowsUploaded,
        int CurrentRows,
        long PendingRows,
        int SealedSegments,
        int MaxSegments,
        int BatchRows,
        int FlushMinutes,
        bool Backpressured);
}
