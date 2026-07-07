// Ignore Spelling: SPI Twamp

using CsvHelper.Configuration.Attributes;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// 
    /// </summary>
    public class CsvRow
    {
        /// <summary>
        /// Gets or sets the nri identifier.
        /// </summary>
        /// <value>
        /// The nri identifier.
        /// </value>
        public ulong? NriId { get; set; }
        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        /// <value>
        /// The name.
        /// </value>
        public string? Name{ get; set; }
        /// <summary>
        /// Gets or sets the name of the host.
        /// </summary>
        /// <value>
        /// The name of the host.
        /// </value>
        public string? HostName { get; set; }
        /// <summary>
        /// Gets or sets the ip.
        /// </summary>
        /// <value>
        /// The ip.
        /// </value>
        public string Ip { get; set; } = "0.0.0.0";
        /// <summary>
        /// Gets or sets the proba.
        /// </summary>
        /// <value>
        /// The proba.
        /// </value>
        public string Probe { get; set; } = "0.0.0.0";
        /// <summary>
        /// Gets or sets the type.
        /// </summary>
        /// <value>
        /// The type.
        /// </value>
        public TaskType Type { get; set; } = TaskType.Scheduler;
        /// <summary>
        /// Gets or sets the repeat.
        /// </summary>
        /// <value>
        /// The repeat.
        /// </value>
        public uint Repeats { get; set; } = 1;
        /// <summary>
        /// Gets or sets the cron.
        /// </summary>
        /// <value>
        /// The cron.
        /// </value>
        public string Cron { get; set; } = "* * * * *";
        /// <summary>
        /// Gets or sets the circles.
        /// </summary>
        /// <value>
        /// The circles.
        /// </value>
        public uint Circles { get; set; } = 1;
        /// <summary>
        /// Gets or sets the pause sec.
        /// </summary>
        /// <value>
        /// The pause sec.
        /// </value>
        public ulong Pause { get; set; } = 1;
        /// <summary>
        /// Индивидуальный таймаут выполнения зонда, в секундах (0 — без ограничения).
        /// Колонка необязательна: [Optional] позволяет загружать CSV без этого столбца.
        /// </summary>
        /// <value>
        /// Таймаут выполнения в секундах (0 — без ограничения).
        /// </value>
        [Optional]
        public int Timeout { get; set; } = 0;
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
        /// Gets or sets the mode.
        /// </summary>
        /// <value>
        /// The mode.
        /// </value>
        public TaskMode Mode { get; set; } = TaskMode.TWamp;
        /// <summary>
        /// Gets or sets the request.
        /// </summary>
        /// <value>
        /// The request.
        /// </value>
        public string? Request { get; set; }


    }
}
