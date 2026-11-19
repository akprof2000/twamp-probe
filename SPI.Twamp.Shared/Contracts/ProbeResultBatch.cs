// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Пачка результатов, полученная от пробы (ответ CheckData).
    /// Требует подтверждения (ConfirmData) после успешной записи в БД —
    /// до подтверждения проба хранит и повторно выдаёт эту пачку.
    /// </summary>
    public class ProbeResultBatch
    {
        /// <summary>
        /// Идентификатор пачки для подтверждения. <see cref="Guid.Empty"/> — пачка пуста.
        /// </summary>
        public Guid BatchId { get; set; }

        /// <summary>
        /// Результаты замеров в пачке.
        /// </summary>
        public ActionData[] Items { get; set; } = [];
    }
}
