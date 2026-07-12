// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
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
        /// Зонд TWampy — выполняется и обрабатывается так же, как TWamp,
        /// но своим исполняемым файлом; в системе это третий тип задач.
        /// </summary>
        TWampy
    }
}
