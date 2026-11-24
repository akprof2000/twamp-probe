// Ignore Spelling: SPI Twamp Clickhouse ndjson

using NLog;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using System.Net.Http.Headers;
using System.Text;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="IClickHouseWriter"/> поверх HTTP-интерфейса ClickHouse.
    /// <para>
    /// Внешний драйвер не нужен: база принимает запрос в параметре <c>query</c>, а данные —
    /// телом запроса. Файл сегмента уже лежит в формате <c>JSONEachRow</c>, поэтому
    /// отправляется потоком без чтения в память — вставка 100 000 строк не зависит
    /// от объёма ОЗУ сервера.
    /// </para>
    /// </summary>
    public sealed class ClickHouseWriter : IClickHouseWriter, IDisposable
    {
        /// <summary>Настройки вставки: не падать на незнакомых полях и понимать любые формы дат.</summary>
        private const string InsertSettings =
            "&async_insert=0&wait_end_of_query=1&date_time_input_format=best_effort";

        private readonly Logger _logger;
        private readonly HttpClient _http;
        private readonly Endpoint[] _endpoints;
        private readonly TimeProvider _clock;
        private readonly TimeSpan _retryInterval;
        private string _activeUrl;

        private sealed class Endpoint(string url)
        {
            public string Url { get; } = url;
            public SemaphoreSlim SchemaGate { get; } = new(1, 1);
            public volatile bool SchemaReady;
            public long RetryAtTicks;
        }
        private readonly string _database;
        private readonly string _table;
        private readonly bool _ha;
        private readonly string _replicaPath;

        /// <summary>Создаёт писателя по конфигурации (секция <c>ClickHouse</c>).</summary>
        /// <param name="logger">Логгер.</param>
        /// <param name="configuration">Конфигурация приложения.</param>
        /// <param name="clock">Часы для отсчёта паузы после отказа адреса.</param>
        public ClickHouseWriter(Logger logger, IConfiguration configuration, TimeProvider? clock = null)
        {
            _logger = logger;
            Enabled = configuration["ClickHouse:Enabled"].ConvertTo(true);
            _clock = clock ?? TimeProvider.System;
            _retryInterval = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("ClickHouse:FailoverRetrySeconds", 30)));
            var urls = configuration.GetSection("ClickHouse:Urls").GetChildren().Select(c => c.Value).ToArray();
            if (urls.Length == 0)
                urls = [configuration["ClickHouse:Url"] ?? "http://localhost:8123"];
            _endpoints = urls.Select(ValidateAddress).Distinct(StringComparer.Ordinal)
                .Select(url => new Endpoint(url)).ToArray();
            _activeUrl = _endpoints[0].Url;
            _database = configuration["ClickHouse:Database"] ?? "twamp";
            _table = configuration["ClickHouse:Table"] ?? "results";
            _ha = configuration.GetValue("HighAvailability:Enabled", false);
            if (_ha && (!System.Text.RegularExpressions.Regex.IsMatch(_database, @"\A[A-Za-z0-9_]+\z") ||
                !System.Text.RegularExpressions.Regex.IsMatch(_table, @"\A[A-Za-z0-9_]+\z")))
                throw new InvalidOperationException("Недопустимое имя базы или таблицы ClickHouse.");
            _replicaPath = $"/clickhouse/twamp/{_database}/{_table}/01";

            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                // Вставка большого сегмента может идти долго — таймаут щедрый.
                Timeout = TimeSpan.FromSeconds(configuration["ClickHouse:TimeoutSec"].ConvertTo(300))
            };

            string user = configuration["ClickHouse:User"] ?? "default";
            string password = configuration["ClickHouse:Password"] ?? "";
            _http.DefaultRequestHeaders.Add("X-ClickHouse-User", user);
            if (password.Length > 0)
            {
                _http.DefaultRequestHeaders.Add("X-ClickHouse-Key", password);
            }
        }

        /// <inheritdoc/>
        public bool Enabled { get; }

        /// <inheritdoc/>
        public string Url => Volatile.Read(ref _activeUrl);

        /// <inheritdoc/>
        public string Target => $"{_database}.{_table}";

        /// <inheritdoc/>
        public async Task EnsureTableAsync(CancellationToken cancellationToken)
        {
            if (!_ha) { await WithFailoverAsync(EnsureEndpointAsync, cancellationToken); return; }
            // Bootstrap all reachable replicas before the first quorum INSERT.
            await Task.WhenAll(_endpoints.Select(async ep =>
            {
                try { await EnsureEndpointAsync(ep,cancellationToken); }
                catch(Exception ex) when(!cancellationToken.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
                { _logger.Warn("Реплика {Url}: {Reason}",ep.Url,ex.Message); }
            }));
            if (!_endpoints.Any(ep=>ep.SchemaReady)) throw new HttpRequestException("Нет доступной реплицируемой таблицы TWamp.");
        }

        /// <inheritdoc/>
        public Task InsertSegmentAsync(string path, CancellationToken cancellationToken) =>
            WithFailoverAsync(async (endpoint, ct) =>
            {
                await EnsureEndpointAsync(endpoint, ct);
                string query = Uri.EscapeDataString($"INSERT INTO `{_database}`.`{_table}` FORMAT JSONEachRow");
                using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using StreamContent content = new(file);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
                var quorum = _ha ? "&insert_quorum=2" : "";
                if (_ha && Guid.TryParseExact(Path.GetFileName(path).Split('.')[0],"N",out var batch))
                    quorum += "&insert_deduplication_token="+batch.ToString("N");
                using var response = await _http.PostAsync($"{endpoint.Url}/?query={query}{InsertSettings}{quorum}", content, ct);
                await EnsureSuccessAsync(response, ct);
            }, cancellationToken);

        private async Task WithFailoverAsync(Func<Endpoint, CancellationToken, Task> operation, CancellationToken ct)
        {
            long now = _clock.GetUtcNow().UtcTicks;
            // After a cooldown, the next operation tries the primary first again.
            var candidates = _endpoints.OrderBy(ep => Interlocked.Read(ref ep.RetryAtTicks) > now).ToArray();
            List<Exception> failures = [];
            foreach (var endpoint in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await operation(endpoint, ct);
                    Interlocked.Exchange(ref endpoint.RetryAtTicks, 0);
                    Volatile.Write(ref _activeUrl, endpoint.Url);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested &&
                    ex is (HttpRequestException or OperationCanceledException or System.Net.Http.HttpIOException))
                {
                    endpoint.SchemaReady = false;
                    Interlocked.Exchange(ref endpoint.RetryAtTicks, (_clock.GetUtcNow() + _retryInterval).UtcTicks);
                    failures.Add(new HttpRequestException($"{endpoint.Url}: {ex.Message}", ex));
                }
            }

            ct.ThrowIfCancellationRequested();
            throw new HttpRequestException("Запись в ClickHouse не подтверждена. " +
                string.Join("; ", failures.Select(failure => failure.Message)), new AggregateException(failures));
        }

        private async Task EnsureEndpointAsync(Endpoint endpoint, CancellationToken ct)
        {
            if (endpoint.SchemaReady) return;
            await endpoint.SchemaGate.WaitAsync(ct);
            try
            {
                if (endpoint.SchemaReady) return;
                await ExecuteAsync(endpoint.Url, $"CREATE DATABASE IF NOT EXISTS `{_database}`", ct);
                var engine = _ha ? $"ReplicatedReplacingMergeTree('{_replicaPath}', '{{replica}}')" : "ReplacingMergeTree";
                await ExecuteAsync(endpoint.Url, ExportRow.CreateTableDdl(_database, _table, engine, _ha), ct);
                if (_ha)
                {
                    string check = $"SELECT throwIf(count() != 1, 'HA requires the replicated TWamp table') FROM system.replicas WHERE database='{_database}' AND table='{_table}' AND engine='ReplicatedReplacingMergeTree' AND zookeeper_path='{_replicaPath}' FORMAT Null";
                    await ExecuteAsync(endpoint.Url, check, ct);
                }
                await ExecuteAsync(endpoint.Url, $"ALTER TABLE `{_database}`.`{_table}` ADD COLUMN IF NOT EXISTS SourceProbe String, ADD COLUMN IF NOT EXISTS RequestInfo String, ADD COLUMN IF NOT EXISTS IPAddress String", ct);
                endpoint.SchemaReady = true;
                _logger.Info("ClickHouse: таблица {Database}.{Table} готова через {Url}", _database, _table, endpoint.Url);
            }
            finally
            {
                endpoint.SchemaGate.Release();
            }
        }

        private async Task ExecuteAsync(string url, string sql, CancellationToken cancellationToken)
        {
            using StringContent content = new(sql, Encoding.UTF8, "text/plain");
            using HttpResponseMessage response = await _http.PostAsync(url + "/?wait_end_of_query=1", content, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
        }

        private static string ValidateAddress(string? address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var url) ||
                url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 ||
                url.Query.Length != 0 || url.Fragment.Length != 0)
                throw new InvalidOperationException("ClickHouse:Urls и ClickHouse:Url принимают базовые адреса HTTP(S).");
            return url.AbsoluteUri.TrimEnd('/');
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.OK && string.IsNullOrWhiteSpace(body))
            {
                return;
            }

            throw new HttpRequestException($"ClickHouse ответил {(int)response.StatusCode}: {body.Trim()}");
        }

        /// <summary>Освобождает HTTP-клиент.</summary>
        public void Dispose()
        {
            _http.Dispose();
            foreach (var endpoint in _endpoints) endpoint.SchemaGate.Dispose();
        }
    }
}
