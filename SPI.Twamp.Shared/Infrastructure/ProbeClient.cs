// Ignore Spelling: SPI Twamp

using Flurl;
using Flurl.Http;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure
{
    /// <summary>
    /// Реализация <see cref="IProbeClient"/> на основе Flurl.
    /// Все адреса эндпоинтов пробы, таймауты и аутентификация собраны здесь.
    /// </summary>
    public sealed class ProbeClient : IProbeClient
    {
        /// <summary>Таймаут HTTP-запросов к пробе, секунд (в т. ч. длинный опрос CheckData).</summary>
        private readonly int _timeoutSeconds;

        /// <summary>Общий ключ API (заголовок X-Api-Key); пустой — аутентификация выключена.</summary>
        private readonly string? _apiKey;
        private readonly bool _quorumAck;
        private readonly string? _leaseToken;

        /// <summary>Считывает таймаут («Probe:HttpTimeoutSec») и ключ API («Auth:ApiKey») из конфигурации.</summary>
        public ProbeClient(IConfiguration configuration, string? leaseToken = null)
        {
            _timeoutSeconds = configuration["Probe:HttpTimeoutSec"].ConvertTo(60);
            _apiKey = configuration["Auth:ApiKey"];
            _leaseToken = leaseToken;
            _quorumAck = configuration.GetValue("HighAvailability:Enabled",false);
        }

        /// <summary>Формирует запрос к пробе с таймаутом и ключом API (если настроен).</summary>
        private IFlurlRequest Request(string probeUrl, string path)
        {
            IFlurlRequest request = probeUrl
                .AppendPathSegment(path)
                .WithTimeout(_timeoutSeconds);

            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request = request.WithHeader("X-Api-Key", _apiKey);
            }

            if (_leaseToken != null) request = request.WithHeader("X-Twamp-Lease", _leaseToken);
            return request;
        }

        /// <inheritdoc/>
        public Task<Identify> CheckInAsync(string probeUrl, CancellationToken cancellationToken) =>
            Request(probeUrl, "api/ProbeInterface/CheckIn")
                .SetQueryParam("RequestInfo", probeUrl)
                .PostAsync(cancellationToken: cancellationToken)
                .ReceiveJson<Identify>();

        /// <inheritdoc/>
        public async Task PushTasksAsync(string probeUrl, IEnumerable<TaskInfo> tasks, CancellationToken cancellationToken) =>
            _ = await Request(probeUrl, "api/ProbeInterface/SetJobs")
                .PostJsonAsync(tasks, cancellationToken: cancellationToken);

        /// <inheritdoc/>
        public Task<ProbeResultBatch> GetResultsAsync(string probeUrl, CancellationToken cancellationToken) =>
            Request(probeUrl, "api/ProbeInterface/CheckData")
                .GetAsync(cancellationToken: cancellationToken)
                .ReceiveJson<ProbeResultBatch>();

        /// <inheritdoc/>
        public async Task ConfirmResultsAsync(string probeUrl, Guid batchId, CancellationToken cancellationToken) =>
            _ = await Request(probeUrl, "api/ProbeInterface/ConfirmData")
                .WithHeader("X-Twamp-Commit", "clickhouse")
                .WithHeader("X-Twamp-Quorum", _quorumAck ? "2" : "1")
                .SetQueryParam("batchId", batchId)
                .PostAsync(cancellationToken: cancellationToken).ReceiveJson<bool>();

        /// <inheritdoc/>
        public Task<Guid[]> GetTaskIdsAsync(string probeUrl, CancellationToken cancellationToken) =>
            Request(probeUrl, "api/ProbeInterface/TaskIds")
                .GetAsync(cancellationToken: cancellationToken)
                .ReceiveJson<Guid[]>();

        /// <inheritdoc/>
        public async Task<IReadOnlyList<TaskInfo>> GetTasksAsync(string probeUrl, CancellationToken cancellationToken) =>
            await Request(probeUrl, "api/ProbeInterface/Tasks")
                .GetAsync(cancellationToken: cancellationToken)
                .ReceiveJson<TaskInfo[]>();

        /// <inheritdoc/>
        public Task<string> GetTaskStatusRawAsync(string probeUrl, string query, CancellationToken cancellationToken)
        {
            IFlurlRequest request = Request(probeUrl, "api/ProbeInterface/TaskStatus");
            if (!string.IsNullOrEmpty(query))
            {
                request.Url.Query = query; // фильтры и пагинация пробрасываются как есть
            }
            return request.GetAsync(cancellationToken: cancellationToken).ReceiveString();
        }

        /// <inheritdoc/>
        public Task<string> GetProbeStateRawAsync(string probeUrl, CancellationToken cancellationToken) =>
            Request(probeUrl, "api/ProbeInterface/ProbeState")
                .GetAsync(cancellationToken: cancellationToken)
                .ReceiveString();
    }
}
