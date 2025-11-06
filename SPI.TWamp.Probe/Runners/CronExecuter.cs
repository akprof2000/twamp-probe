// Ignore Spelling: SPI Twamp

using NCrontab;
using NLog;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Environment;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SPI.Twamp.Probe.Runners
{
    /// <summary>
    /// 
    /// </summary>
    internal class CronExecuter(Logger logger, TaskInfo task, IConfiguration configuration, ConcurrentBag<ActionData> bag, Action endJob) : IDisposable
    {
        private readonly IConfiguration _configuration = configuration;
        private TaskInfo _task = task;
        private readonly Logger logger = logger;
        private Timer? timer = null;
        private bool disposedValue;
        private readonly ConcurrentBag<ActionData> _answers = bag;
        private readonly Action _endJob = endJob;

        private void SetUpTimer(DateTime alertTime, CancellationToken stoppingToken)
        {
            DateTime current = DateTime.Now;
            TimeSpan timeToGo = alertTime - current;
            if (timeToGo < TimeSpan.Zero)
            {
                return;//time already passed
            }
            timer = new Timer(async x =>
            {
                await SomeMethodRunsAt(stoppingToken);
            }, null, timeToGo, Timeout.InfiniteTimeSpan);
        }

        
        private async Task SomeMethodRunsAt(CancellationToken stoppingToken)
        {
            if (timer != null)
            {
                await timer.DisposeAsync();
            }

            timer = null;
            logger.Info("Run job {Guid} at {Time}", _task.Id, DateTime.Now);
            try
            {


                string[] arr = _task.EndNode.Split([";", ","], StringSplitOptions.RemoveEmptyEntries);
                List<Task> tasks = [];
                foreach (string item in arr)
                {
                    tasks.Add(Task.Run(() => HostFunctions.DoWork(_task,_configuration, logger, _endJob, _answers, stoppingToken), stoppingToken));
                }

                if (tasks != null)
                {
                    await Task.WhenAll(tasks);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            await SetNextExecute(stoppingToken);
        }

        /// <summary>
        /// Sets the next execute.
        /// </summary>
        internal async Task SetNextExecute(CancellationToken stoppingToken)
        {
            if (timer != null)
            {
                await timer.DisposeAsync();
            }

            timer = null;
            if (_task.Delete)
            {
                logger.Info("Task is delete id {Guid}", _task.Id);
                return;
            }

            CrontabSchedule s = CrontabSchedule.Parse(_task.CronExpression, new CrontabSchedule.ParseOptions { IncludingSeconds = _task.CronWithSeconds });
            DateTime start = DateTime.Now;
            DateTime end = _task.End;
            DateTime next = s.GetNextOccurrence(start, end);
            if (next >= end)
            {
                logger.Info("Task is ended by date {Date} id {Guid}", _task.End, _task.Id);
                return;
            }
            SetUpTimer(next, stoppingToken);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
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

        /// <summary>
        /// Sets the cron data.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="stoppingToken">The stopping token.</param>
        /// <returns></returns>
        /// <exception cref="System.NotImplementedException"></exception>
        internal async Task SetCronData(TaskInfo item, CancellationToken stoppingToken)
        {
            _task = item;
            await SetNextExecute(stoppingToken);
        }
    }
}
