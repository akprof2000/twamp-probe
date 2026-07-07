// Ignore Spelling: SPI Twamp

using LiteDB;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Подтверждённая проба (клиент), которую сервер опрашивает.
    /// Расширяет идентификационные данные пробы.
    /// </summary>
    /// <seealso cref="Identify" />
    public class Client : Identify
    {
        /// <summary>
        /// Идентификатор записи в БД.
        /// </summary>
        [BsonId]
        public ObjectId? Id { get; set; }

        /// <summary>
        /// Отображаемое имя пробы.
        /// </summary>
        public string Name { get; set; } = "New One";
    }
}
