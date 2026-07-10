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
        Logger logger, IConfiguration configuration, IClientRepository clients,
        IProbeClient probe, IActionRepository actions, IStatRepository stats, ITaskService taskService)
        : IHostedService, IProbePoller, IProbeStatusProvider, IDisposable
    {
        /// <summary>Максимальная задержка между попытками при ошибках связи, секунд.</summary>
        private const int MaxBackoffSeconds = 900;

        private readonly Logger _logger = logger;
        private readonly IClientRepository _clients = clients;
        private readonly IProbeClient _probe = probe;
        private readonly IActionRepository _actions = actions;
        private readonly IStatRepository _stats = stats;
        private readonly ITaskService _taskService = taskService;

        /// <summary>Интервал фоновой сверки задач с пробами, секунд.</summary>
        private readonly int _reconcileIntervalSeconds =
            configuration["Probe:ReconcileIntervalSec"].ConvertTo(30);

        /// <summary>Запущенные циклы опроса по адресу пробы — защита от повторного запуска.</summary>
        private readonly ConcurrentDictionary<string, Task> _pollers = new();

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
        }

        /// <inheritdoc/>
        public void StartPolling(Client client)
        {
            if (_disposed)
            {
                return;
            }

            // GetOrAdd гарантирует один цикл на пробу даже при параллельных вызовах.
            _ = _pollers.GetOrAdd(client.RequestInfo, url =>
            {
                _logger.Info("Старт опроса пробы {ProbeUrl}", url);
                return Task.Run(() => PollLoopAsync(url, _cts.Token));
            });

            // Немедленная первичная сверка — например, чтобы сразу настроить только что
            // подтверждённую (или перезалитую) пробу, не дожидаясь тика фонового цикла.
            _ = Task.Run(() => ReconcileSafeAsync(client.RequestInfo, _cts.Token));
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
                    ProbeResultBatch batch = await _probe.GetResultsAsync(probeUrl, cancellationToken);
                    if (batch.Items.Length > 0)
                    {
                        _logger.Info("Получено {Count} результатов от пробы {ProbeUrl}", batch.Items.Length, probeUrl);

                        // Сохраняем с отбрасыванием дубликатов (повторная доставка после сбоя).
                        IReadOnlyList<ActionData> fresh = await _actions.AddRangeAsync(batch.Items);

                        // Обновляем «последний результат» каждой задачи — он показывается
                        // в списке задач веб-интерфейса.
                        foreach (ActionData action in fresh)
                        {
                            _lastResults[action.TaskId] = new TaskLastResult(
                                action.Creation ?? DateTime.Now,
                                !string.IsNullOrEmpty(action.ErrorConsole));
                        }

                        // Разбираем статистику сразу при приёме — выгрузка отчёта
                        // потом читает готовые записи без повторного парсинга.
                        await StoreStatsAsync(fresh);

                        // Подтверждаем доставку — только теперь проба удалит пачку у себя.
                        await _probe.ConfirmResultsAsync(probeUrl, batch.BatchId, cancellationToken);
                        totalResults += fresh.Count;
                    }

                    backoffSeconds = 1; // связь есть — возвращаем минимальную задержку
                    _states[probeUrl] = new ProbePollState(DateTime.Now, null, null, totalResults, 0);
                }
                catch (OperationCanceledException)
                {
                    break; // штатная остановка сервиса
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Ошибка опроса пробы {ProbeUrl}, повтор через {Delay} c", probeUrl, backoffSeconds);
                    ProbePollState? prev = _states.TryGetValue(probeUrl, out ProbePollState? p) ? p : null;
                    _states[probeUrl] = new ProbePollState(prev?.LastSuccess, DateTime.Now, ex.Message, totalResults, backoffSeconds);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Удваиваем задержку до разумного предела — чтобы не «долбить» недоступную пробу.
                    backoffSeconds = Math.Min(backoffSeconds * 2, MaxBackoffSeconds);
                }
            }

            _logger.Info("Опрос пробы {ProbeUrl} остановлен", probeUrl);
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
                foreach (Parser.TwPingStats parsed in
                         Parser.TwPingParser.ParseMany(action.Console, action.ErrorConsole, action.TaskId))
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
                    IReadOnlyList<Client> clients = await _clients.GetAllAsync();
                    foreach (Client client in clients)
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
                await Task.WhenAll(_pollers.Values).WaitAsync(cancellationToken);
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
