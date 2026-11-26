using System.Collections.Concurrent;
using NLog;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.BackgroundServices
{
    internal sealed class ProbeConfigurationService(IConfiguration configuration, IClientRepository clients,
        IProbeClient probe, ITaskService taskService, IChangeNotifier changeNotifier, Infrastructure.OrderedSchedules ordered) : BackgroundService, IProbePoller
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly IClientRepository _clients = clients;
        private readonly IProbeClient _probe = probe;
        private readonly ITaskService _taskService = taskService;
        private readonly IChangeNotifier _changeNotifier = changeNotifier;
        private readonly int _reconcileIntervalSeconds = Math.Max(1,configuration.GetValue("Probe:ReconcileIntervalSec", 30));
        private readonly int _cleanupWaitHours = configuration.GetValue("Probe:CleanupWaitHours", 24);
        private readonly ConcurrentDictionary<string,DateTime> _versionChecked = new();
        private static readonly TimeSpan VersionRefreshInterval = TimeSpan.FromMinutes(5);
        private CancellationToken _stopping;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stopping = stoppingToken;
            foreach (var client in await _clients.GetAllAsync())
                await ReconcileSafeAsync(client.RequestInfo, stoppingToken);
            await ReconcileLoopAsync(stoppingToken);
        }
        public void StartPolling(Client client) => _ = ReconcileSafeAsync(client.RequestInfo, _stopping);
        public void StopPolling(string requestInfo) => _versionChecked.TryRemove(requestInfo, out _);

        /// <summary>
        /// Фоновый цикл сверки: периодически приводит набор задач каждой пробы
        /// в соответствие с хранилищем (досылает недостающее, убирает устаревшее).
        /// </summary>
        private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_reconcileIntervalSeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    IReadOnlyList<Client> allClients = await _clients.GetAllAsync();
                    foreach (Client client in allClients)
                    {
                        await ReconcileSafeAsync(client.RequestInfo, cancellationToken);
                        if (!await ordered.IsRouted(client.RequestInfo)) await RefreshVersionAsync(client, cancellationToken);
                    }

                    await ProcessCleanupsAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warn("Ошибка цикла сверки задач: {Reason}", ErrorText.ShortReason(ex));
                    _logger.Debug(ex, "Подробности ошибки цикла сверки задач");
                }
            }
        }

        /// <summary>
        /// Обрабатывает отложенные очистки удалённых проб: если проба доступна —
        /// снимает с неё все задания и закрывает очистку; если проба не появилась
        /// в течение «Probe:CleanupWaitHours» часов — снимает очистку и вычищает
        /// её задачи и кэш результатов на сервере.
        /// </summary>
        private async Task ProcessCleanupsAsync(CancellationToken cancellationToken)
        {
            foreach (Contracts.PendingProbeCleanup cleanup in await _clients.GetCleanupsAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(await _clients.ExistsAsync(cleanup.RequestInfo))
                {
                    await _clients.RemoveCleanupAsync(cleanup.RequestInfo);
                    continue;
                }

                if (await TryClearProbeTasksAsync(cleanup.RequestInfo, cancellationToken))
                {
                    _logger.Info("Задания сняты с удалённой пробы {RequestInfo} — очистка закрыта", cleanup.RequestInfo);
                    await _clients.RemoveCleanupAsync(cleanup.RequestInfo);
                    continue;
                }

                if (DateTime.Now > cleanup.DeletedAt.AddHours(_cleanupWaitHours))
                {
                    // Проба так и не вышла на связь — забываем про неё: убираем задание
                    // на удаление и вычищаем её задачи вместе с кэшем результатов.
                    _logger.Info(
                        "Проба {RequestInfo} не появилась за {Hours} ч после удаления — вычищаем её задачи и кэш",
                        cleanup.RequestInfo, _cleanupWaitHours);

                    await _taskService.PurgeByRequestInfoAsync(cleanup.RequestInfo);
                    await _clients.RemoveCleanupAsync(cleanup.RequestInfo);
                }
            }
        }

        /// <summary>
        /// Пытается снять с пробы все её задания (известные и самой пробе, и серверу).
        /// Возвращает <c>false</c>, если проба недоступна.
        /// </summary>
        private async Task<bool> TryClearProbeTasksAsync(string requestInfo, CancellationToken cancellationToken)
        {
            try
            {
                // Union заданий: что проба помнит сама + что числится за ней в БД.
                Guid[] onProbe = await _probe.GetTaskIdsAsync(requestInfo, cancellationToken);
                HashSet<Guid> ids = [.. onProbe];
                foreach (Contracts.TaskInfo task in await _taskService.GetAllAsync())
                {
                    if (task.RequestInfo == requestInfo)
                    {
                        _ = ids.Add(task.Id);
                    }
                }

                if (ids.Count > 0)
                {
                    List<Contracts.TaskInfo> stubs = [.. ids.Select(id => new Contracts.TaskInfo
                    {
                        Id = id,
                        RequestInfo = requestInfo,
                        Type = Contracts.TaskType.Scheduler,
                        Delete = true
                    })];
                    await _probe.PushTasksAsync(requestInfo, stubs, cancellationToken);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false; // проба недоступна — попробуем на следующем тике
            }
        }

        /// <summary>
        /// Приводит сохранённую версию пробы к её текущей: после обновления пробы в списке
        /// должна быть видна новая версия, а не та, что была записана при регистрации.
        /// <para>
        /// Версия меняется только при обновлении пробы, поэтому спрашиваем её редко
        /// (<see cref="VersionRefreshInterval"/>). Сразу после восстановления связи проверка
        /// делается вне очереди: обновление пробы — это её перезапуск, то есть обрыв опроса.
        /// </para>
        /// </summary>
        private async Task RefreshVersionAsync(Client client, CancellationToken cancellationToken)
        {
            DateTime now = DateTime.Now;
            if (_versionChecked.TryGetValue(client.RequestInfo, out DateTime last) &&
                now - last < VersionRefreshInterval)
            {
                return;
            }
            _versionChecked[client.RequestInfo] = now;

            try
            {
                Identify identify = await _probe.CheckInAsync(client.RequestInfo, cancellationToken);
                if (string.IsNullOrEmpty(identify.Version) || identify.Version == client.Version)
                {
                    return; // версия не изменилась — записывать нечего
                }

                _logger.Info("Проба {ProbeUrl}: версия изменилась {Old} → {New}",
                    client.RequestInfo, client.Version, identify.Version);
                client.Version = identify.Version;
                await _clients.UpdateAsync(client);
                _changeNotifier.Notify(); // список проб в интерфейсе перечитает версию
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Проба недоступна — не беда: версию перечитаем на следующем тике.
                _logger.Debug(ex, "Не удалось получить версию пробы {ProbeUrl}", client.RequestInfo);
            }
        }

        /// <summary>Выполняет сверку одной пробы, не прерываясь на ошибках связи с ней.</summary>
        private async Task ReconcileSafeAsync(string requestInfo, CancellationToken cancellationToken)
        {
            try
            {
                if (!await ordered.SyncIfRouted(requestInfo,cancellationToken))
                    await _taskService.ReconcileAsync(requestInfo, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Остановка сервиса — тихо выходим.
            }
            catch (Exception ex)
            {
                _logger.Warn("Не удалось синхронизировать задачи пробы {ProbeUrl}: {Reason}",
                    requestInfo, ErrorText.ShortReason(ex));
                _logger.Debug(ex, "Подробности ошибки синхронизации задач пробы {ProbeUrl}", requestInfo);
            }
        }

    }
}
