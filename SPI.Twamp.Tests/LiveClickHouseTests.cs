// Ignore Spelling: SPI Twamp Clickhouse ndjson

using Microsoft.Extensions.Configuration;
using NLog;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;
using SPI.Twamp.Server.Parser;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Проверка против настоящего ClickHouse: DDL, вставка JSONEachRow, обратное чтение
    /// и схлопывание повторно доставленной пачки.
    /// <para>
    /// Тест выполняется, только если задана переменная окружения <c>TWAMP_CLICKHOUSE_URL</c>
    /// (и при необходимости <c>TWAMP_CLICKHOUSE_PASSWORD</c>) — иначе пропускается, чтобы
    /// сборка без базы оставалась зелёной. Поднять базу для прогона:
    /// <code>
    /// docker run -d --name twamp-ch -p 18123:8123 -e CLICKHOUSE_PASSWORD=twamp clickhouse/clickhouse-server
    /// set TWAMP_CLICKHOUSE_URL=http://localhost:18123
    /// set TWAMP_CLICKHOUSE_PASSWORD=twamp
    /// </code>
    /// </para>
    /// </summary>
    public class LiveClickHouseTests
    {
        /// <summary>Адрес базы для прогона (пусто — тест пропускается).</summary>
        private static readonly string? Url = System.Environment.GetEnvironmentVariable("TWAMP_CLICKHOUSE_URL");

        /// <summary>Пароль пользователя default.</summary>
        private static readonly string Password =
            System.Environment.GetEnvironmentVariable("TWAMP_CLICKHOUSE_PASSWORD") ?? "";

        private static readonly string TwPingOutput = """
            --- twping statistics from [10.0.0.1]:8888 to [10.0.0.2]:9999 ---
            SID: abcdef0123456789
            first: 2026-07-08T10:00:00.0
            last: 2026-07-08T10:05:00.0
            300 sent, 3 lost (1.000%)
            round-trip time min/median/max = 1.5/2.0/9.9 ms
            send time min/median/max = 0.7/1.0/5.0 ms
            reflect time min/median/max = 0.6/0.9/4.4 ms
            reflector processing time min/max = 0.01/0.05 ms
            two-way jitter = 0.8 ms
            send hops = 7
            """;

        private static IConfiguration Config(string dir, string table) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClickHouse:Enabled"] = "true",
                ["ClickHouse:Url"] = Url,
                ["ClickHouse:Database"] = "twamp",
                ["ClickHouse:Table"] = table,
                ["ClickHouse:User"] = "default",
                ["ClickHouse:Password"] = Password,
                ["ClickHouse:SpoolPath"] = dir,
                ["ClickHouse:BatchRows"] = "1000",
                ["ClickHouse:MaxSegments"] = "16"
            }).Build();

        private static async Task<string> QueryAsync(string sql)
        {
            using HttpClient http = new();
            http.DefaultRequestHeaders.Add("X-ClickHouse-User", "default");
            http.DefaultRequestHeaders.Add("X-ClickHouse-Key", Password);
            using StringContent body = new(sql);
            HttpResponseMessage response = await http.PostAsync(Url + "/", body);
            string text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            return text.Trim();
        }

        [Fact(DisplayName = "LIVE: реальный ClickHouse принимает DDL, вставку и схлопывает дубли")]
        public async Task Live_Roundtrip()
        {
            Assert.SkipWhen(string.IsNullOrEmpty(Url), "TWAMP_CLICKHOUSE_URL не задан — живой ClickHouse не проверяется");

            string table = "results_live";
            string dir = Path.Combine(Path.GetTempPath(), "twamp-live-" + Guid.NewGuid().ToString("N"));
            Logger logger = LogManager.GetCurrentClassLogger();

            _ = await QueryAsync($"DROP TABLE IF EXISTS `twamp`.`{table}`");

            IConfiguration config = Config(dir, table);
            using ResultSpool spool = new(logger, config);
            using ClickHouseWriter writer = new(logger, config);

            // 1. Настоящий вывод twping → строки формата экспорта.
            ActionData action = new()
            {
                ResultId = Guid.NewGuid(),
                Creation = new DateTime(2026, 7, 22, 16, 9, 5, DateTimeKind.Local),
                TaskId = Guid.NewGuid(),
                Mode = "TWamp",
                CallLine = "twping 10.0.0.2",
                EndNode = "10.0.0.2",
                Outcome = "Success",
                ExitCode = 0,
                Console = "многострочный\nвывод «зонда»"
            };

            List<ExportRow> rows = [];
            int rowNo = 0;
            foreach (TwPingStats parsed in ProbeOutputParser.Parse(action.Mode, TwPingOutput, "", action.TaskId))
            {
                rows.Add(ExportRow.Create(action, parsed, rowNo++, "задача №1"));
            }
            Assert.NotEmpty(rows);

            // 2. Буфер → запечатать → отправить.
            await spool.AppendAsync(rows, TestContext.Current.CancellationToken);
            _ = await spool.SealIfDueAsync(); // срок не вышел, поэтому запечатаем принудительно ниже
            await writer.EnsureTableAsync(TestContext.Current.CancellationToken);

            // Принудительное запечатывание: перезапуск буфера закрывает текущий сегмент.
            spool.Dispose();
            using ResultSpool reopened = new(logger, config);
            string segment = Assert.Single(reopened.GetSealedSegments()).Path;

            await writer.InsertSegmentAsync(segment, TestContext.Current.CancellationToken);

            // 3. Читаем обратно — типы и значения доехали.
            Assert.Equal(rows.Count.ToString(), await QueryAsync($"SELECT count() FROM `twamp`.`{table}`"));
            Assert.Equal("2026-07-22 16:09:05", await QueryAsync($"SELECT toString(Started) FROM `twamp`.`{table}` LIMIT 1"));
            Assert.Equal("задача №1", await QueryAsync($"SELECT Title FROM `twamp`.`{table}` LIMIT 1"));
            Assert.Equal("1.5", await QueryAsync($"SELECT toString(RttMin) FROM `twamp`.`{table}` LIMIT 1"));
            Assert.Equal("300", await QueryAsync($"SELECT toString(Sent) FROM `twamp`.`{table}` LIMIT 1"));
            Assert.Equal("10.0.0.2", await QueryAsync($"SELECT ToHost FROM `twamp`.`{table}` LIMIT 1"));
            Assert.Contains("зонда", await QueryAsync($"SELECT Console FROM `twamp`.`{table}` LIMIT 1"));

            // 4. Повторная доставка той же пачки — дубли схлопываются.
            await writer.InsertSegmentAsync(segment, TestContext.Current.CancellationToken);
            Assert.Equal((rows.Count * 2).ToString(), await QueryAsync($"SELECT count() FROM `twamp`.`{table}`"));
            Assert.Equal(rows.Count.ToString(), await QueryAsync($"SELECT count() FROM `twamp`.`{table}` FINAL"));

            _ = await QueryAsync($"DROP TABLE `twamp`.`{table}`");
            Directory.Delete(dir, recursive: true);
        }
    }
}
