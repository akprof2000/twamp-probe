// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Probe.Contracts
{
    /// <summary>
    /// Пачка результатов, выдаваемая серверу через CheckData.
    /// <para>
    /// Пачка считается доставленной только после подтверждения сервером
    /// (ConfirmData с тем же <see cref="BatchId"/>). До подтверждения проба
    /// хранит пачку и повторно выдаёт её при следующем опросе — так результаты
    /// не теряются, если сервер упал между получением и записью в БД.
    /// </para>
    /// </summary>
    public class ResultBatch
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
