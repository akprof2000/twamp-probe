// Ignore Spelling: SPI Twamp

using CsvHelper.Configuration.Attributes;
using LiteDB;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Шаблон задачи зондирования, загружаемый из CSV-файла шаблонов.
    /// <para>
    /// CSV — с заголовками, разделитель «;», порядок колонок любой (маппинг по имени
    /// заголовка), большинство колонок необязательны. Пример строки:
    /// <c>d46;http://100.123.20.60:443;-c 300 -i 1 -D 46;Scheduler;1;1;0;*/6 * * * *;;2 week 3 day 2 hour;TWamp;10</c>.
    /// </para>
    /// <para>
    /// Поля <see cref="Start"/> и <see cref="End"/> задаются либо абсолютной датой,
    /// либо относительной длительностью от момента создания задач
    /// («2 week 3 day 2 hour», поддерживаются week/day/hour/min и опечатка «weak»).
    /// </para>
    /// </summary>
    public class ProbeTemplate
    {
        /// <summary>
        /// Идентификатор записи в БД (не является колонкой CSV).
        /// </summary>
        [Ignore]
        [BsonId]
        public ObjectId? Id { get; set; }

        /// <summary>
        /// Имя набора шаблонов (не колонка CSV — задаётся при загрузке файла,
        /// по умолчанию — имя файла). Наборы загружаются, применяются к файлам
        /// маршрутизаторов и удаляются независимо друг от друга.
        /// </summary>
        [Ignore]
        public string SetName { get; set; } = "";

        /// <summary>
        /// Название шаблона — попадает в имя создаваемой задачи («устройство-шаблон»).
        /// </summary>
        [Optional]
        public string Name { get; set; } = "";

        /// <summary>
        /// Адрес пробы, которая будет выполнять задачи этого шаблона (обязательное поле).
        /// </summary>
        public string Probe { get; set; } = "";

        /// <summary>
        /// Аргументы командной строки зонда (адрес маршрутизатора подставляет проба).
        /// </summary>
        [Optional]
        public string? Request { get; set; }

        /// <summary>
        /// Тип задачи (Scheduler или Repeater). По умолчанию — Scheduler.
        /// </summary>
        [Optional]
        public TaskType Type { get; set; } = TaskType.Scheduler;

        /// <summary>
        /// Количество повторов зонда внутри цикла.
        /// </summary>
        [Optional]
        public uint Repeats { get; set; } = 1;

        /// <summary>
        /// Количество циклов замера.
        /// </summary>
        [Optional]
        public uint Circles { get; set; } = 1;

        /// <summary>
        /// Пауза между циклами, в секундах.
        /// </summary>
        [Optional]
        public ulong Pause { get; set; } = 0;

        /// <summary>
        /// Cron-выражение расписания.
        /// </summary>
        [Optional]
        public string Cron { get; set; } = "*/1 * * * *";

        /// <summary>
        /// Начало действия: дата либо длительность от момента создания. Пусто — сразу.
        /// </summary>
        [Optional]
        public string? Start { get; set; }

        /// <summary>
        /// Окончание действия: дата либо длительность от момента создания («2 week 3 day»).
        /// </summary>
        [Optional]
        public string? End { get; set; }

        /// <summary>
        /// Режим зондирования (TWamp или WinPing). По умолчанию — TWamp.
        /// </summary>
        [Optional]
        public TaskMode Mode { get; set; } = TaskMode.TWamp;

        /// <summary>
        /// Индивидуальный таймаут задачи, в секундах (0 — без ограничения).
        /// Заголовок в CSV — «TimeOut» или «Timeout».
        /// </summary>
        [Optional]
        [Name("TimeOut", "Timeout")]
        public int Timeout { get; set; } = 0;
    }
}
