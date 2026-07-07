// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Probe.Contracts
{
    /// <summary>
    /// Режим зондирования: системный ping или утилита TWamp.
    /// </summary>
    public enum TaskMode
    {
        /// <summary>
        /// Системный ping (Windows).
        /// </summary>
        WinPing,
        /// <summary>
        /// Зонд TWamp.
        /// </summary>
        TWamp
    }
}
