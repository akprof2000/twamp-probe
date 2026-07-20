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
        IProbeClient probe, IActionRepository actions, IStatRepository stats,
        ITaskService taskService, IChangeNotifier changeNotifier)
        : IHostedService, IProbePoller, IProbeStatusProvider, IDisposable
    {
        /// <summary>Максимальная задержка между попытками при ошибках связи, секунд.</summary>
        private const int MaxBackoffSeconds = 900;

        // Логгер берём статически (а не через DI): иначе у конструктора 8 зависимостей.
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly IClientRepository _clients = clients;
        private readonly IProbeClient _probe = probe;
        private readonly IActionRepository _actions = actions;
        private readonly IStatRepository _stats = stats;
        private readonly ITaskService _taskService = taskService;
        private readonly IChangeNotifier _changeNotifier = changeNotifier;

        /// <summary>Интервал фоновой сверки задач с пробами, секунд.</summary>
        private readonly int _reconcileIntervalSeconds =
            configuration["Probe:ReconcileIntervalSec"].ConvertTo(30);

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
            await _actions.EnsureIndexesAsync();
            await _stats.EnsureIndexesAsync();

            IReadOnlyList<Client> known = await _clients.GetAllAsync();
            foreach (Client client in known)
            {
                StartPolling(client);
            }

            // Единый фоновый цикл сверки задач для всех проб.
            _ = Task.Run(() => ReconcileLoopAsync(_cts.Token), CancellationToken.None);

            // Прогрев «последних результатов» из БД: после перезапуска сервера история
            // выполнения не должна выглядеть пустой («не выполнялась ни разу»).
            _ = Task.Run(() => WarmupLastResultsAsync(_cts.Token), CancellationToken.None);
        }

        /// <summary>
        /// Заполняет реестр последних результатов из БД: для каждой известной задачи
        /// берётся её последний сохранённый результат. Свежие записи, успевшие прийти
        /// от проб, не затираются (TryAdd).
        /// </summary>
        private async Task WarmupLastResultsAsync(CancellationToken cancellationToken)
        {
            try
            {
                int restored = 0;
                IEnumerable<Guid> taskIds = (await _taskService.GetAllAsync()).Select(task => task.Id);
                foreach (Guid taskId in taskIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ActionData? last = await _actions.GetLastByTaskAsync(taskId);
                    if (last is not null && _lastResults.TryAdd(taskId, BuildLastResult(last)))
                    {
                        restored++;
                    }
                }

                if (restored > 0)
                {
                    _logger.Info("Восстановлено последних результатов из БД: {Count}", restored);
                    _changeNotifier.Notify(); // интерфейс перечитает статусы
                }
            }
            catch (OperationCanceledException)
            {
                // Остановка сервиса во время прогрева — штатно.
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Не удалось восстановить последние результаты из БД");
            }
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
            ProbeResultBatch batch = await _probe.GetResultsAsync(probeUrl, cancellationToken);
            if (batch.Items.Length == 0)
            {
                return 0;
            }

            _logger.Info("Получено {Count} результатов от пробы {ProbeUrl}", batch.Items.Length, probeUrl);

            // Сохраняем с отбрасыванием дубликатов (повторная доставка после сбоя).
            IReadOnlyList<ActionData> fresh = await _actions.AddRangeAsync(batch.Items);

            // Обновляем «последний результат» каждой задачи — он показывается
            // в списке задач веб-интерфейса.
            foreach (ActionData action in fresh)
            {
                _lastResults[action.TaskId] = BuildLastResult(action);
            }

            // Разбираем статистику сразу при приёме — выгрузка отчёта
            // потом читает готовые записи без повторного парсинга.
            await StoreStatsAsync(fresh);

            // Подтверждаем доставку — только теперь проба удалит пачку у себя.
            await _probe.ConfirmResultsAsync(probeUrl, batch.BatchId, cancellationToken);

            if (fresh.Count > 0)
            {
                _changeNotifier.Notify(); // новые результаты — будим веб-интерфейс
            }
            return fresh.Count;
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

        /// <summary>Разбирает принятые результаты в статистику и сохраняет её.</summary>
        private async Task StoreStatsAsync(IReadOnlyList<ActionData> fresh)
        {
            if (fresh.Count == 0)
            {
                return;
            }

            List<StatRecord> records = [];
            foreach (ActionData action in fresh)
            {
                // Парсер выбирается по режиму задачи (twampy — свой формат вывода),
                // Mode проставляется внутри диспетчера.
                foreach (Parser.TwPingStats parsed in
                         Parser.ProbeOutputParser.Parse(action.Mode, action.Console, action.ErrorConsole, action.TaskId))
                {
                    records.Add(new StatRecord
                    {
                        Creation = action.Creation ?? DateTime.Now,
                        TaskId = action.TaskId,
                        CallLine = action.CallLine,
                        Stats = parsed
                    });
                }
            }

            await _stats.AddRangeAsync(records);
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
