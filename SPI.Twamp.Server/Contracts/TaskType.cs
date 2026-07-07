// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
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
