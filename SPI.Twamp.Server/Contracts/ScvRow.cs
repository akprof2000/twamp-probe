// Ignore Spelling: SPI Twamp

using CsvHelper.Configuration.Attributes;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Строка CSV-файла импорта задач. Соответствует одной задаче зондирования.
    /// </summary>
    public class CsvRow
    {
        /// <summary>
        /// Внешний идентификатор (NRI) — не используется логикой, только для справки.
        /// </summary>
        public ulong? NriId { get; set; }
        /// <summary>
        /// Имя (метка) задачи из файла.
        /// </summary>
        public string? Name { get; set; }
        /// <summary>
        /// Имя хоста конечного узла.
        /// </summary>
        public string? HostName { get; set; }
        /// <summary>
        /// Адрес конечного узла (куда направлен зонд).
        /// </summary>
        public string Ip { get; set; } = "0.0.0.0";
        /// <summary>
        /// Адрес пробы, которая должна выполнять задачу.
        /// </summary>
        public string Probe { get; set; } = "0.0.0.0";
        /// <summary>
        /// Тип задачи (разовая или по расписанию).
        /// </summary>
        public TaskType Type { get; set; } = TaskType.Scheduler;
        /// <summary>
        /// Количество повторов зонда внутри цикла.
        /// </summary>
        public uint Repeats { get; set; } = 1;
        /// <summary>
        /// Cron-выражение расписания.
        /// </summary>
        public string Cron { get; set; } = "* * * * *";
        /// <summary>
        /// Количество циклов замера.
        /// </summary>
        public uint Circles { get; set; } = 1;
        /// <summary>
        /// Пауза между циклами, в секундах.
        /// </summary>
        public ulong Pause { get; set; } = 1;
        /// <summary>
        /// Индивидуальный таймаут выполнения зонда, в секундах (0 — без ограничения).
        /// Колонка необязательна: [Optional] позволяет загружать CSV без этого столбца.
        /// </summary>
        [Optional]
        public int Timeout { get; set; } = 0;
        /// <summary>
        /// Дата начала действия задачи.
        /// </summary>
        public DateTime Start { get; set; }
        /// <summary>
        /// Дата окончания действия задачи.
        /// </summary>
        public DateTime End { get; set; }
        /// <summary>
        /// Режим зондирования (WinPing / TWamp / TWampy).
        /// </summary>
        public TaskMode Mode { get; set; } = TaskMode.TWamp;
        /// <summary>
        /// Дополнительные аргументы командной строки зонда.
        /// </summary>
        public string? Request { get; set; }
    }
}
