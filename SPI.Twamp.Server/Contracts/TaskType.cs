// Ignore Spelling: SPI Twamp

using System.Text.Json.Serialization;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Тип задачи: разовая или по расписанию.
    /// </summary>
    // Конвертер для System.Text.Json (его использует Flurl): проба отдаёт enum строкой
    // («Scheduler») — без этого сервер не может прочитать задачи с пробы для восстановления.
    // Контроллеры (Newtonsoft) и LiteDB (BSON) эту аннотацию игнорируют.
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
