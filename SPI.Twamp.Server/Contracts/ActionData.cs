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
        /// Уникальный идентификатор результата, присвоенный пробой.
        /// По нему отбрасываются дубликаты при повторной доставке пачки.
        /// </summary>
        public Guid ResultId { get; set; }

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
        /// Тип запроса, которым выполнена задача (WinPing / TWamp / TWampy).
        /// </summary>
        public string Mode { get; set; } = "";
        /// <summary>
        /// Фактическая строка вызова зонда на пробе (исполняемый файл и аргументы).
        /// Служит для точной идентификации ответа в отчётах.
        /// </summary>
        public string CallLine { get; set; } = "";
        /// <summary>
        /// Исход запуска зонда на пробе: Success (процесс завершился сам, код 0),
        /// ExitCodeError (завершился сам с ошибкой приложения), TimedOut (убит по
        /// таймауту), StartFailed (не удалось запустить); пусто — старая версия пробы.
        /// </summary>
        public string Outcome { get; set; } = "";
        /// <summary>
        /// Код выхода процесса зонда (0 — корректное завершение;
        /// null — процесс не удалось запустить).
        /// </summary>
        public int? ExitCode { get; set; }
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
