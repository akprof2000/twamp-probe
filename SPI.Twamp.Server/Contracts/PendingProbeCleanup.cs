// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Отложенное задание «снять все задачи с удалённой пробы».
    /// <para>
    /// Создаётся при удалении пробы. Фоновый цикл пытается дотянуться до пробы и
    /// отправить ей удаление всех заданий; если проба не появилась в течение
    /// «Probe:CleanupWaitHours» часов — задание снимается, а задачи пробы и кэш
    /// её результатов вычищаются на сервере.
    /// </para>
    /// </summary>
    public class PendingProbeCleanup
    {
        /// <summary>Идентификатор записи.</summary>
        public int Id { get; set; }

        /// <summary>Адрес удалённой пробы (RequestInfo).</summary>
        public string RequestInfo { get; set; } = "";

        /// <summary>Момент удаления пробы — от него отсчитывается срок ожидания.</summary>
        public DateTime DeletedAt { get; set; } = DateTime.Now;
    }
}
