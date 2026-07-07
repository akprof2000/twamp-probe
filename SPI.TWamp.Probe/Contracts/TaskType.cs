// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Probe.Contracts
{
    /// <summary>
    /// Тип задачи: разовая или по расписанию.
    /// </summary>
    public enum TaskType
    {
        /// <summary>
        /// Разовая задача (выполняется один раз).
        /// </summary>
        Repeater,
        /// <summary>
        /// Задача по расписанию (cron).
        /// </summary>
        Scheduler
    }
}
