// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NLog;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Server;
using SPI.Twamp.Probe.Environment;
using System.ComponentModel.DataAnnotations;
using System.Net;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace SPI.Twamp.Probe.Controllers
{
    /// <summary>
    /// Веб-интерфейс зонда: приём задач и выдача результатов их выполнения.
    /// </summary>
    /// <seealso cref="ControllerBase" />
    [Route("api/[controller]")]
    [ApiController]
    public class ProbeInterface(Logger logger, Worker storage, IResultStore resultStore) : ControllerBase
    {
        private readonly Logger logger = logger;
        private readonly Worker storage = storage;
        private readonly IResultStore resultStore = resultStore;


        /// <summary>
        /// Sets the information client.
        /// </summary>
        /// <param name="requestInfo">The request information.</param>
        /// <returns></returns>
        [HttpPost("[action]")]
        public ActionResult<Identify> CheckIn([FromQuery][Required] string requestInfo)
        {
            ArgumentException.ThrowIfNullOrEmpty(requestInfo);
            logger.Info("Get check In", requestInfo);

            (string address, string name, string mac, string descr) = HostFunctions.GetFirstIPAddress();

            Identify res = new()
            {
                IPAddress = address,
                MacAddress = mac,
                HostName = Dns.GetHostName(),
                Description = descr,
                Title = name,
                RequestInfo = requestInfo
            };
            logger.Info("Check in answer is {@Identify}", res);

            return Ok(res);
        }

        /// <summary>
        /// Sets the information client.
        /// </summary>
        /// <param name="jobs">The jobs.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        [HttpPost("[action]")]
        public async Task<ActionResult> SetJobs([FromBody][Required] TaskInfo[] jobs, CancellationToken cancellationToken)
        {
            logger.Info("Set jobs {@ArrayTaskInfo}", jobs);
            await storage.PushData(JsonConvert.SerializeObject(jobs), cancellationToken);

            return Ok();
        }

        /// <summary>
        /// Возвращает накопленные результаты выполнения задач.
        /// Реализует «длинный опрос»: ждёт до 30 секунд появления новых данных,
        /// не блокируя поток пула, и отдаёт их пачкой.
        /// </summary>
        /// <param name="cancellationToken">Токен отмены (разрыв соединения клиентом).</param>
        /// <returns>Массив результатов (возможно пустой при таймауте).</returns>
        [HttpGet("[action]")]
        public async Task<ActionResult<ActionData[]>> CheckData(CancellationToken cancellationToken)
        {
            logger.Info("Запрос результатов зондирования");
            ActionData[] results = await resultStore.TakeBatchAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return Ok(results);
        }
    }
}
