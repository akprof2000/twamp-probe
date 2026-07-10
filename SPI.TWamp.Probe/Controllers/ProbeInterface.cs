// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Mvc;
using NLog;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using SPI.Twamp.Probe.Server;
using SPI.Twamp.Probe.Environment;
using System.ComponentModel.DataAnnotations;
using System.Net;

namespace SPI.Twamp.Probe.Controllers
{
    /// <summary>
    /// Веб-интерфейс зонда: приём задач и выдача результатов их выполнения.
    /// </summary>
    /// <seealso cref="ControllerBase" />
    [Route("api/[controller]")]
    [ApiController]
    public class ProbeInterface(Logger logger, Worker storage, IResultStore resultStore, ITaskRunRegistry runRegistry) : ControllerBase
    {
        private readonly Logger logger = logger;
        private readonly Worker storage = storage;
        private readonly IResultStore resultStore = resultStore;
        private readonly ITaskRunRegistry runRegistry = runRegistry;


        /// <summary>
        /// Регистрирует пробу и возвращает её идентификационные данные.
        /// </summary>
        /// <param name="requestInfo">Идентификатор запроса (адрес сервера).</param>
        [HttpPost("[action]")]
        public ActionResult<Identify> CheckIn([FromQuery][Required] string requestInfo)
        {
            ArgumentException.ThrowIfNullOrEmpty(requestInfo);
            logger.Info("Получен CheckIn {RequestInfo}", requestInfo);

            (string address, string name, string mac, string descr) = HostFunctions.GetFirstIPAddress();

            Identify res = new()
            {
                IPAddress = address,
                MacAddress = mac,
                HostName = Dns.GetHostName(),
                Description = descr,
                Title = name,
                RequestInfo = requestInfo,
                // Версия сборки — сервер показывает её в списке проб и может
                // обнаруживать устаревшие пробы после обновления.
                Version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? ""
            };
            logger.Info("Ответ CheckIn {@Identify}", res);

            return Ok(res);
        }

        /// <summary>
        /// Принимает от сервера изменившиеся задачи и сливает их в реестр пробы
        /// (инкрементально: добавление, обновление, удаление).
        /// </summary>
        /// <param name="jobs">Изменившиеся задачи (не обязательно полный список).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("[action]")]
        public async Task<ActionResult> SetJobs([FromBody][Required] TaskInfo[] jobs, CancellationToken cancellationToken)
        {
            logger.Info("Получено изменений задач: {Count}", jobs.Length);
            await storage.MergeJobs(jobs, cancellationToken);

            return Ok();
        }

        /// <summary>
        /// Возвращает идентификаторы задач по расписанию, известных пробе.
        /// Используется сервером для сверки состояния и досылки недостающих задач.
        /// </summary>
        [HttpGet("[action]")]
        public ActionResult<Guid[]> TaskIds()
        {
            return Ok(storage.GetKnownTaskIds());
        }

        /// <summary>
        /// Возвращает состояние выполнения задач на пробе: выполняется ли сейчас,
        /// когда был последний запуск/завершение, сколько раз выполнялась,
        /// ближайший запланированный запуск и последняя ошибка.
        /// </summary>
        [HttpGet("[action]")]
        public ActionResult<IReadOnlyList<TaskRunInfo>> TaskStatus()
        {
            return Ok(runRegistry.GetAll());
        }

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
            logger.Info("Запрос результатов зондирования");
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
