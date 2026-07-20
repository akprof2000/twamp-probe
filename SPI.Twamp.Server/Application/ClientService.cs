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
        Logger logger, IClientRepository clients, IProbeClient probe, IProbePoller poller,
        ITaskService taskService, IChangeNotifier changeNotifier) : IClientService
    {
        private readonly Logger _logger = logger;
        private readonly IClientRepository _clients = clients;
        private readonly IProbeClient _probe = probe;
        private readonly IProbePoller _poller = poller;
        private readonly ITaskService _taskService = taskService;
        private readonly IChangeNotifier _changeNotifier = changeNotifier;

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
            _changeNotifier.Notify(); // данные проб изменились — событие для интерфейса
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
                // Частичное обновление (например, переименование из интерфейса):
                // незаполненные поля берём из существующей записи, чтобы не затереть
                // версию, адреса и описание пустыми значениями.
                client.Id = existing.Id;
                client.Version = string.IsNullOrEmpty(client.Version) ? existing.Version : client.Version;
                client.HostName = string.IsNullOrEmpty(client.HostName) ? existing.HostName : client.HostName;
                client.IPAddress = client.IPAddress is null or "" or "0.0.0.0" ? existing.IPAddress : client.IPAddress;
                client.MacAddress = client.MacAddress is "" or "00:00:00:00:00:00" ? existing.MacAddress : client.MacAddress;
                client.Title ??= existing.Title;
                client.Description ??= existing.Description;
                await _clients.UpdateAsync(client);
            }

            _changeNotifier.Notify(); // список проб изменился — событие для интерфейса
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteAsync(string requestInfo, bool deleteTasks, CancellationToken cancellationToken)
        {
            _logger.Info("Удаление пробы {RequestInfo} (с задачами: {DeleteTasks})", requestInfo, deleteTasks);

            // Сначала останавливаем опрос — чтобы фоновый цикл не «воскресил» статус пробы.
            _poller.StopPolling(requestInfo);

            bool removed = await _clients.DeleteAsync(requestInfo);
            await _clients.RemoveIdentifyAsync(requestInfo); // и из очереди неопознанных, если была

            if (removed && deleteTasks)
            {
                // Пометка задач удалёнными + попытка снять их с самой пробы (если жива).
                await _taskService.DeleteByRequestInfoAsync(requestInfo, cancellationToken);
            }

            if (removed)
            {
                // Отложенная очистка: фоновый цикл снимет ВСЕ задания с самой пробы,
                // когда (и если) она будет доступна в течение «Probe:CleanupWaitHours»;
                // по истечении срока задачи пробы и кэш вычищаются на сервере.
                await _clients.AddCleanupAsync(new PendingProbeCleanup { RequestInfo = requestInfo });
            }

            _changeNotifier.Notify(); // список проб изменился — событие для интерфейса
            return removed;
        }

        /// <inheritdoc/>
        public async Task RejectUnidentifiedAsync(string requestInfo)
        {
            _logger.Info("Отклонение неопознанной пробы {RequestInfo}", requestInfo);
            await _clients.RemoveIdentifyAsync(requestInfo);
            _changeNotifier.Notify();
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
