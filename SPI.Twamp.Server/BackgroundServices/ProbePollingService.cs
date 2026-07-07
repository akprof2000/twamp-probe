// Ignore Spelling: SPI Twamp

using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using System.Collections.Concurrent;

namespace SPI.Twamp.Server.BackgroundServices
{
    /// <summary>
    /// Фоновый сервис опроса проб. Для каждой подтверждённой пробы поддерживает
    /// отдельный цикл «длинного опроса» результатов (CheckData) и сохраняет их в БД.
    /// <para>
    /// Пришёл на смену прежнему подходу с <c>Task.Factory.StartNew(async …)</c>: теперь
    /// циклы корректно отменяются при остановке сервиса, не дублируются для одной пробы
    /// и используют экспоненциальную задержку при ошибках связи.
    /// </para>
    /// </summary>
    public sealed class ProbePollingService(
        Logger logger, IClientRepository clients, IProbeClient probe, IActionRepository actions)
        : IHostedService, IProbePoller, IDisposable
    {
        /// <summary>Максимальная задержка между попытками при ошибках связи, секунд.</summary>
        private const int MaxBackoffSeconds = 900;

        private readonly Logger _logger = logger;
        private readonly IClientRepository _clients = clients;
        private readonly IProbeClient _probe = probe;
        private readonly IActionRepository _actions = actions;

        /// <summary>Запущенные циклы опроса по адресу пробы — защита от повторного запуска.</summary>
        private readonly ConcurrentDictionary<string, Task> _pollers = new();

        /// <summary>Токен жизненного цикла всех циклов опроса.</summary>
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        /// <summary>Старт сервиса: подготовка индексов и запуск опроса всех известных проб.</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.Info("Запуск сервиса опроса проб");
            await _actions.EnsureIndexesAsync();

            IReadOnlyList<Client> known = await _clients.GetAllAsync();
            foreach (Client client in known)
            {
                StartPolling(client);
            }
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
        }

        /// <summary>
        /// Бесконечный цикл опроса одной пробы: запрашивает результаты и сохраняет их.
        /// При ошибках связи ждёт с нарастающей задержкой (backoff), при успехе — сбрасывает её.
        /// </summary>
        private async Task PollLoopAsync(string probeUrl, CancellationToken cancellationToken)
        {
            int backoffSeconds = 1;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ActionData[] results = await _probe.GetResultsAsync(probeUrl, cancellationToken);
                    if (results.Length > 0)
                    {
                        _logger.Info("Получено {Count} результатов от пробы {ProbeUrl}", results.Length, probeUrl);
                        await _actions.AddRangeAsync(results);
                    }

                    backoffSeconds = 1; // связь есть — возвращаем минимальную задержку
                }
                catch (OperationCanceledException)
                {
                    break; // штатная остановка сервиса
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Ошибка опроса пробы {ProbeUrl}, повтор через {Delay} c", probeUrl, backoffSeconds);
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
