// Ignore Spelling: SPI Twamp

using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Реализация <see cref="IClientService"/>: регистрация и подтверждение проб,
    /// запуск их фонового опроса.
    /// </summary>
    public sealed class ClientService(
        Logger logger, IClientRepository clients, IProbeClient probe, IProbePoller poller) : IClientService
    {
        private readonly Logger _logger = logger;
        private readonly IClientRepository _clients = clients;
        private readonly IProbeClient _probe = probe;
        private readonly IProbePoller _poller = poller;

        /// <inheritdoc/>
        public Task<IReadOnlyList<Client>> GetClientsAsync() => _clients.GetAllAsync();

        /// <inheritdoc/>
        public Task<IReadOnlyList<Identify>> GetUnidentifiedAsync() => _clients.GetUnidentifiedAsync();

        /// <inheritdoc/>
        public async Task CheckInAsync(string probeUrl, CancellationToken cancellationToken)
        {
            _logger.Info("CheckIn пробы {ProbeUrl}", probeUrl);
            Identify identify = await _probe.CheckInAsync(probeUrl, cancellationToken);
            await RegisterUnidentifiedAsync(identify);
        }

        /// <inheritdoc/>
        public async Task SetInfoAsync(Client client, CancellationToken cancellationToken)
        {
            _logger.Info("Подтверждение пробы {@Client}", client);

            // Проба подтверждена оператором — убираем её из очереди неопознанных.
            await _clients.RemoveIdentifyAsync(client.RequestInfo);

            Client? existing = await _clients.GetByRequestInfoAsync(client.RequestInfo);
            if (existing is null)
            {
                await _clients.InsertAsync(client);
                _poller.StartPolling(client); // сразу запускаем фоновый опрос новой пробы
            }
            else
            {
                client.Id = existing.Id;
                await _clients.UpdateAsync(client);
            }
        }

        /// <summary>
        /// Регистрирует пробу как неопознанную, если она ещё не подтверждена
        /// и не стоит в очереди на подтверждение.
        /// </summary>
        private async Task RegisterUnidentifiedAsync(Identify identify)
        {
            if (await _clients.ExistsAsync(identify.RequestInfo))
            {
                return; // проба уже подтверждена — ничего не делаем
            }

            if (!await _clients.IdentifyExistsAsync(identify.RequestInfo))
            {
                await _clients.AddIdentifyAsync(identify);
            }
        }
    }
}
