// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Probe.Contracts
{
    /// <summary>
    /// Режим зондирования: системный ping, утилита TWamp или TWampy.
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
        TWamp,
        /// <summary>
        /// Зонд TWampy — запускается как TWamp (свой исполняемый файл),
        /// вывод разбирается тем же парсером и даёт те же поля.
        /// </summary>
        TWampy
    }
}
