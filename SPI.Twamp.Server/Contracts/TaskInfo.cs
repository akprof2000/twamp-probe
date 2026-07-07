// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Описание задачи зондирования, хранимое сервером и передаваемое пробе.
    /// </summary>
    public class TaskInfo
    {
        /// <summary>
        /// Идентификатор запроса — адрес пробы, которой принадлежит задача.
        /// </summary>
        public string RequestInfo { get; set; } = "0.0.0.0";
        /// <summary>
        /// Уникальный идентификатор задачи.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>
        /// Человекочитаемое название задачи.
        /// </summary>
        public string Title { get; set; } = "empty";
        /// <summary>
        /// Тип задачи (разовая или по расписанию).
        /// </summary>
        public TaskType Type { get; set; } = TaskType.Scheduler;
        /// <summary>
        /// Режим зондирования (ping или TWamp).
        /// </summary>
        public TaskMode Mode { get; set; } = TaskMode.TWamp;
        /// <summary>
        /// Cron-выражение расписания.
        /// </summary>
        public string CronExpression { get; set; } = "*/1 * * * *";
        /// <summary>
        /// Учитывать ли секунды в cron-выражении.
        /// </summary>
        public bool CronWithSeconds { get; set; } = false;
        /// <summary>
        /// Продолжать ли выполнение при ошибке.
        /// </summary>
        public bool ContinueIfError { get; set; } = false;
        /// <summary>
        /// Количество повторов зонда внутри одного цикла.
        /// </summary>
        public uint Repeats { get; set; } = 1;
        /// <summary>
        /// Количество циклов замера.
        /// </summary>
        public uint Circles { get; set; } = 1;
        /// <summary>
        /// Пауза между циклами, в секундах.
        /// </summary>
        public ulong PauseSec { get; set; } = 1;
        /// <summary>
        /// Индивидуальный таймаут выполнения одного запуска зонда, в секундах.
        /// Значение передаётся пробе: если процесс не завершится за это время,
        /// проба принудительно завершит его (Kill). Значение 0 — без ограничения.
        /// </summary>
        public int TimeoutSec { get; set; } = 0;
        /// <summary>
        /// Дата начала действия задачи.
        /// </summary>
        public DateTime Start { get; set; }
        /// <summary>
        /// Дата окончания действия задачи.
        /// </summary>
        public DateTime End { get; set; }
        /// <summary>
        /// Дата создания задачи.
        /// </summary>
        public DateTime Create { get; set; }
        /// <summary>
        /// Признак того, что задача помечена на удаление.
        /// </summary>
        public bool Delete { get; set; } = false;
        /// <summary>
        /// Список конечных узлов (адресов) через «;» или «,».
        /// </summary>
        public string EndNode { get; set; } = "0.0.0.0";
        /// <summary>
        /// Дополнительные параметры командной строки зонда.
        /// </summary>
        public IDictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    }
}
