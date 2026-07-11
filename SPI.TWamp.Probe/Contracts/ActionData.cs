// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Probe.Contracts
{
    /// <summary>
    /// Результат одного замера зонда, передаваемый серверу.
    /// </summary>
    public class ActionData
    {
        /// <summary>
        /// Уникальный идентификатор результата. Присваивается пробой и позволяет
        /// серверу отбрасывать дубликаты при повторной доставке пачки
        /// (доставка «минимум один раз» + дедупликация = «ровно один раз»).
        /// </summary>
        public Guid ResultId { get; set; } = Guid.NewGuid();
        /// <summary>
        /// Момент создания результата.
        /// </summary>
        public DateTime Creation { get; set; } = DateTime.Now;
        /// <summary>
        /// Идентификатор задачи, к которой относится результат.
        /// </summary>
        public Guid TaskId { get; set; }
        /// <summary>
        /// Конечный узел (адрес), для которого выполнялся зонд.
        /// </summary>
        public string EndNode { get; set; } = "0.0.0.0.0";
        /// <summary>
        /// IP-адрес, связанный с задачей.
        /// </summary>
        public string IPAddress { get; set; } = "0.0.0.0";
        /// <summary>
        /// Идентификатор запроса (адрес сервера-источника задачи).
        /// </summary>
        public string RequestInfo { get; set; } = "0.0.0.0";
        /// <summary>
        /// Фактическая строка вызова зонда (исполняемый файл и аргументы).
        /// Служит для точной идентификации ответа в отчётах.
        /// </summary>
        public string CallLine { get; set; } = "";
        /// <summary>
        /// Код выхода процесса зонда (0 — корректное завершение;
        /// null — процесс не удалось запустить).
        /// </summary>
        public int? ExitCode { get; set; }
        /// <summary>
        /// Исход запуска зонда: Success (процесс завершился сам, код 0),
        /// ExitCodeError (завершился сам, но с ошибкой приложения),
        /// TimedOut (убит по таймауту), StartFailed (не удалось запустить).
        /// Разделяет статус выполнения процесса и статус результата приложения.
        /// </summary>
        public string Outcome { get; set; } = "";
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
