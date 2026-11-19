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
    public class StorageController(SPI.Twamp.Server.Infrastructure.CollectorRegistry collectors) : ControllerBase
    {
        /// <summary>Возвращает состояние ClickHouse и буфера съёмника.</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult<ClickHouseState>> ClickHouseStatus(CancellationToken ct, string? collectorId = null)
        {
            try
            {
                using var client = await collectors.ConnectAsync(collectorId);
                return Ok(await client.GetFromJsonAsync<ClickHouseState>("storage", ct));
            }
            catch (HttpRequestException) { return StatusCode(503, "Съёмник недоступен"); }
        }
    }
}
