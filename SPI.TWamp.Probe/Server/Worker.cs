// Ignore Spelling: SPI Twamp

using Newtonsoft.Json;
using NLog;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Runners;
using System.Collections.Concurrent;

namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Фоновый сервис-оркестратор. Хранит реестр задач по расписанию, регистрирует под
    /// них исполнителей и применяет инкрементальные изменения, приходящие от сервера.
    /// <para>
    /// Обмен с сервером инкрементальный: <see cref="MergeJobs"/> получает только
    /// изменившиеся задачи и сливает их в реестр (добавляет, обновляет, удаляет), а не
    /// перезаписывает весь список. Сервер сверяет состояние через <see cref="GetKnownTaskIds"/>
    /// и досылает недостающее — так чистая (перезалитая) проба сама получает все свои задачи.
    /// </para>
    /// <para>
    /// Разовые задачи (Repeater) выполняются один раз и в реестре не хранятся.
    /// Выполнение зондов делегируется <see cref="IProbeDispatcher"/>, выдача результатов —
    /// <see cref="IResultStore"/>.
    /// </para>
    /// </summary>
    public sealed class Worker(Logger logger, IProbeDispatcher dispatcher, IResultStore resultStore) : IHostedService, IDisposable
    {
        /// <summary>Файл с сохранённым реестром задач по расписанию.</summary>
        private const string TasksFileName = "TaskInfo.json";

        private readonly Logger _logger = logger;
        private readonly IProbeDispatcher _dispatcher = dispatcher;
        private readonly IResultStore _resultStore = resultStore;

        /// <summary>Реестр задач по расписанию (единственный вид задач, хранимый между запусками).</summary>
        private readonly List<TaskInfo> _tasks = [];

        /// <summary>Активные исполнители задач по расписанию (ключ — идентификатор задачи).</summary>
        private readonly ConcurrentDictionary<Guid, CronExecuter> _cron = [];

        /// <summary>Сериализует применение изменений при одновременных запросах.</summary>
        private readonly SemaphoreSlim _registrationLock = new(1, 1);

        /// <summary>Источник токена, живущий всё время работы сервиса; отменяется при остановке.</summary>
        private readonly CancellationTokenSource _shutdown = new();
        private bool _disposed;

        /// <summary>Запуск сервиса: загрузка сохранённых результатов и реестра задач.</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _resultStore.LoadAsync(cancellationToken);

            if (File.Exists(TasksFileName))
            {
                string text = await File.ReadAllTextAsync(TasksFileName, cancellationToken);
                TaskInfo[]? saved = JsonConvert.DeserializeObject<TaskInfo[]>(text);
                await MergeJobs(saved ?? [], cancellationToken);
            }
        }

        /// <summary>
        /// Применяет инкрементальные изменения задач: добавляет новые, обновляет существующие
        /// и удаляет помеченные на удаление. Метод безопасен при параллельных вызовах.
        /// </summary>
        /// <param name="jobs">Изменившиеся задачи (не обязательно полный список).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        public async Task MergeJobs(TaskInfo[] jobs, CancellationToken cancellationToken)
        {
            await _registrationLock.WaitAsync(cancellationToken);
            try
            {
                bool changed = false;
                foreach (TaskInfo item in jobs)
                {
                    changed |= await MergeOneAsync(item);
                }

                if (changed)
                {
                    await PersistAsync(cancellationToken);
                }
            }
            finally
            {
                _ = _registrationLock.Release();
            }
        }

        /// <summary>Возвращает идентификаторы задач по расписанию, известных пробе сейчас.</summary>
        public Guid[] GetKnownTaskIds() => [.. _cron.Keys];

        /// <summary>
        /// Применяет одну задачу к реестру. Возвращает <c>true</c>, если реестр изменился
        /// (значит, его нужно сохранить на диск).
        /// </summary>
        private async Task<bool> MergeOneAsync(TaskInfo item)
        {
            // Разовые задачи выполняем немедленно и в реестре не храним.
            if (item.Type == TaskType.Repeater)
            {
                if (!item.Delete)
                {
                    _dispatcher.Enqueue(item);
                    _logger.Debug("Разовая задача {Id} поставлена в очередь", item.Id);
                }
                return false;
            }

            // Задача по расписанию, помеченная на удаление.
            if (item.Delete)
            {
                return RemoveScheduler(item.Id);
            }

            // Обновление существующей задачи по расписанию.
            if (_cron.TryGetValue(item.Id, out CronExecuter? existing))
            {
                int index = _tasks.FindIndex(t => t.Id == item.Id);
                if (index >= 0)
                {
                    _tasks[index] = item;
                }
                await existing.SetCronData(item); // перепланировать по новым параметрам
                _logger.Debug("Задача {Id} обновлена", item.Id);
                return true;
            }

            // Новая задача по расписанию.
            _tasks.Add(item);
            CronExecuter executer = new(_logger, item, _dispatcher);
            _cron[item.Id] = executer;
            await executer.SetNextExecute();
            _logger.Debug("Задача {Id} добавлена", item.Id);
            return true;
        }

        /// <summary>Останавливает и удаляет задачу по расписанию из реестра.</summary>
        private bool RemoveScheduler(Guid id)
        {
            bool changed = false;

            if (_cron.TryRemove(id, out CronExecuter? cron))
            {
                cron.Dispose();
                changed = true;
            }

            if (_tasks.RemoveAll(t => t.Id == id) > 0)
            {
                changed = true;
            }

            if (changed)
            {
                _logger.Debug("Задача {Id} удалена", id);
            }
            return changed;
        }

        /// <summary>Сохраняет текущий реестр задач по расписанию на диск.</summary>
        private async Task PersistAsync(CancellationToken cancellationToken) =>
            await File.WriteAllTextAsync(TasksFileName, JsonConvert.SerializeObject(_tasks), cancellationToken);

        /// <summary>Остановка сервиса: отмена выполнения и освобождение исполнителей.</summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _shutdown.CancelAsync();

            foreach (CronExecuter cron in _cron.Values)
            {
                cron.Dispose();
            }
        }

        /// <summary>Освобождает ресурсы сервиса.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            foreach (CronExecuter cron in _cron.Values)
            {
                cron.Dispose();
            }

            _registrationLock.Dispose();
            _shutdown.Dispose();
        }
    }
}
