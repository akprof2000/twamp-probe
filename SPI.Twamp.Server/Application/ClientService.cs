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

            // Переносим из записи CheckIn данные, которые оператор не заполняет вручную
            // (версия пробы, MAC-адрес, сведения об интерфейсе).
            Identify? identify = await _clients.GetIdentifyAsync(client.RequestInfo);
            if (identify is not null)
            {
                client.Version = string.IsNullOrEmpty(client.Version) ? identify.Version : client.Version;
                client.MacAddress = client.MacAddress is "" or "00:00:00:00:00:00" ? identify.MacAddress : client.MacAddress;
                client.Title ??= identify.Title;
                client.Description ??= identify.Description;
            }

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
        /// Регистрирует пробу как неопознанную, если она ещё не подтверждена.
        /// Для уже подтверждённой пробы обновляет её сведения (версию после обновления,
        /// MAC-адрес и описание интерфейса).
        /// </summary>
        private async Task RegisterUnidentifiedAsync(Identify identify)
        {
            Client? existing = await _clients.GetByRequestInfoAsync(identify.RequestInfo);
            if (existing is not null)
            {
                // Проба уже подтверждена — актуализируем её данные из свежего CheckIn.
                existing.Version = identify.Version;
                existing.MacAddress = identify.MacAddress;
                existing.IPAddress = identify.IPAddress;
                existing.HostName = identify.HostName;
                existing.Title = identify.Title;
                existing.Description = identify.Description;
                await _clients.UpdateAsync(existing);
                return;
            }

            if (!await _clients.IdentifyExistsAsync(identify.RequestInfo))
            {
                await _clients.AddIdentifyAsync(identify);
            }
        }
    }
}
