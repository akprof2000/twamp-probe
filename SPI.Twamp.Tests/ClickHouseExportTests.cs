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
                }

                context.Response.StatusCode = _statusCode;
                context.Response.Close();
            }
        }

        /// <summary>Собирает буфер и писателя, настроенные на заглушку.</summary>
        private (ResultSpool Spool, ClickHouseWriter Writer, IConfiguration Config) CreateStack()
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
