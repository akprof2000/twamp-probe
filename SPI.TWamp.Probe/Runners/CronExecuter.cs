// Ignore Spelling: SPI Twamp

using NCrontab;
using NLog;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;

namespace SPI.Twamp.Probe.Runners
{
    /// <summary>
    /// Планировщик одной задачи по cron-расписанию.
    /// Вычисляет момент следующего запуска и по срабатыванию ставит задачу в очередь
    /// диспетчера, после чего сразу планирует следующее срабатывание.
    /// </summary>
    internal sealed class CronExecuter(
        Logger logger, TaskInfo task, IProbeDispatcher dispatcher, ITaskRunRegistry runRegistry) : IDisposable
    {
        private readonly Logger _logger = logger;
        private readonly IProbeDispatcher _dispatcher = dispatcher;
        private readonly ITaskRunRegistry _runRegistry = runRegistry;

        /// <summary>Источник токена отмены — останавливает планирование при удалении задачи или остановке.</summary>
        private readonly CancellationTokenSource _cts = new();

        private TaskInfo _task = task;
        private Timer? _timer;
        private bool _disposed;

        /// <summary>Планирует следующий запуск задачи согласно cron-выражению.</summary>
        internal async Task SetNextExecute()
        {
            await DisposeTimerAsync();

            if (_task.Delete)
            {
                _logger.Info("Задача {Guid} помечена на удаление — расписание остановлено", _task.Id);
                _runRegistry.SetNextRun(_task.Id, _task.Title, null);
                return;
            }

            CrontabSchedule schedule = CrontabSchedule.Parse(
                _task.CronExpression,
                new CrontabSchedule.ParseOptions { IncludingSeconds = _task.CronWithSeconds });

            DateTime next = schedule.GetNextOccurrence(DateTime.Now, _task.End);
            if (next >= _task.End)
            {
                _logger.Info("Задача {Guid} завершена по дате окончания {Date}", _task.Id, _task.End);
                _runRegistry.SetNextRun(_task.Id, _task.Title, null);
                return;
            }

            // Фиксируем план в реестре — оператор видит, когда следующий запуск.
            _runRegistry.SetNextRun(_task.Id, _task.Title, next);
            ScheduleAt(next);
        }

        /// <summary>Обновляет параметры задачи и пересчитывает расписание.</summary>
        internal async Task SetCronData(TaskInfo task)
        {
            _task = task;
            await SetNextExecute();
        }

        /// <summary>Ставит таймер на указанный момент срабатывания.</summary>
        private void ScheduleAt(DateTime alertTime)
        {
            TimeSpan delay = alertTime - DateTime.Now;
            if (delay < TimeSpan.Zero)
            {
                return; // момент уже прошёл
            }

            _timer = new Timer(async _ => await ExecuteAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }

        /// <summary>Ставит задачу в очередь на выполнение и планирует следующий запуск.</summary>
        private async Task ExecuteAsync()
        {
            await DisposeTimerAsync();

            if (_cts.IsCancellationRequested)
            {
                return; // задача удалена или сервис останавливается
            }

            _logger.Debug("Постановка задачи {Guid} в очередь в {Time}", _task.Id, DateTime.Now);

            // Только ставим в очередь — фактическое выполнение возьмёт на себя диспетчер.
            // Следующее срабатывание планируем сразу, не дожидаясь завершения зонда.
            _dispatcher.Enqueue(_task);
            await SetNextExecute();
        }

        /// <summary>Останавливает и освобождает текущий таймер.</summary>
        private async Task DisposeTimerAsync()
        {
            if (_timer != null)
            {
                await _timer.DisposeAsync();
                _timer = null;
            }
        }

        /// <summary>Останавливает расписание и освобождает ресурсы.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _cts.Cancel();
            _timer?.Dispose();
            _cts.Dispose();
        }
    }
}
