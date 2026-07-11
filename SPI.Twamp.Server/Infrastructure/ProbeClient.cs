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

        /// <summary>Считывает таймаут («Probe:HttpTimeoutSec») и ключ API («Auth:ApiKey») из конфигурации.</summary>
        public ProbeClient(IConfiguration configuration)
        {
            _timeoutSeconds = configuration["Probe:HttpTimeoutSec"].ConvertTo(60);
            _apiKey = configuration["Auth:ApiKey"];
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
                .AllowAnyHttpStatus()
                .PostJsonAsync(tasks, cancellationToken: cancellationToken);

        /// <inheritdoc/>
        public Task<ProbeResultBatch> GetResultsAsync(string probeUrl, CancellationToken cancellationToken) =>
            Request(probeUrl, "api/ProbeInterface/CheckData")
                .GetAsync(cancellationToken: cancellationToken)
                .ReceiveJson<ProbeResultBatch>();

        /// <inheritdoc/>
        public async Task ConfirmResultsAsync(string probeUrl, Guid batchId, CancellationToken cancellationToken) =>
            _ = await Request(probeUrl, "api/ProbeInterface/ConfirmData")
                .SetQueryParam("batchId", batchId)
                .PostAsync(cancellationToken: cancellationToken);

        /// <inheritdoc/>
        public Task<Guid[]> GetTaskIdsAsync(string probeUrl, CancellationToken cancellationToken) =>
            Request(probeUrl, "api/ProbeInterface/TaskIds")
                .GetAsync(cancellationToken: cancellationToken)
                .ReceiveJson<Guid[]>();

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
    }
}
