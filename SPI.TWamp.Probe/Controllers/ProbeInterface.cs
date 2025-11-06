// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NLog;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Server;
using SPI.Twamp.Probe.Environment;
using System.ComponentModel.DataAnnotations;
using System.Net;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace SPI.Twamp.Probe.Controllers
{
    /// <summary>
    /// 
    /// </summary>
    /// <seealso cref="ControllerBase" />
    [Route("api/[controller]")]
    [ApiController]
    public class ProbeInterface(Logger logger, Worker storage) : ControllerBase
    {
        private readonly Logger logger = logger;
        private readonly Worker storage = storage;


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
        /// Checks the data.
        /// </summary>
        /// <returns></returns>
        [HttpGet("[action]")]
        public async Task<ActionResult<ActionData[]>> CheckData(CancellationToken cancellationToken)
        {
            logger.Info("Check data execute");
            return Ok(await storage.GetActionsData(cancellationToken));
        }
    }
}
