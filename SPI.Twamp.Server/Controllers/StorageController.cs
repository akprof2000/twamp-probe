// Ignore Spelling: SPI Twamp Clickhouse

using Microsoft.AspNetCore.Mvc;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Controllers
{
    /// <summary>
    /// Состояние хранилища измерений: буфер результатов и перенос их в ClickHouse.
    /// </summary>
    [ApiController]
    [Route("api/userinterface")]
    public class StorageController(IClickHouseStatusProvider status) : ControllerBase
    {
        private readonly IClickHouseStatusProvider _status = status;

        /// <summary>
        /// Возвращает состояние переноса результатов в ClickHouse: объёмы очереди,
        /// сколько уже выгружено, доступность базы и текст последней ошибки.
        /// </summary>
        [HttpGet("[action]")]
        public ActionResult<ClickHouseState> ClickHouseStatus() => Ok(_status.GetState());
    }
}
