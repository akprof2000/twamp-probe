// Ignore Spelling: SPI Twamp Clickhouse ndjson

using Microsoft.Extensions.Configuration;
using NLog;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты буфера результатов: ротация сегментов по числу строк, переполнение
    /// (обратное давление), чтение для отчёта и восстановление после перезапуска.
    /// </summary>
    public class ResultSpoolTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "twamp-spool-" + Guid.NewGuid().ToString("N"));

        /// <summary>Создаёт буфер с заданными пределами.</summary>
        private ResultSpool CreateSpool(int batchRows, int maxSegments, int flushMinutes = 10)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ClickHouse:SpoolPath"] = _directory,
                    ["ClickHouse:BatchRows"] = batchRows.ToString(),
                    ["ClickHouse:MaxSegments"] = maxSegments.ToString(),
                    ["ClickHouse:FlushMinutes"] = flushMinutes.ToString()
                })
                .Build();

            return new ResultSpool(LogManager.GetCurrentClassLogger(), configuration);
        }

        /// <summary>Строки-пустышки для наполнения буфера.</summary>
        private static ExportRow[] Rows(int count) =>
            [.. Enumerable.Range(0, count).Select(i => new ExportRow
            {
                Started = "2026-07-24 10:00:00",
                Title = "задача " + i,   // кириллица — проверяем кодировку
                Id = Guid.Empty.ToString(),
                ResultId = Guid.NewGuid().ToString(),
                RowNo = 0,
                RttMin = i
            })];

        [Fact(DisplayName = "Сегмент запечатывается при достижении предела строк")]
        public async Task Seals_On_Row_Limit()
        {
            using ResultSpool spool = CreateSpool(batchRows: 10, maxSegments: 16);

            await spool.AppendAsync(Rows(4), TestContext.Current.CancellationToken);
            Assert.Equal(0, spool.SealedCount);
            Assert.Equal(4, spool.CurrentRows);

            await spool.AppendAsync(Rows(6), TestContext.Current.CancellationToken);

            Assert.Equal(1, spool.SealedCount);   // 10 строк — сегмент закрыт
            Assert.Equal(0, spool.CurrentRows);   // и начат новый
            _ = Assert.Single(spool.GetSealedSegments());
        }

        [Fact(DisplayName = "Набрав предел сегментов, буфер сообщает о переполнении")]
        public async Task Reports_Full_At_Segment_Limit()
        {
            using ResultSpool spool = CreateSpool(batchRows: 2, maxSegments: 3);

            for (int i = 0; i < 2; i++)
            {
                await spool.AppendAsync(Rows(2), TestContext.Current.CancellationToken);
            }
            Assert.False(spool.IsFull);

            await spool.AppendAsync(Rows(2), TestContext.Current.CancellationToken);

            Assert.True(spool.IsFull);            // опрос проб должен приостановиться
            Assert.Equal(3, spool.SealedCount);
        }

        [Fact(DisplayName = "Удаление выгруженного сегмента освобождает буфер")]
        public async Task Delete_Frees_Buffer()
        {
            using ResultSpool spool = CreateSpool(batchRows: 2, maxSegments: 1);
            await spool.AppendAsync(Rows(2), TestContext.Current.CancellationToken);
            Assert.True(spool.IsFull);

            spool.DeleteSegment(spool.GetSealedSegments()[0].Path);

            Assert.False(spool.IsFull);
            Assert.Equal(0, spool.SealedCount);
        }

        [Fact(DisplayName = "Отчёт читает и запечатанные сегменты, и заполняемый")]
        public async Task Reads_Sealed_And_Current()
        {
            using ResultSpool spool = CreateSpool(batchRows: 3, maxSegments: 16);
            await spool.AppendAsync(Rows(3), TestContext.Current.CancellationToken); // запечатан
            await spool.AppendAsync(Rows(2), TestContext.Current.CancellationToken); // текущий

            List<ExportRow> read = [];
            await foreach (ExportRow row in spool.ReadPendingAsync(TestContext.Current.CancellationToken))
            {
                read.Add(row);
            }

            Assert.Equal(5, read.Count);
            Assert.All(read, row => Assert.StartsWith("задача", row.Title)); // кириллица уцелела
        }

        [Fact(DisplayName = "После перезапуска недописанный сегмент запечатывается и не теряется")]
        public async Task Recovers_Current_Segment_After_Restart()
        {
            using (ResultSpool first = CreateSpool(batchRows: 1000, maxSegments: 16))
            {
                await first.AppendAsync(Rows(5), TestContext.Current.CancellationToken);
                Assert.Equal(0, first.SealedCount); // до предела далеко — остался текущим
            }

            using ResultSpool restarted = CreateSpool(batchRows: 1000, maxSegments: 16);

            Assert.Equal(1, restarted.SealedCount); // подхвачен и поставлен в очередь

            List<ExportRow> read = [];
            await foreach (ExportRow row in restarted.ReadPendingAsync(TestContext.Current.CancellationToken))
            {
                read.Add(row);
            }
            Assert.Equal(5, read.Count);
        }

        [Fact(DisplayName = "Строка сегмента — валидный JSON в одну строку (формат JSONEachRow)")]
        public async Task Segment_Line_Is_Single_Line_Json()
        {
            using ResultSpool spool = CreateSpool(batchRows: 1, maxSegments: 16);

            ExportRow row = Rows(1)[0];
            row.Console = "многострочный\nвывод\r\nзонда"; // переносы обязаны быть экранированы
            await spool.AppendAsync([row], TestContext.Current.CancellationToken);

            string[] lines = await File.ReadAllLinesAsync(
                spool.GetSealedSegments()[0].Path, TestContext.Current.CancellationToken);

            _ = Assert.Single(lines);
            ExportRow? parsed = System.Text.Json.JsonSerializer.Deserialize<ExportRow>(lines[0], SpoolJson.Options);
            Assert.NotNull(parsed);
            Assert.Equal("многострочный\nвывод\r\nзонда", parsed.Console);
        }

        /// <summary>Убирает временный каталог буфера.</summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
