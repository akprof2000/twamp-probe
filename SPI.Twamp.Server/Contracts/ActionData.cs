// Ignore Spelling: SPI Twamp

using LiteDB;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Результат замера зонда, полученный от пробы и сохранённый в БД.
    /// </summary>
    public class ActionData
    {
        /// <summary>
        /// Идентификатор записи в БД.
        /// </summary>
        [BsonId]
        public ObjectId? Id { get; set; }

        /// <summary>
        /// Момент создания результата.
        /// </summary>
        public DateTime? Creation { get; set; }

        /// <summary>
        /// Идентификатор задачи, к которой относится результат.
        /// </summary>
        public Guid TaskId { get; set; }

        /// <summary>
        /// Конечный узел (адрес), для которого выполнялся зонд.
        /// </summary>
        public string EndNode { get; set; } = "0.0.0.0.0";

        /// <summary>
        /// Идентификатор запроса (адрес пробы-источника).
        /// </summary>
        public string RequestInfo { get; set; } = "0.0.0.0";

        /// <summary>
        /// Фактическая строка вызова зонда на пробе (исполняемый файл и аргументы).
        /// Служит для точной идентификации ответа в отчётах.
        /// </summary>
        public string CallLine { get; set; } = "";
        /// <summary>
        /// Стандартный вывод процесса зонда.
        /// </summary>
        public string Console { get; set; } = "";

        /// <summary>
        /// Вывод ошибок процесса зонда.
        /// </summary>
        public string ErrorConsole { get; set; } = "";
    }
}
