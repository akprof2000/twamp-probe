// Ignore Spelling: SPI Twamp

using NLog;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using System.Collections.Concurrent;

namespace SPI.Twamp.Server.BackgroundServices
{
    /// <summary>
    /// Фоновый сервис опроса проб. Для каждой подтверждённой пробы поддерживает
    /// цикл «длинного опроса» результатов (CheckData) и сохраняет их в БД, а также
    /// периодически сверяет и синхронизирует набор задач пробы.
    /// <para>
    /// Пришёл на смену прежнему подходу с <c>Task.Factory.StartNew(async …)</c>: циклы
    /// корректно отменяются при остановке, не дублируются и используют экспоненциальную
    /// задержку при ошибках связи. Периодическая сверка (<see cref="ITaskService.ReconcileAsync"/>)
    /// досылает недостающие задачи — в т. ч. автоматически конфигурирует чистую перезалитую пробу.
    /// </para>
    /// </summary>
    public sealed class ProbePollingService(
        IConfiguration configuration, IClientRepository clients,
        IProbeClient probe, IResultIngestService ingest,
        ITaskService taskService, IChangeNotifier changeNotifier)
        : IHostedService, IProbePoller, IProbeStatusProvider, IDisposable
    {
        /// <summary>Максимальная задержка между попытками при ошибках связи, секунд.</summary>
        private const int MaxBackoffSeconds = 900;

        /// <summary>Пауза опроса, пока буфер результатов переполнен, секунд.</summary>
        private const int BackpressureDelaySeconds = 15;

        // Логгер берём статически (а не через DI): иначе у конструктора 8 зависимостей.
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly IClientRepository _clients = clients;
        private readonly IProbeClient _probe = probe;
        private readonly IResultIngestService _ingest = ingest;
        private readonly ITaskService _taskService = taskService;
        private readonly IChangeNotifier _changeNotifier = changeNotifier;

        /// <summary>Интервал фоновой сверки задач с пробами, секунд.</summary>
        private readonly int _reconcileIntervalSeconds =
            configuration["Probe:ReconcileIntervalSec"].ConvertTo(30);

        /// <summary>
        /// Сколько часов после удаления пробы ждать её появления, чтобы снять с неё
        /// задания. По истечении срока отложенная очистка снимается, а задачи пробы
        /// и кэш её результатов вычищаются на сервере.
        /// </summary>
        private readonly int _cleanupWaitHours = configuration["Probe:CleanupWaitHours"].ConvertTo(24);

        /// <summary>
        /// Запущенные циклы опроса по адресу пробы — защита от повторного запуска.
        /// Вместе с задачей хранится её персональный источник отмены, чтобы опрос
        /// одной пробы можно было остановить (удаление пробы), не трогая остальные.
        /// </summary>
        private readonly ConcurrentDictionary<string, (Task Loop, CancellationTokenSource Cts)> _pollers = new();

        /// <summary>Текущее состояние опроса каждой пробы (для страницы статуса).</summary>
        private readonly ConcurrentDictionary<string, ProbePollState> _states = new();

        /// <summary>Последний результат каждой задачи (для колонки статуса в списке задач).</summary>
        private readonly ConcurrentDictionary<Guid, TaskLastResult> _lastResults = new();

        /// <summary>Токен жизненного цикла всех фоновых циклов.</summary>
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, ProbePollState> GetStates() =>
            new Dictionary<string, ProbePollState>(_states);

        /// <inheritdoc/>
        public IReadOnlyDictionary<Guid, TaskLastResult> GetLastResults() =>
            new Dictionary<Guid, TaskLastResult>(_lastResults);

        /// <summary>Старт сервиса: индексы, опрос известных проб и цикл сверки задач.</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Запуск сервиса опроса проб");

            IReadOnlyList<Client> known = await _clients.GetAllAsync();
            foreach (Client client in known)
            {
                StartPolling(client);
            }

            // Единый фоновый цикл сверки задач для всех проб.
            _ = Task.Run(() => ReconcileLoopAsync(_cts.Token), CancellationToken.None);

            // Прогрева «последних результатов» из БД больше нет: результаты не оседают
            // в базе сервера, а сразу уходят в буфер и далее в ClickHouse. Реестр
            // наполняется заново по мере прихода результатов от проб.
        }

        /// <summary>
        /// Собирает «последний результат» задачи из записи БД: исход запуска,
        /// код выхода и краткий текст ошибки для интерфейса.
        /// </summary>
        private static TaskLastResult BuildLastResult(ActionData action)
        {
            string? outcome = string.IsNullOrEmpty(action.Outcome) ? null : action.Outcome;

            // Для записей новых проб исход определяет Outcome; для старых — эвристика
            // по тексту ошибки и коду выхода.
            bool hasError = outcome is not null
                ? outcome != "Success"
                : !string.IsNullOrEmpty(action.ErrorConsole) || action.ExitCode is not (null or 0);

            // Текст ошибки для интерфейса обрезаем до 300 символов.
            string? error = null;
            if (!string.IsNullOrEmpty(action.ErrorConsole))
            {
                error = action.ErrorConsole.Length > 300 ? action.ErrorConsole[..300] : action.ErrorConsole;
            }

            return new TaskLastResult(action.Creation ?? DateTime.Now, hasError, outcome, action.ExitCode, error);
        }

        /// <inheritdoc/>
        public void StartPolling(Client client)
        {
            if (_disposed)
            {
                return;
            }

            // GetOrAdd гарантирует один цикл на пробу даже при параллельных вызовах.
            // Токен цикла связан с общим: отменяется и при остановке сервиса,
            // и индивидуально при удалении пробы (StopPolling).
            _ = _pollers.GetOrAdd(client.RequestInfo, url =>
            {
                _logger.Info("Старт опроса пробы {ProbeUrl}", url);
                CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                return (Task.Run(() => PollLoopAsync(url, cts.Token)), cts);
            });

            // Немедленная первичная сверка — например, чтобы сразу настроить только что
            // подтверждённую (или перезалитую) пробу, не дожидаясь тика фонового цикла.
            _ = Task.Run(() => ReconcileSafeAsync(client.RequestInfo, _cts.Token));
        }

        /// <inheritdoc/>
        public void StopPolling(string requestInfo)
        {
            if (!_pollers.TryRemove(requestInfo, out (Task Loop, CancellationTokenSource Cts) poller))
            {
                return;
            }

            _logger.Info("Остановка опроса пробы {ProbeUrl}", requestInfo);
            poller.Cts.Cancel();
            poller.Cts.Dispose();
            _ = _states.TryRemove(requestInfo, out _); // пробы больше нет на странице статуса
            _changeNotifier.Notify();
        }

        /// <summary>
        /// Бесконечный цикл опроса одной пробы: запрашивает результаты и сохраняет их.
        /// При ошибках связи ждёт с нарастающей задержкой (backoff), при успехе — сбрасывает её.
        /// </summary>
        private async Task PollLoopAsync(string probeUrl, CancellationToken cancellationToken)
        {
            int backoffSeconds = 1;
            long totalResults = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    totalResults += await PollOnceAsync(probeUrl, cancellationToken);
                    backoffSeconds = 1; // связь есть — возвращаем минимальную задержку
                    MarkPollSuccess(probeUrl, totalResults);
                }
                catch (OperationCanceledException)
                {
                    break; // штатная остановка сервиса
                }
                catch (Exception ex)
                {
                    MarkPollFailure(probeUrl, ex, totalResults, backoffSeconds);
                    if (!await DelayBackoffAsync(backoffSeconds, cancellationToken))
                    {
                        break; // сервис остановлен во время ожидания
                    }

                    // Удваиваем задержку до разумного предела — чтобы не «долбить» недоступную пробу.
                    backoffSeconds = Math.Min(backoffSeconds * 2, MaxBackoffSeconds);
                }
            }

            _logger.Info("Опрос пробы {ProbeUrl} остановлен", probeUrl);
        }

        /// <summary>
        /// Один цикл опроса пробы: получает пачку результатов, сохраняет их (с отбрасыванием
        /// дубликатов), разбирает статистику и подтверждает доставку. Возвращает число новых записей.
        /// </summary>
        private async Task<int> PollOnceAsync(string probeUrl, CancellationToken cancellationToken)
        {
            // Буфер переполнен: ClickHouse недоступен слишком долго. Не забираем результаты —
            // они подождут на пробе (у неё своя очередь с сохранением на диск), а мы не
            // потеряем уже принятое. Ждём разбора очереди и пробуем снова.
            if (_ingest.IsBackpressured)
            {
                await Task.Delay(TimeSpan.FromSeconds(BackpressureDelaySeconds), cancellationToken);
                return 0;
            }

            ProbeResultBatch batch = await _probe.GetResultsAsync(probeUrl, cancellationToken);
            if (batch.Items.Length == 0)
            {
                return 0;
            }

            _logger.Info("Получено {Count} результатов от пробы {ProbeUrl}", batch.Items.Length, probeUrl);

            // Разбираем вывод зонда и укладываем строки в буфер. Метод возвращает
            // управление только после сброса данных на диск.
            int rows = await _ingest.IngestAsync(batch.Items, cancellationToken);

            // Обновляем «последний результат» каждой задачи — он показывается
            // в списке задач веб-интерфейса.
            foreach (ActionData action in batch.Items)
            {
                _lastResults[action.TaskId] = BuildLastResult(action);
            }

            // Подтверждаем доставку — только теперь проба удалит пачку у себя.
            // Повторная доставка (если подтверждение не дойдёт) не страшна:
            // ClickHouse схлопнет повтор по ResultId.
            await _probe.ConfirmResultsAsync(probeUrl, batch.BatchId, cancellationToken);

            _changeNotifier.Notify(); // новые результаты — будим веб-интерфейс
            _logger.Debug("Уложено в буфер строк: {Rows}", rows);
            return batch.Items.Length;
        }

        /// <summary>Фиксирует успешный опрос: сбрасывает состояние и будит интерфейс при восстановлении связи.</summary>
        private void MarkPollSuccess(string probeUrl, long totalResults)
        {
            bool wasFailing = _states.TryGetValue(probeUrl, out ProbePollState? prevState) &&
                              prevState.BackoffSeconds > 0;
            _states[probeUrl] = new ProbePollState(DateTime.Now, null, null, totalResults, 0);
            if (wasFailing)
            {
                _changeNotifier.Notify();
            }
        }

        /// <summary>Фиксирует ошибку опроса пробы и обновляет её состояние для страницы статуса.</summary>
        private void MarkPollFailure(string probeUrl, Exception ex, long totalResults, int backoffSeconds)
        {
            _logger.Warn(ex, "Ошибка опроса пробы {ProbeUrl}, повтор через {Delay} c", probeUrl, backoffSeconds);
            ProbePollState? prev = _states.TryGetValue(probeUrl, out ProbePollState? p) ? p : null;
            bool becameFailing = prev is null || prev.BackoffSeconds == 0;
            _states[probeUrl] = new ProbePollState(prev?.LastSuccess, DateTime.Now, ex.Message, totalResults, backoffSeconds);
            if (becameFailing)
            {
                _changeNotifier.Notify(); // проба перестала отвечать — событие для интерфейса
            }
        }

        /// <summary>Ждёт backoff-паузу; возвращает <c>false</c>, если сервис остановлен во время ожидания.</summary>
        private static async Task<bool> DelayBackoffAsync(int backoffSeconds, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

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
                    }

                    await ProcessCleanupsAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Ошибка цикла сверки задач");
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

                    IReadOnlyList<Guid> purged = await _taskService.PurgeByRequestInfoAsync(cleanup.RequestInfo);
                    foreach (Guid id in purged)
                    {
                        _ = _lastResults.TryRemove(id, out _); // кэш «последних результатов»
                    }
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

        /// <summary>Выполняет сверку одной пробы, не прерываясь на ошибках связи с ней.</summary>
        private async Task ReconcileSafeAsync(string requestInfo, CancellationToken cancellationToken)
        {
            try
            {
                await _taskService.ReconcileAsync(requestInfo, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Остановка сервиса — тихо выходим.
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Не удалось синхронизировать задачи пробы {ProbeUrl}", requestInfo);
            }
        }

        /// <summary>Остановка сервиса: отмена всех циклов опроса и ожидание их завершения.</summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Остановка сервиса опроса проб");
            await _cts.CancelAsync();

            try
            {
                await Task.WhenAll(_pollers.Values.Select(p => p.Loop)).WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Не все циклы успели завершиться в отведённое время — это допустимо при остановке.
            }
        }

        /// <summary>Освобождает источник токена отмены.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cts.Dispose();
        }
    }
}
