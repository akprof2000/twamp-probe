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
    /// Фоновый сервис-оркестратор. Хранит реестр задач, регистрирует под них
    /// исполнителей по расписанию и обеспечивает сохранение/загрузку состояния.
    /// <para>
    /// Сам сервис не выполняет зонды и не выдаёт результаты: выполнение делегируется
    /// <see cref="IProbeDispatcher"/>, а выдача результатов — <see cref="IResultStore"/>.
    /// </para>
    /// </summary>
    public sealed class Worker(Logger logger, IProbeDispatcher dispatcher, IResultStore resultStore) : IHostedService, IDisposable
    {
        /// <summary>Файл с текущим списком зарегистрированных задач.</summary>
        private const string TasksFileName = "TaskInfo.json";

        private readonly Logger _logger = logger;
        private readonly IProbeDispatcher _dispatcher = dispatcher;
        private readonly IResultStore _resultStore = resultStore;

        /// <summary>Уже известные задачи (реестр).</summary>
        private readonly List<TaskInfo> _tasks = [];

        /// <summary>Идентификаторы известных задач для быстрой проверки «новая ли задача» (O(1) вместо O(n)).</summary>
        private readonly HashSet<Guid> _knownTaskIds = [];

        /// <summary>Активные исполнители задач по расписанию.</summary>
        private readonly ConcurrentDictionary<Guid, CronExecuter> _cron = [];

        /// <summary>Сериализует регистрацию задач при одновременных запросах.</summary>
        private readonly SemaphoreSlim _registrationLock = new(1, 1);

        /// <summary>Источник токена, живущий всё время работы сервиса; отменяется при остановке.</summary>
        private readonly CancellationTokenSource _shutdown = new();

        /// <summary>Хэш последнего полученного списка задач — чтобы не переобрабатывать одно и то же.</summary>
        private int _hashCode;
        private bool _disposed;

        /// <summary>Запуск сервиса: загрузка сохранённых результатов и ранее известных задач.</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _resultStore.LoadAsync(cancellationToken);

            if (File.Exists(TasksFileName))
            {
                string text = await File.ReadAllTextAsync(TasksFileName, cancellationToken);
                _hashCode = text.GetHashCode();
                TaskInfo[]? list = JsonConvert.DeserializeObject<TaskInfo[]>(text);
                await RegisterTasksAsync(list, cancellationToken);
            }
        }

        /// <summary>
        /// Принимает новый список задач от веб-интерфейса, сохраняет его и регистрирует
        /// изменения. Повторные одинаковые списки игнорируются по хэшу.
        /// </summary>
        /// <param name="data">JSON-представление массива задач.</param>
        /// <param name="cancellationToken">Токен отмены запроса.</param>
        public async Task PushData(string data, CancellationToken cancellationToken)
        {
            int hash = data.GetHashCode();
            if (hash == _hashCode)
            {
                return; // список не изменился — обрабатывать нечего
            }

            await File.WriteAllTextAsync(TasksFileName, data, cancellationToken);
            _hashCode = hash;

            TaskInfo[]? list = JsonConvert.DeserializeObject<TaskInfo[]>(data);
            _logger.Debug("Получен обновлённый список задач {@Tasks}", list);

            await RegisterTasksAsync(list, cancellationToken);
        }

        /// <summary>
        /// Регистрирует новые задачи и обновляет существующие. Доступ сериализован,
        /// поэтому метод безопасен при параллельных вызовах из нескольких запросов.
        /// </summary>
        private async Task RegisterTasksAsync(TaskInfo[]? list, CancellationToken cancellationToken)
        {
            if (list is null)
            {
                return;
            }

            await _registrationLock.WaitAsync(cancellationToken);
            try
            {
                bool tasksChanged = false;

                foreach (TaskInfo item in list)
                {
                    if (_knownTaskIds.Contains(item.Id))
                    {
                        // Известную задачу-планировщик обновляем «на лету».
                        if (item.Type == TaskType.Scheduler && _cron.TryGetValue(item.Id, out CronExecuter? cron))
                        {
                            await cron.SetCronData(item);
                        }
                        continue;
                    }

                    // На горячем пути (тысячи задач за один запрос) не сериализуем весь объект —
                    // это слишком дорого и переполняет очередь логов. Пишем компактно и на уровне Debug.
                    _logger.Debug("Обнаружена новая задача {Id} {Title} {Mode}", item.Id, item.Title, item.Mode);
                    _tasks.Add(item);
                    _ = _knownTaskIds.Add(item.Id);

                    switch (item.Type)
                    {
                        case TaskType.Scheduler:
                            await RegisterCronTask(item);
                            break;

                        case TaskType.Repeater:
                            // Разовую задачу выполняем один раз. При перезагрузке из файла
                            // она приходит уже помеченной Delete=true и повторно не запускается.
                            if (!item.Delete)
                            {
                                _dispatcher.Enqueue(item);
                                item.Delete = true;
                                tasksChanged = true;
                            }
                            break;
                    }
                }

                if (tasksChanged)
                {
                    await File.WriteAllTextAsync(TasksFileName, JsonConvert.SerializeObject(_tasks), cancellationToken);
                }
            }
            finally
            {
                _ = _registrationLock.Release();
            }
        }

        /// <summary>Создаёт исполнителя задачи по расписанию и планирует первый запуск.</summary>
        private async Task RegisterCronTask(TaskInfo item)
        {
            CronExecuter executer = new(_logger, item, _dispatcher);
            _cron[item.Id] = executer;
            await executer.SetNextExecute();
        }

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
