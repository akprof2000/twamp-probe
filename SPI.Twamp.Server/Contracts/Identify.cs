// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Идентификационные данные пробы (ответ пробы на CheckIn).
    /// </summary>
    public class Identify
    {
        /// <summary>
        /// IP-адрес пробы.
        /// </summary>
        public string IPAddress { get; set; } = "0.0.0.0";
        /// <summary>
        /// Имя хоста.
        /// </summary>
        public string HostName { get; set; } = "local";
        /// <summary>
        /// MAC-адрес.
        /// </summary>
        public string MacAddress { get; set; } = "00:00:00:00:00:00";
        /// <summary>
        /// Название сетевого интерфейса.
        /// </summary>
        public string? Title { get; set; }
        /// <summary>
        /// Описание сетевого интерфейса.
        /// </summary>
        public string? Description { get; set; }
        /// <summary>
        /// Идентификатор запроса (адрес пробы).
        /// </summary>
        public string RequestInfo { get; set; } = "0.0.0.0";
    }
}
