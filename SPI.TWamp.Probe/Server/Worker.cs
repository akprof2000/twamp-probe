// Ignore Spelling: SPI Twamp

using Newtonsoft.Json;
using NLog;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Runners;
using System.Collections.Concurrent;


namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Worker"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="configuration"></param>
    public class Worker(Logger logger, IConfiguration configuration) : IHostedService, IDisposable
    {
        private readonly Logger logger = logger;
        private readonly List<TaskInfo> _tasks = [];
        private int _hashCode = 0;
        private bool _jobFinish = false;
        private Timer? timer = null;
        private readonly IConfiguration _configuration = configuration;
        private readonly ConcurrentDictionary<Guid, CronExecuter> _cron = [];
        private readonly ConcurrentDictionary<Guid, RepeatExecuter> _repeat = [];

        private ConcurrentBag<ActionData>? actions;
        private bool disposedValue;
        private readonly AutoResetEvent _autoEvent = new(false);


        private void SetUpTimer(CancellationToken stoppingToken)
        {
            timer = new Timer(async x =>
            {
                await SomeMethodRunsAt(stoppingToken);
            }, null, 1000, 0);
        }


        private async Task SomeMethodRunsAt(CancellationToken stoppingToken)
        {
            if (timer != null)
            {
                await timer.DisposeAsync();
            }

            timer = null;

            if (_jobFinish)
            {
                ActionData[] lst = [.. actions!];
                await File.WriteAllTextAsync("JobResult.json", JsonConvert.SerializeObject(lst), stoppingToken);
                _jobFinish = false;
            }

            SetUpTimer(stoppingToken);

            await Task.CompletedTask;
        }

        private void EndJobCicle()
        {
            _jobFinish = true;
            _autoEvent.Set();
        }

        private async Task CheckTasksInfo(TaskInfo[]? list, CancellationToken stoppingToken)
        {
            if (list == null)
            {
                return;
            }
            bool saved = false;
            foreach (TaskInfo item in list)
            {
                TaskInfo? res = _tasks.Find(x => x.Id == item.Id);
                if (res != null)
                {
                    if (item.Type == TaskType.Scheduler && _cron.TryGetValue(item.Id, out CronExecuter? cron))
                    {
                        await cron.SetCronData(item, stoppingToken);
                    }
                }
                else
                {
                    logger.Info("Found new task data {@Task}", item);
                    _tasks.Add(item);
                    if (item.Type == TaskType.Scheduler)
                    {
                        _cron[item.Id] = new CronExecuter(logger, item, _configuration, actions!, EndJobCicle);
                        await _cron[item.Id].SetNextExecute(stoppingToken);
                    }
                    else if (item.Type == TaskType.Repeater)
                    {

                        _repeat[item.Id] = new RepeatExecuter(logger, item, _configuration, actions!, EndJobCicle);
                        _ = _repeat[item.Id].SomeMethodRunsAt(stoppingToken);
                        item.Delete = true;
                        saved = true;

                    }

                }
            }

            if (saved)
            {
                await File.WriteAllTextAsync("TaskInfo.json", JsonConvert.SerializeObject(_tasks), stoppingToken);
            }
        }

        /// <summary>
        /// Pushes the data.
        /// </summary>
        /// <param name="data">The data.</param>
        /// <param name="stoppingToken">The stopping token.</param>
        public async Task PushData(string data, CancellationToken stoppingToken)
        {
            int hash = data.GetHashCode();
            logger.Debug("Answer is OK with tasks {Tasks}", data);

            if (hash != _hashCode)
            {
                await File.WriteAllTextAsync("TaskInfo.json", data, stoppingToken);
                _hashCode = hash;
                TaskInfo[]? list = JsonConvert.DeserializeObject<TaskInfo[]>(data);
                logger.Debug("Hash changed update info {@Tasks}", list);

                await CheckTasksInfo(list, stoppingToken);
            }


            await Task.CompletedTask;
        }

        /// <summary>
        /// Triggered when the application host is ready to start the service.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
        public async Task StartAsync(CancellationToken cancellationToken)
        {

            if (File.Exists("JobResult.json"))
            {
                string text = await File.ReadAllTextAsync("JobResult.json", cancellationToken);
                ActionData[] lst = JsonConvert.DeserializeObject<ActionData[]>(text)!;
                actions = [.. lst];

            }
            else
            {
                actions = [];
            }

            if (File.Exists("TaskInfo.json"))
            {
                string text = await File.ReadAllTextAsync("TaskInfo.json", cancellationToken);
                _hashCode = text.GetHashCode();
                TaskInfo[]? list = JsonConvert.DeserializeObject<TaskInfo[]>(text);
                await CheckTasksInfo(list, cancellationToken);
            }

            SetUpTimer(cancellationToken);

            await Task.CompletedTask;


        }

        /// <summary>
        /// Triggered when the application host is performing a graceful shutdown.
        /// </summary>
        /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
        /// <returns>
        /// A <see cref="T:System.Threading.Tasks.Task" /> that represents the asynchronous Stop operation.
        /// </returns>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (timer != null)
            {
                await timer.DisposeAsync();
            }

           await Task.CompletedTask;

        }

        internal async Task<ActionData[]> GetActionsData(CancellationToken cancellationToken)
        {
            return await Task.Factory.StartNew(() =>
            {
                if (actions!.IsEmpty)
                {
                    int signaled = WaitHandle.WaitAny([_autoEvent, cancellationToken.WaitHandle], 30000);
                    if (signaled > 0)
                    {
                        return [];
                    }
                }
                ActionData[] lst = [.. actions!];
                actions.Clear();
                if (File.Exists("JobResult.json"))
                {
                    File.Delete("JobResult.json");
                }

                return lst;
            }, cancellationToken);


        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _autoEvent.Dispose();
                    timer?.Dispose();
                }

     
                disposedValue = true;
            }
        }


        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
