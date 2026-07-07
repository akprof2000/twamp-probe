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
    /// Все адреса эндпоинтов пробы и таймауты собраны здесь.
    /// </summary>
    public sealed class ProbeClient : IProbeClient
    {
        /// <summary>Таймаут HTTP-запросов к пробе, секунд (в т. ч. длинный опрос CheckData).</summary>
        private readonly int _timeoutSeconds;

        /// <summary>Считывает таймаут из конфигурации («Probe:HttpTimeoutSec», по умолчанию 60 с).</summary>
        public ProbeClient(IConfiguration configuration)
        {
            _timeoutSeconds = configuration["Probe:HttpTimeoutSec"].ConvertTo(60);
        }

        /// <inheritdoc/>
        public Task<Identify> CheckInAsync(string probeUrl, CancellationToken cancellationToken) =>
            probeUrl
                .AppendPathSegment("api/ProbeInterface/CheckIn")
                .SetQueryParam("RequestInfo", probeUrl)
                .WithTimeout(_timeoutSeconds)
                .PostAsync(cancellationToken: cancellationToken)
                .ReceiveJson<Identify>();

        /// <inheritdoc/>
        public async Task PushTasksAsync(string probeUrl, IEnumerable<TaskInfo> tasks, CancellationToken cancellationToken) =>
            _ = await probeUrl
                .AppendPathSegment("api/ProbeInterface/SetJobs")
                .WithTimeout(_timeoutSeconds)
                .AllowAnyHttpStatus()
                .PostJsonAsync(tasks, cancellationToken: cancellationToken);

        /// <inheritdoc/>
        public Task<ActionData[]> GetResultsAsync(string probeUrl, CancellationToken cancellationToken) =>
            probeUrl
                .AppendPathSegment("api/ProbeInterface/CheckData")
                .WithTimeout(_timeoutSeconds)
                .AllowAnyHttpStatus()
                .GetAsync(cancellationToken: cancellationToken)
                .ReceiveJson<ActionData[]>();
    }
}
