// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Probe.Contracts
{
    /// <summary>
    /// 
    /// </summary>
    public class TaskInfo
    {
        /// <summary>
        /// Gets or sets the ip address.
        /// </summary>
        /// <value>
        /// The ip address.
        /// </value>
        public string IpAddress { get; set; } = "0.0.0.0";
        /// <summary>
        /// Gets or sets the identifier.
        /// </summary>
        /// <value>
        /// The identifier.
        /// </value>
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>
        /// Gets or sets the title.
        /// </summary>
        /// <value>
        /// The title.
        /// </value>
        public string Title { get; set; } = "empty";
        /// <summary>
        /// Gets or sets the type.
        /// </summary>
        /// <value>
        /// The type.
        /// </value>
        public TaskType Type { get; set; } = TaskType.Scheduler;
        /// <summary>
        /// Gets or sets the mode.
        /// </summary>
        /// <value>
        /// The mode.
        /// </value>
        public TaskMode Mode { get; set; } = TaskMode.TWamp;
        /// <summary>
        /// Gets or sets the cron expression.
        /// </summary>
        /// <value>
        /// The cron expression.
        /// </value>
        public string CronExpression { get; set; } = "*/1 * * * *";
        /// <summary>
        /// Gets or sets a value indicating whether [cron with seconds].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [cron with seconds]; otherwise, <c>false</c>.
        /// </value>
        public bool CronWithSeconds { get; set; } = false;
        /// <summary>
        /// Gets or sets a value indicating whether [continue if error].
        /// </summary>
        /// <value>
        ///   <c>true</c> if [continue if error]; otherwise, <c>false</c>.
        /// </value>
        public bool ContinueIfError { get; set; } = false;
        /// <summary>
        /// Gets or sets the repeats.
        /// </summary>
        /// <value>
        /// The repeats.
        /// </value>
        public int Repeats { get; set; } = 1;
        /// <summary>
        /// Gets or sets the circles.
        /// </summary>
        /// <value>
        /// The circles.
        /// </value>
        public int Circles { get; set; } = 1;
        /// <summary>
        /// Gets or sets the pause sec.
        /// </summary>
        /// <value>
        /// The pause sec.
        /// </value>
        public ulong PauseSec { get; set; } = 1;
        /// <summary>
        /// Индивидуальный таймаут выполнения одного запуска зонда, в секундах.
        /// Если процесс не завершится за это время — он принудительно завершается (Kill).
        /// Значение 0 означает «без ограничения по времени».
        /// </summary>
        /// <value>
        /// Таймаут выполнения в секундах (0 — без ограничения).
        /// </value>
        public int TimeoutSec { get; set; } = 0;
        /// <summary>
        /// Gets or sets the start.
        /// </summary>
        /// <value>
        /// The start.
        /// </value>
        public DateTime Start { get; set; }
        /// <summary>
        /// Gets or sets the end.
        /// </summary>
        /// <value>
        /// The end.
        /// </value>
        public DateTime End { get; set; }
        /// <summary>
        /// Gets or sets the create.
        /// </summary>
        /// <value>
        /// The create.
        /// </value>
        public DateTime Create { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether this <see cref="TaskInfo"/> is delete.
        /// </summary>
        /// <value>
        ///   <c>true</c> if delete; otherwise, <c>false</c>.
        /// </value>
        public bool Delete { get; set; } = false;
        /// <summary>
        /// Gets or sets the end node.
        /// </summary>
        /// <value>
        /// The end node.
        /// </value>
        public string EndNode { get; set; } = "0.0.0.0";
        /// <summary>
        /// Gets or sets the parameters.
        /// </summary>
        /// <value>
        /// The parameters.
        /// </value>
        public IDictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
        /// <summary>
        /// Gets or sets the request information.
        /// </summary>
        /// <value>
        /// The request information.
        /// </value>
        public string RequestInfo { get; set; } = "0.0.0.0";
    }

}
