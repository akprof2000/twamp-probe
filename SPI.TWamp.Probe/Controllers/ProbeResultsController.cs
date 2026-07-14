// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Mvc;
using NLog;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using System.ComponentModel.DataAnnotations;

namespace SPI.Twamp.Probe.Controllers
{
    /// <summary>
    /// Выдача результатов зондирования с гарантированной доставкой: «длинный опрос»
    /// пачки и её подтверждение. Часть API пробы (<c>api/probeinterface</c>).
    /// </summary>
    [Route("api/probeinterface")]
    [ApiController]
    public class ProbeResultsController(Logger logger, IResultStore resultStore) : ControllerBase
    {
        private readonly Logger logger = logger;
        private readonly IResultStore resultStore = resultStore;

        /// <summary>
        /// Возвращает накопленные результаты пачкой с идентификатором для подтверждения.
        /// Реализует «длинный опрос»: ждёт до 30 секунд появления новых данных, не блокируя
        /// поток пула. Пачка хранится пробой до подтверждения через ConfirmData —
        /// при потере связи она будет выдана повторно.
        /// </summary>
        /// <param name="cancellationToken">Токен отмены (разрыв соединения клиентом).</param>
        /// <returns>Пачка результатов (пустая при таймауте).</returns>
        [HttpGet("[action]")]
        public async Task<ActionResult<ResultBatch>> CheckData(CancellationToken cancellationToken)
        {
            // Debug, а не Info: длинный опрос идёт непрерывно, и на каждый цикл
            // не должно приходиться записи в файловый лог.
            logger.Debug("Запрос результатов зондирования");
            ResultBatch batch = await resultStore.TakeBatchAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return Ok(batch);
        }

        /// <summary>
        /// Подтверждает доставку пачки результатов: сервер записал её в БД,
        /// и проба может удалить данные.
        /// </summary>
        /// <param name="batchId">Идентификатор пачки из ответа CheckData.</param>
        /// <returns><c>true</c>, если пачка найдена и удалена.</returns>
        [HttpPost("[action]")]
        public async Task<ActionResult<bool>> ConfirmData([FromQuery][Required] Guid batchId)
        {
            bool confirmed = await resultStore.ConfirmAsync(batchId);
            logger.Debug("Подтверждение пачки {BatchId}: {Confirmed}", batchId, confirmed);
            return Ok(confirmed);
        }
    }
}
