// Ignore Spelling: SPI Twamp

using NLog;
using spi.twamp.Probe.Environment;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using System.Threading.Channels;

namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Реализация диспетчера зондов на основе очереди (<see cref="Channel{T}"/>) и
    /// фиксированного пула рабочих циклов.
    /// <para>
    /// При старте поднимается N воркеров (N = «Probe:MaxParallel»). Каждый воркер в
    /// цикле берёт из очереди очередную задачу и выполняет её через <see cref="IProbeRunner"/>.
    /// Благодаря этому одновременно работает ровно N задач — независимо от того,
    /// поступило их 10 или 10 000, — а завершение запущенных зондов не конкурирует
    /// с тысячами ожидающих элементов в очереди пула потоков.
    /// </para>
    /// </summary>
    public sealed class ProbeDispatcher : IProbeDispatcher, IHostedService
    {
        private readonly Logger _logger;
        private readonly IProbeRunner _runner;
        private readonly int _workerCount;

        /// <summary>Очередь задач на выполнение. Безлимитная: приём задач никогда не блокирует отправителя.</summary>
        private readonly Channel<TaskInfo> _queue = Channel.CreateUnbounded<TaskInfo>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        /// <summary>Токен жизненного цикла пула воркеров.</summary>
        private readonly CancellationTokenSource _cts = new();
        private Task[] _workers = [];

        private readonly ITaskRunRegistry _runRegistry;

        /// <summary>Создаёт диспетчер и вычисляет число воркеров (предел параллелизма) из конфигурации.</summary>
        public ProbeDispatcher(Logger logger, IConfiguration configuration, IProbeRunner runner, ITaskRunRegistry runRegistry)
        {
            _logger = logger;
            _runner = runner;
            _runRegistry = runRegistry;

            // Каждый зонд — это отдельный процесс ОС (ping/TWamp). Для коротких
            // CPU-активных зондов (ping) разумно немного воркеров (~4 × ядра), а для
            // длинных I/O-зондов (twping -c 300 живёт минуты и почти спит) параллелизм
            // должен покрывать число одновременно активных задач — сотни и тысячи.
            // Точное значение задаётся ключом «Probe:MaxParallel».
            int defaultCount = Math.Max(16, System.Environment.ProcessorCount * 4);
            int count = configuration["Probe:MaxParallel"].ConvertTo(defaultCount);
            _workerCount = count > 0 ? count : defaultCount;
        }

        /// <inheritdoc/>
        public void Enqueue(TaskInfo task) => _queue.Writer.TryWrite(task);

        /// <summary>Поднимает пул рабочих циклов при старте приложения.</summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _workers = new Task[_workerCount];
            for (int i = 0; i < _workerCount; i++)
            {
                _workers[i] = Task.Run(() => WorkerLoopAsync(_cts.Token), CancellationToken.None);
            }

            _logger.Info("Запущено {Count} воркеров обработки зондов", _workerCount);
            return Task.CompletedTask;
        }

        /// <summary>Рабочий цикл: последовательно берёт задачи из очереди и выполняет их.</summary>
        private async Task WorkerLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (TaskInfo task in _queue.Reader.ReadAllAsync(cancellationToken))
                {
                    // Фиксируем начало и конец выполнения — это видно в логе и в
                    // эндпоинте TaskStatus (ответ на вопрос «запустилась ли задача»).
                    _runRegistry.MarkStarted(task);
                    _logger.Info("Начало выполнения задачи {Guid} «{Title}»", task.Id, task.Title);
                    try
                    {
                        await _runner.RunForNodesAsync(task, cancellationToken);
                        _logger.Info("Задача {Guid} «{Title}» выполнена", task.Id, task.Title);
                    }
                    catch (OperationCanceledException)
                    {
                        // Остановка сервиса — выходим из цикла на следующей итерации.
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Ошибка обработки задачи {Guid}", task.Id);
                        _runRegistry.ReportOutcome(task.Id, RunOutcome.StartFailed, null, ex.Message);
                    }
                    finally
                    {
                        _runRegistry.MarkFinished(task.Id);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Штатное завершение воркера при остановке.
            }
        }

        /// <summary>Останавливает приём задач и дожидается завершения воркеров.</summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _ = _queue.Writer.TryComplete();
            await _cts.CancelAsync();

            try
            {
                await Task.WhenAll(_workers).WaitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Не успели завершиться в отведённое время — это допустимо при остановке.
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }
}
