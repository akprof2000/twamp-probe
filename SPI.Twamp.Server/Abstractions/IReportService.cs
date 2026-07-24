// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Сервис отчётов и импорта: выгрузка результатов зондирования в CSV и
    /// загрузка задач из CSV-файла.
    /// </summary>
    public interface IReportService
    {
        /// <summary>
        /// Потоково пишет CSV-отчёт в указанный писатель.
        /// <para>
        /// Источник — буфер результатов, то есть данные, ещё не уехавшие в ClickHouse.
        /// Выбора периода нет: вся история живёт в ClickHouse и запрашивается оттуда.
        /// Чтение потоковое, поэтому объём буфера не влияет на память сервера.
        /// </para>
        /// </summary>
        /// <param name="separator">Разделитель полей CSV.</param>
        /// <param name="decimalSeparator">Десятичный разделитель чисел.</param>
        /// <param name="writer">Куда писать CSV (тело HTTP-ответа).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        Task StreamCsvAsync(
            char separator, char decimalSeparator, TextWriter writer, CancellationToken cancellationToken);

        /// <summary>
        /// Импортирует задачи из CSV-потока и ставит их на выполнение.
        /// </summary>
        /// <param name="csv">Поток с содержимым CSV.</param>
        /// <param name="sourceName">Имя исходного файла (для названий задач).</param>
        /// <param name="delimiter">Разделитель полей CSV.</param>
        /// <param name="dateTimeFormat">Формат дат в файле.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Названия задач с некорректной датой окончания (не приняты).</returns>
        Task<IReadOnlyList<string>> ImportCsvAsync(
            Stream csv, string sourceName, string delimiter, string dateTimeFormat, CancellationToken cancellationToken);
    }
}
