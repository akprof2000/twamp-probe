// Ignore Spelling: SPI Twamp Clickhouse

using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Parser;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты строки результата: соответствие CSV-отчёта и схемы таблицы ClickHouse.
    /// </summary>
    public class ExportRowTests
    {
        /// <summary>Собирает строку из типового ответа зонда.</summary>
        private static ExportRow Build() => ExportRow.Create(
            new ActionData
            {
                ResultId = Guid.NewGuid(),
                Creation = new DateTime(2026, 7, 22, 16, 9, 5, DateTimeKind.Local),
                TaskId = Guid.NewGuid(),
                Mode = "TWampy",
                CallLine = "./twampy -c 1 10.0.0.1",
                EndNode = "10.0.0.1",
                Outcome = "Success",
                ExitCode = 0,
                Console = "вывод"
            },
            new TwPingStats { FromHost = "10.0.0.1", RttMin = 1.5 },
            rowNo: 0,
            title: "задача");

        [Fact(DisplayName = "Первая колонка отчёта — Started в формате dd.MM.yyyy HH.mm.ss")]
        public void Csv_Started_Is_First_Column()
        {
            string[] header = ExportRow.CsvHeader(';').Split(';');
            string[] cells = Build().ToCsvLine(';', ',').Split(';');

            Assert.Equal("Started", header[0]);
            Assert.Equal("22.07.2026 16.09.05", cells[0]);
        }

        [Fact(DisplayName = "Число ячеек строки совпадает с числом колонок заголовка")]
        public void Csv_Cell_Count_Matches_Header()
        {
            Assert.Equal(
                ExportRow.CsvHeader(';').Split(';').Length,
                Build().ToCsvLine(';', ',').Split(';').Length);
        }

        [Fact(DisplayName = "Значения попадают в колонки, заявленные заголовком")]
        public void Csv_Values_Land_In_Declared_Columns()
        {
            string[] header = ExportRow.CsvHeader(';').Split(';');
            string[] cells = Build().ToCsvLine(';', ',').Split(';');

            Assert.Equal("TWampy", cells[Array.IndexOf(header, "Mode")]);
            Assert.Equal("задача", cells[Array.IndexOf(header, "Title")]);
            // Десятичный разделитель — запятая, поэтому значение берётся в кавычки.
            Assert.Equal("\"1,5\"", cells[Array.IndexOf(header, "RttMin")]);
        }

        [Fact(DisplayName = "Сырьё зонда есть в строке, но не в CSV-отчёте")]
        public void Raw_Output_Is_Kept_But_Not_Exported_To_Csv()
        {
            ExportRow row = Build();

            Assert.Equal("вывод", row.Console);      // уедет в ClickHouse
            Assert.Equal("Success", row.Outcome);
            Assert.DoesNotContain("Console", ExportRow.CsvHeader(';').Split(';'));
            Assert.DoesNotContain("Outcome", ExportRow.CsvHeader(';').Split(';'));
        }

        [Fact(DisplayName = "DDL объявляет каждую колонку отчёта и ключ дедупликации")]
        public void Ddl_Declares_Every_Report_Column()
        {
            string ddl = ExportRow.CreateTableDdl("twamp", "results");

            foreach (string column in ExportRow.CsvHeader(';').Split(';'))
            {
                Assert.Contains($"{column} ", ddl);
            }

            // Дедупликация повторно доставленных пачек.
            Assert.Contains("ReplacingMergeTree", ddl);
            Assert.Contains("ORDER BY (Started, ResultId, RowNo)", ddl);
        }
    }
}
