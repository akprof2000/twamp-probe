// Ignore Spelling: SPI Twamp

using LiteDB;
using SPI.Twamp.Server.Parser;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Разобранная статистика замера, сохранённая в БД при приёме результата.
    /// <para>
    /// Парсинг сырого вывода зонда выполняется один раз при поступлении данных,
    /// поэтому выгрузка отчёта читает готовые записи и не разбирает текст повторно.
    /// </para>
    /// </summary>
    public class StatRecord
    {
        /// <summary>
        /// Идентификатор записи в БД.
        /// </summary>
        [BsonId]
        public ObjectId? Id { get; set; }

        /// <summary>
        /// Момент создания исходного результата (для фильтра по периоду).
        /// </summary>
        public DateTime Creation { get; set; }

        /// <summary>
        /// Идентификатор задачи.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// Фактическая строка вызова зонда (для идентификации ответа).
        /// </summary>
        public string CallLine { get; set; } = "";

        /// <summary>
        /// Разобранная статистика сеанса.
        /// </summary>
        public TwPingStats Stats { get; set; } = new();
    }
}
