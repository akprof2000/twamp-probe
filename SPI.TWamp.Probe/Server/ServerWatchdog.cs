// Ignore Spelling: SPI Twamp

using NLog;
using spi.twamp.Probe.Environment;
using SPI.Twamp.Probe.Abstractions;

namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Трекер последнего обращения сервера к пробе. Отметку ставит middleware
    /// на каждый входящий запрос к «api/**».
    /// </summary>
    public sealed class ServerContactTracker
    {
        private long _lastContactTicks = DateTime.Now.Ticks; // отсчёт с запуска пробы

        /// <summary>Момент последнего запроса от сервера.</summary>
        public DateTime LastContact => new(Interlocked.Read(ref _lastContactTicks));

        /// <summary>Фиксирует обращение сервера (вызывается middleware).</summary>
        public void MarkContact() => Interlocked.Exchange(ref _lastContactTicks, DateTime.Now.Ticks);
    }

    /// <summary>
    /// Сторож связи с сервером: если сервер не обращался к пробе дольше
    /// «Probe:ServerTimeoutHours» часов (0 — сторож выключен), проба считает себя
    /// удалённой — останавливает все задачи по расписанию, удаляет реестр
    /// (TaskInfo.json) и очищает кэш недоставленных результатов (JobResult.json).
    /// <para>
    /// Проба продолжает слушать HTTP: если сервер вернётся, CheckIn и фоновая
    /// сверка сервера восстановят задачи автоматически (самосинхронизация).
    /// </para>
    /// </summary>
    public sealed class ServerWatchdogService(
        Logger logger, IConfiguration configuration, ServerContactTracker tracker,
        Worker worker, IResultStore resultStore) : BackgroundService
    {
        /// <summary>Период проверки сторожа.</summary>
        private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

        private readonly Logger _logger = logger;
        private readonly ServerContactTracker _tracker = tracker;
        private readonly Worker _worker = worker;
        private readonly IResultStore _resultStore = resultStore;

        /// <summary>Таймаут молчания сервера, часов (0 — сторож выключен).</summary>
        private readonly int _timeoutHours = configuration["Probe:ServerTimeoutHours"].ConvertTo(24);

        /// <summary>Момент последней сработавшей очистки — защита от повторных срабатываний.</summary>
        private DateTime _lastCleared = DateTime.MinValue;

        /// <summary>Цикл сторожа: периодическая проверка давности последнего обращения сервера.</summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_timeoutHours <= 0)
            {
                _logger.Info("Сторож связи с сервером выключен (Probe:ServerTimeoutHours = 0)");
                return;
            }

            _logger.Info("Сторож связи: без запросов сервера дольше {Hours} ч задачи будут остановлены", _timeoutHours);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(CheckInterval, stoppingToken);
                    await CheckAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break; // штатная остановка
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Ошибка сторожа связи с сервером");
                }
            }
        }

        /// <summary>Одна проверка: если сервер молчит дольше таймаута — остановить и вычистить всё.</summary>
        private async Task CheckAsync(CancellationToken cancellationToken)
        {
            DateTime lastContact = _tracker.LastContact;

            // Уже чистили после этого контакта — ждём следующего обращения сервера.
            if (_lastCleared >= lastContact)
            {
                return;
            }

            if (DateTime.Now - lastContact < TimeSpan.FromHours(_timeoutHours))
            {
                return;
            }

            int stopped = await _worker.ClearAllAsync(cancellationToken);
            await _resultStore.ClearAsync();
            _lastCleared = DateTime.Now;

            _logger.Warn(
                "Сервер не обращался к пробе с {LastContact} (дольше {Hours} ч) — проба считает себя удалённой: " +
                "остановлено задач {Count}, реестр и кэш результатов очищены",
                lastContact, _timeoutHours, stopped);
        }
    }
}
