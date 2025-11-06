// Ignore Spelling: SPI Twamp

using NCrontab;
using Newtonsoft.Json;
using NLog;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Environment;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SPI.Twamp.Probe.Runners
{
    /// <summary>
    /// 
    /// </summary>
    internal class RepeatExecuter(Logger logger, TaskInfo task, IConfiguration configuration, ConcurrentBag<ActionData> bag, Action endJob)
    {
        private readonly IConfiguration _configuration = configuration;
        private readonly TaskInfo _task = task;
        private readonly Logger logger = logger;
        private readonly ConcurrentBag<ActionData> _answers = bag;
        private readonly Action _endJob = endJob;


        public async Task SomeMethodRunsAt(CancellationToken stoppingToken)
        {
            if (_task.Delete)
            {
                logger.Info("Task is delete id {Guid}", _task.Id);
                return;
            }

            logger.Info("Run job {Guid} at {Time}", _task.Id, DateTime.Now);
            try
            {

                string[] arr = _task.EndNode.Split([";", ","], StringSplitOptions.RemoveEmptyEntries);
                List<Task> tasks = [];
                foreach (string item in arr)
                {
                    tasks.Add(Task.Run(() => HostFunctions.DoWork(_task, _configuration, logger, _endJob, _answers, stoppingToken), stoppingToken));
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

        }


    }
}