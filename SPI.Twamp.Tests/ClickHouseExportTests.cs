// Ignore Spelling: SPI Twamp Clickhouse ndjson

using Microsoft.Extensions.Configuration;
using NLog;
using SPI.Twamp.Server.BackgroundServices;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;
using System.Net;
using System.Text;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Проверка выгрузки в ClickHouse на HTTP-заглушке: запросы уходят по протоколу
    /// базы, сегмент отправляется целиком и удаляется только после успешного ответа,
    /// а при ошибке базы — остаётся в очереди.
    /// </summary>
    public class ClickHouseExportTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "twamp-ch-" + Guid.NewGuid().ToString("N"));

        private readonly HttpListener _listener = new();
        private readonly List<(string Query, string Body)> _received = [];
        private readonly int _port;

        /// <summary>Код ответа заглушки — меняется тестом, чтобы сыграть недоступность базы.</summary>
        private int _statusCode = 200;
        private string _responseBody = "";
        private readonly List<string> _insertPaths = [];
        private volatile bool _failPrimaryInsert;

        private sealed class TestClock : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        /// <summary>Поднимает заглушку ClickHouse на свободном порту.</summary>
        public ClickHouseExportTests()
        {
            _ = Directory.CreateDirectory(_directory);
            _port = GetFreePort();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        /// <summary>Занимает и сразу освобождает порт, чтобы узнать свободный номер.</summary>
        private static int GetFreePort()
        {
            System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>Принимает запросы и записывает их для проверок.</summary>
        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // заглушка остановлена
                }

                using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                lock (_received)
                {
                    _received.Add((context.Request.Url?.Query ?? "", body));
                    if (context.Request.QueryString["query"]?.StartsWith("INSERT", StringComparison.Ordinal) == true)
                        _insertPaths.Add(context.Request.Url!.AbsolutePath);
                }

                context.Response.StatusCode = _failPrimaryInsert && context.Request.Url!.AbsolutePath == "/primary/" &&
                    context.Request.QueryString["query"] != null ? 503 : _statusCode;
                context.Response.Close(Encoding.UTF8.GetBytes(_responseBody), false);
            }
        }

        [Fact]
        public async Task InsertSwitchesToReserveAndReturnsToPrimaryAfterRecovery()
        {
            _failPrimaryInsert = true;
            var primary = $"http://localhost:{_port}/primary";
            var reserve = $"http://localhost:{_port}/reserve";
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClickHouse:Urls:0"] = primary,
                ["ClickHouse:Urls:1"] = reserve
            }).Build();
            var clock = new TestClock();
            using var writer = new ClickHouseWriter(LogManager.GetCurrentClassLogger(), config, clock);
            string path = Path.Combine(_directory, "test.ndjson");
            var ct = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(path, "{}\n", ct);
            await writer.EnsureTableAsync(ct);
            Assert.Equal(primary, writer.Url);
            await writer.InsertSegmentAsync(path, ct);
            await writer.InsertSegmentAsync(path, ct);
            Assert.Equal(reserve, writer.Url);
            _failPrimaryInsert = false;
            clock.Now += TimeSpan.FromSeconds(30);
            await writer.EnsureTableAsync(ct);
            Assert.Equal(primary, writer.Url);
            await writer.InsertSegmentAsync(path, ct);
            Assert.Equal(primary, writer.Url);
            lock (_received)
                Assert.Equal(new[] { "/primary/", "/reserve/", "/reserve/", "/primary/" }, _insertPaths.ToArray());
        }

        /// <summary>Собирает буфер и писателя, настроенные на заглушку.</summary>
        private (ResultSpool Spool, ClickHouseWriter Writer, IConfiguration Config) CreateStack(bool useFailover = false)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ClickHouse:Enabled"] = "true",
                    ["ClickHouse:Url"] = $"http://localhost:{_port}",
                    ["ClickHouse:Database"] = "twamp",
                    ["ClickHouse:Table"] = "results",
                    ["ClickHouse:SpoolPath"] = _directory,
                    ["ClickHouse:BatchRows"] = "2",
                    ["ClickHouse:MaxSegments"] = "16",
                    ["ClickHouse:RetrySeconds"] = "1"
                })
                .Build();

            if (useFailover)
            {
                configuration["ClickHouse:Urls:0"] = $"http://localhost:{_port}/primary";
                configuration["ClickHouse:Urls:1"] = $"http://localhost:{_port}/reserve";
            }

            Logger logger = LogManager.GetCurrentClassLogger();
            return (new ResultSpool(logger, configuration), new ClickHouseWriter(logger, configuration), configuration);
        }

        /// <summary>Две строки результата — ровно предел сегмента в тестовой настройке.</summary>
        private static ExportRow[] TwoRows() =>
        [
            new() { Started = "2026-07-24 10:00:00", Title = "первая", ResultId = Guid.NewGuid().ToString() },
            new() { Started = "2026-07-24 10:00:01", Title = "вторая", ResultId = Guid.NewGuid().ToString() }
        ];

        /// <summary>Ждёт выполнения условия (заглушка работает в другом потоке).</summary>
        private static async Task<bool> WaitForAsync(Func<bool> condition, int seconds = 10)
        {
            for (int i = 0; i < seconds * 20; i++)
            {
                if (condition())
                {
                    return true;
                }
                await Task.Delay(50);
            }
            return condition();
        }

        [Fact(DisplayName = "Сегмент уходит в ClickHouse как JSONEachRow и удаляется после ответа")]
        public async Task Segment_Is_Sent_And_Deleted()
        {
            (ResultSpool spool, ClickHouseWriter writer, IConfiguration config) = CreateStack();
            using ResultSpool spoolScope = spool;
            using ClickHouseWriter writerScope = writer;

            await spool.AppendAsync(TwoRows(), TestContext.Current.CancellationToken);
            Assert.Equal(1, spool.SealedCount);

            ClickHouseExportService service = new(LogManager.GetCurrentClassLogger(), spool, writer, config);
            await service.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(await WaitForAsync(() => spool.SealedCount == 0), "сегмент не был выгружен");
            await service.StopAsync(TestContext.Current.CancellationToken);

            (string Query, string Body)[] requests;
            lock (_received)
            {
                requests = [.. _received];
            }

            // Схема создаётся до вставки.
            Assert.Contains(requests, r => r.Body.Contains("CREATE DATABASE IF NOT EXISTS"));
            Assert.Contains(requests, r => r.Body.Contains("ReplacingMergeTree"));

            // Вставка идёт запросом в параметре query, данными — тело NDJSON.
            (string Query, string Body) insert = Assert.Single(requests, r => r.Query.Contains("INSERT+INTO") ||
                                                                              r.Query.Contains("INSERT%20INTO"));
            string[] lines = insert.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Contains("FORMAT+JSONEachRow", insert.Query.Replace("%20", "+"));

            // Каждая строка тела — самостоятельный JSON-объект с колонками таблицы,
            // кириллица не поломана экранированием.
            ExportRow? first = System.Text.Json.JsonSerializer.Deserialize<ExportRow>(lines[0], SpoolJson.Options);
            Assert.Equal("первая", first?.Title);

            // Файл сегмента удалён с диска.
            Assert.Empty(Directory.GetFiles(_directory, "seg-*.ndjson"));
        }

        [Theory]
        [InlineData(503, "unavailable")]
        [InlineData(200, "Code: 241. DB::Exception: insert failed")]
        [InlineData(202, "")]
        public async Task UncommittedBatchSurvivesRestart(int status, string body)
        {
            var ct = TestContext.Current.CancellationToken;
            (var spool, var writer, var config) = CreateStack(useFailover: true);
            using var spoolScope = spool;
            using var writerScope = writer;
            const string probe = "http://localhost:8443";
            var batch = new ProbeResultBatch { BatchId = Guid.NewGuid(), Items = [
                new ActionData { ResultId = Guid.NewGuid(), TaskId = Guid.NewGuid(), Mode = "TWampy", Console = "raw" }
            ] };
            string path;
            using (var buffer = new SPI.Twamp.Collector.DeliveryBuffer(_directory))
            {
                path = await buffer.StageAsync(probe, batch, new Dictionary<Guid,string>(), ct);
                await writer.EnsureTableAsync(ct);
                _statusCode = status;
                _responseBody = body;
                var error = await Assert.ThrowsAsync<HttpRequestException>(
                    () => SPI.Twamp.Collector.DeliveryBuffer.StoreAsync(path, writer, ct));
                Assert.Contains(config["ClickHouse:Urls:0"]!, error.Message);
                Assert.Contains(config["ClickHouse:Urls:1"]!, error.Message);
            }
            using var recovered = new SPI.Twamp.Collector.DeliveryBuffer(_directory);
            Assert.Equal(path, recovered.Peek(probe));
            Assert.Equal(0, recovered.AwaitingConfirmation);
            Assert.Equal(1, SPI.Twamp.Collector.DeliveryBuffer.RowCount(path));
            var row = System.Text.Json.JsonSerializer.Deserialize<ExportRow>(await File.ReadAllTextAsync(path, ct));
            Assert.Equal("raw", row!.Console);
        }

        [Fact(DisplayName = "При ошибке базы сегмент остаётся в очереди и не теряется")]
        public async Task Segment_Survives_Database_Failure()
        {
            _statusCode = 500; // база «сломана»

            (ResultSpool spool, ClickHouseWriter writer, IConfiguration config) = CreateStack();
            using ResultSpool spoolScope = spool;
            using ClickHouseWriter writerScope = writer;

            await spool.AppendAsync(TwoRows(), TestContext.Current.CancellationToken);

            ClickHouseExportService service = new(LogManager.GetCurrentClassLogger(), spool, writer, config);
            await service.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(await WaitForAsync(() => _received.Count > 0), "запрос до заглушки не дошёл");
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await service.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, spool.SealedCount); // сегмент на месте — уедет после восстановления
            _ = Assert.Single(Directory.GetFiles(_directory, "seg-*.ndjson"));
        }

        /// <summary>Останавливает заглушку и убирает временный каталог.</summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            _listener.Stop();
            _listener.Close();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
