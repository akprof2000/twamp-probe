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
            "&input_format_skip_unknown_fields=1&date_time_input_format=best_effort";

        private readonly Logger _logger;
        private readonly HttpClient _http;
        private readonly string _baseUrl;
        private readonly string _database;
        private readonly string _table;

        /// <summary>Создаёт писателя по конфигурации (секция <c>ClickHouse</c>).</summary>
        /// <param name="logger">Логгер.</param>
        /// <param name="configuration">Конфигурация приложения.</param>
        public ClickHouseWriter(Logger logger, IConfiguration configuration)
        {
            _logger = logger;
            Enabled = configuration["ClickHouse:Enabled"].ConvertTo(false);
            _baseUrl = (configuration["ClickHouse:Url"] ?? "http://localhost:8123").TrimEnd('/');
            _database = configuration["ClickHouse:Database"] ?? "twamp";
            _table = configuration["ClickHouse:Table"] ?? "results";

            _http = new HttpClient
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
        public string Url => _baseUrl;

        /// <inheritdoc/>
        public string Target => $"{_database}.{_table}";

        /// <inheritdoc/>
        public async Task EnsureTableAsync(CancellationToken cancellationToken)
        {
            await ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{_database}`", cancellationToken);
            await ExecuteAsync(ExportRow.CreateTableDdl(_database, _table), cancellationToken);
            _logger.Info("ClickHouse: таблица {Database}.{Table} готова", _database, _table);
        }

        /// <inheritdoc/>
        public async Task InsertSegmentAsync(string path, CancellationToken cancellationToken)
        {
            string query = Uri.EscapeDataString($"INSERT INTO `{_database}`.`{_table}` FORMAT JSONEachRow");

            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using StreamContent content = new(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");

            using HttpResponseMessage response = await _http.PostAsync(
                $"{_baseUrl}/?query={query}{InsertSettings}", content, cancellationToken);

            await EnsureSuccessAsync(response, cancellationToken);
        }

        /// <summary>Выполняет запрос без данных (DDL).</summary>
        private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            using StringContent content = new(sql, Encoding.UTF8, "text/plain");
            using HttpResponseMessage response = await _http.PostAsync(_baseUrl + "/", content, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
        }

        /// <summary>
        /// Проверяет ответ базы. ClickHouse возвращает текст ошибки телом, поэтому
        /// включаем его в исключение — иначе в журнале остаётся один код состояния.
        /// </summary>
        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"ClickHouse ответил {(int)response.StatusCode}: {body.Trim()}");
        }

        /// <summary>Освобождает HTTP-клиент.</summary>
        public void Dispose() => _http.Dispose();
    }
}
