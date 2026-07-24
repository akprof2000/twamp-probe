// Ignore Spelling: SPI Twamp Clickhouse

using SPI.Twamp.Server.Parser;
using System.Globalization;
using System.Text.Json.Serialization;

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Одна строка результата зондирования — единый формат для CSV-отчёта, файлов
    /// буфера (NDJSON) и таблицы ClickHouse. Порядок свойств задаёт порядок колонок.
    /// <para>
    /// Первые поля (до <see cref="Errors"/>) — ровно формат файла экспорта; далее идут
    /// сырые данные ответа зонда и служебные <see cref="ResultId"/>/<see cref="RowNo"/>,
    /// по которым ClickHouse (ReplacingMergeTree) схлопывает повторно доставленные пачки.
    /// </para>
    /// </summary>
    public sealed class ExportRow
    {
        /// <summary>Формат даты-времени: и для CSV, и для ClickHouse (DateTime).</summary>
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>Формат даты-времени запуска в CSV-отчёте (как просили в ТЗ).</summary>
        private const string CsvStartedFormat = "dd.MM.yyyy HH.mm.ss";

        // --- Формат файла экспорта ---

        /// <summary>Дата и время запуска задачи (момент выполнения замера пробой).</summary>
        public string Started { get; set; } = "";
        /// <summary>Название задачи.</summary>
        public string Title { get; set; } = "";
        /// <summary>Идентификатор задачи.</summary>
        public string Id { get; set; } = "";
        /// <summary>Режим зондирования (WinPing / TWamp / TWampy).</summary>
        public string Mode { get; set; } = "";
        /// <summary>Фактическая строка вызова зонда.</summary>
        public string CallLine { get; set; } = "";
        /// <summary>Хост-источник.</summary>
        public string FromHost { get; set; } = "";
        /// <summary>Порт источника.</summary>
        public int? FromPort { get; set; }
        /// <summary>Хост-получатель.</summary>
        public string ToHost { get; set; } = "";
        /// <summary>Порт получателя.</summary>
        public int? ToPort { get; set; }
        /// <summary>Идентификатор сеанса TWAMP.</summary>
        public string SID { get; set; } = "";
        /// <summary>Время первого пакета.</summary>
        public string? First { get; set; }
        /// <summary>Время последнего пакета.</summary>
        public string? Last { get; set; }
        /// <summary>Отправлено пакетов.</summary>
        public int? Sent { get; set; }
        /// <summary>Потеряно пакетов.</summary>
        public int? Lost { get; set; }
        /// <summary>Процент потерь.</summary>
        public double? LossPercent { get; set; }
        /// <summary>Круговая задержка, минимум (мс).</summary>
        public double? RttMin { get; set; }
        /// <summary>Круговая задержка, медиана (мс).</summary>
        public double? RttMedian { get; set; }
        /// <summary>Круговая задержка, максимум (мс).</summary>
        public double? RttMax { get; set; }
        /// <summary>Задержка в прямом направлении, минимум (мс).</summary>
        public double? SendMin { get; set; }
        /// <summary>Задержка в прямом направлении, медиана (мс).</summary>
        public double? SendMedian { get; set; }
        /// <summary>Задержка в прямом направлении, максимум (мс).</summary>
        public double? SendMax { get; set; }
        /// <summary>Задержка в обратном направлении, минимум (мс).</summary>
        public double? ReflectMin { get; set; }
        /// <summary>Задержка в обратном направлении, медиана (мс).</summary>
        public double? ReflectMedian { get; set; }
        /// <summary>Задержка в обратном направлении, максимум (мс).</summary>
        public double? ReflectMax { get; set; }
        /// <summary>Время обработки на отражателе, минимум (мс).</summary>
        public double? ReflectProcMin { get; set; }
        /// <summary>Время обработки на отражателе, максимум (мс).</summary>
        public double? ReflectProcMax { get; set; }
        /// <summary>Двусторонний джиттер (мс).</summary>
        public double? TwoWayJitter { get; set; }
        /// <summary>Джиттер в прямом направлении (мс).</summary>
        public double? SendJitter { get; set; }
        /// <summary>Джиттер в обратном направлении (мс).</summary>
        public double? ReflectJitter { get; set; }
        /// <summary>Число переходов в прямом направлении.</summary>
        public int? SendHops { get; set; }
        /// <summary>Число переходов в обратном направлении.</summary>
        public int? ReflectHops { get; set; }
        /// <summary>Текст ошибок замера.</summary>
        public string Errors { get; set; } = "";

        // --- Сырые данные ответа зонда (в CSV-отчёт не попадают) ---

        /// <summary>Конечный узел (цель зондирования).</summary>
        public string EndNode { get; set; } = "";
        /// <summary>Исход запуска: Success / ExitCodeError / TimedOut / StartFailed.</summary>
        public string Outcome { get; set; } = "";
        /// <summary>Код выхода процесса зонда.</summary>
        public int? ExitCode { get; set; }
        /// <summary>Стандартный вывод зонда.</summary>
        public string Console { get; set; } = "";
        /// <summary>Вывод ошибок зонда.</summary>
        public string ErrorConsole { get; set; } = "";

        // --- Служебные поля дедупликации ---

        /// <summary>Идентификатор результата (присвоен пробой) — ключ дедупликации.</summary>
        public string ResultId { get; set; } = "";
        /// <summary>Номер блока статистики внутри одного результата (0, 1, …).</summary>
        public int RowNo { get; set; }

        /// <summary>
        /// Собирает строку из сырого ответа зонда и разобранной статистики.
        /// </summary>
        /// <param name="action">Сырой результат от пробы.</param>
        /// <param name="stats">Разобранная статистика (один блок).</param>
        /// <param name="rowNo">Номер блока внутри результата.</param>
        /// <param name="title">Название задачи.</param>
        public static ExportRow Create(ActionData action, TwPingStats stats, int rowNo, string title) => new()
        {
            Started = (action.Creation ?? DateTime.Now).ToString(TimeFormat, CultureInfo.InvariantCulture),
            Title = title,
            Id = action.TaskId.ToString(),
            Mode = action.Mode ?? "",
            CallLine = action.CallLine ?? "",
            FromHost = stats.FromHost ?? "",
            FromPort = stats.FromPort,
            ToHost = stats.ToHost ?? "",
            ToPort = stats.ToPort,
            SID = stats.Sid ?? "",
            First = stats.First?.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Last = stats.Last?.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Sent = stats.Sent,
            Lost = stats.Lost,
            LossPercent = stats.LossPercent,
            RttMin = stats.RttMin,
            RttMedian = stats.RttMedian,
            RttMax = stats.RttMax,
            SendMin = stats.SendMin,
            SendMedian = stats.SendMedian,
            SendMax = stats.SendMax,
            ReflectMin = stats.ReflectMin,
            ReflectMedian = stats.ReflectMedian,
            ReflectMax = stats.ReflectMax,
            ReflectProcMin = stats.ReflectProcMin,
            ReflectProcMax = stats.ReflectProcMax,
            TwoWayJitter = stats.TwoWayJitter,
            SendJitter = stats.SendJitter,
            ReflectJitter = stats.ReflectJitter,
            SendHops = stats.SendHops,
            ReflectHops = stats.ReflectHops,
            Errors = stats.Errors ?? "",
            EndNode = action.EndNode ?? "",
            Outcome = action.Outcome ?? "",
            ExitCode = action.ExitCode,
            Console = action.Console ?? "",
            ErrorConsole = action.ErrorConsole ?? "",
            ResultId = action.ResultId.ToString(),
            RowNo = rowNo
        };

        /// <summary>Колонки CSV-отчёта (без сырых данных и служебных полей).</summary>
        private static readonly string[] CsvColumns =
        [
            "Started", "Title", "Id", "Mode", "CallLine",
            "FromHost", "FromPort", "ToHost", "ToPort", "SID", "First", "Last", "Sent", "Lost", "LossPercent",
            "RttMin", "RttMedian", "RttMax", "SendMin", "SendMedian", "SendMax",
            "ReflectMin", "ReflectMedian", "ReflectMax", "ReflectProcMin", "ReflectProcMax",
            "TwoWayJitter", "SendJitter", "ReflectJitter", "SendHops", "ReflectHops", "Errors"
        ];

        /// <summary>Строка заголовка CSV-отчёта.</summary>
        /// <param name="columnSeparator">Разделитель колонок.</param>
        public static string CsvHeader(char columnSeparator) => string.Join(columnSeparator, CsvColumns);

        /// <summary>Формирует строку CSV-отчёта.</summary>
        /// <param name="columnSeparator">Разделитель колонок.</param>
        /// <param name="decimalSeparator">Десятичный разделитель чисел.</param>
        public string ToCsvLine(char columnSeparator, char decimalSeparator) =>
            string.Join(columnSeparator,
            TwPingParser.CsvEscape(FormatStarted()),
            TwPingParser.CsvEscape(Title),
            TwPingParser.CsvEscape(Id),
            TwPingParser.CsvEscape(Mode),
            TwPingParser.CsvEscape(CallLine),
            TwPingParser.CsvEscape(FromHost),
            TwPingParser.CsvEscape(FromPort?.ToString()),
            TwPingParser.CsvEscape(ToHost),
            TwPingParser.CsvEscape(ToPort?.ToString()),
            TwPingParser.CsvEscape(SID),
            TwPingParser.CsvEscape(First),
            TwPingParser.CsvEscape(Last),
            TwPingParser.CsvEscape(Sent?.ToString()),
            TwPingParser.CsvEscape(Lost?.ToString()),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(LossPercent, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(RttMin, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(RttMedian, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(RttMax, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(SendMin, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(SendMedian, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(SendMax, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(ReflectMin, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(ReflectMedian, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(ReflectMax, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(ReflectProcMin, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(ReflectProcMax, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(TwoWayJitter, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(SendJitter, decimalSeparator)),
            TwPingParser.CsvEscape(TwPingParser.FormatNumber(ReflectJitter, decimalSeparator)),
            TwPingParser.CsvEscape(SendHops?.ToString()),
            TwPingParser.CsvEscape(ReflectHops?.ToString()),
            TwPingParser.CsvEscape(Errors));

        /// <summary>Переводит время запуска из формата хранения в формат CSV-отчёта.</summary>
        private string FormatStarted() =>
            DateTime.TryParseExact(Started, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t)
                ? t.ToString(CsvStartedFormat, CultureInfo.InvariantCulture)
                : Started;

        /// <summary>
        /// DDL таблицы ClickHouse. Колонки повторяют формат экспорта, далее — сырые данные
        /// ответа зонда. ReplacingMergeTree по (ResultId, RowNo) схлопывает повторные
        /// доставки одной и той же пачки.
        /// </summary>
        /// <param name="database">Имя базы.</param>
        /// <param name="table">Имя таблицы.</param>
        public static string CreateTableDdl(string database, string table) => $$"""
            CREATE TABLE IF NOT EXISTS `{{database}}`.`{{table}}`
            (
                Started DateTime,
                Title String,
                Id String,
                Mode String,
                CallLine String,
                FromHost String,
                FromPort Nullable(Int32),
                ToHost String,
                ToPort Nullable(Int32),
                SID String,
                First Nullable(DateTime),
                Last Nullable(DateTime),
                Sent Nullable(Int32),
                Lost Nullable(Int32),
                LossPercent Nullable(Float64),
                RttMin Nullable(Float64),
                RttMedian Nullable(Float64),
                RttMax Nullable(Float64),
                SendMin Nullable(Float64),
                SendMedian Nullable(Float64),
                SendMax Nullable(Float64),
                ReflectMin Nullable(Float64),
                ReflectMedian Nullable(Float64),
                ReflectMax Nullable(Float64),
                ReflectProcMin Nullable(Float64),
                ReflectProcMax Nullable(Float64),
                TwoWayJitter Nullable(Float64),
                SendJitter Nullable(Float64),
                ReflectJitter Nullable(Float64),
                SendHops Nullable(Int32),
                ReflectHops Nullable(Int32),
                Errors String,
                EndNode String,
                Outcome String,
                ExitCode Nullable(Int32),
                Console String,
                ErrorConsole String,
                ResultId String,
                RowNo Int32
            )
            ENGINE = ReplacingMergeTree
            PARTITION BY toYYYYMM(Started)
            ORDER BY (Started, ResultId, RowNo)
            """;
    }
}
