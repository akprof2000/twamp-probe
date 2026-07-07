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
        /// Формирует CSV-отчёт по результатам зондирования за период.
        /// </summary>
        /// <param name="from">Начало периода.</param>
        /// <param name="to">Конец периода.</param>
        /// <param name="separator">Разделитель полей CSV.</param>
        /// <param name="decimalSeparator">Десятичный разделитель чисел.</param>
        /// <returns>Содержимое файла и его имя.</returns>
        Task<(byte[] Content, string FileName)> BuildCsvAsync(
            DateTime from, DateTime to, char separator, char decimalSeparator);

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
