// Ignore Spelling: SPI Twamp

using System.Text.Json.Serialization;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Режим зондирования: системный ping, утилита TWamp или TWampy.
    /// </summary>
    // Конвертер для System.Text.Json (Flurl): проба отдаёт enum строкой — нужно для
    // чтения задач с пробы при восстановлении. Newtonsoft/LiteDB его игнорируют.
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
