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
    /// Приём задач и статус их выполнения: регистрация пробы, инкрементальное слияние
    /// задач, сверка идентификаторов и состояние выполнения. Часть API пробы
    /// (<c>api/probeinterface</c>).
    /// </summary>
    [Route("api/probeinterface")]
    [ApiController]
    public class ProbeJobsController(Logger logger, Worker storage, ITaskRunRegistry runRegistry) : ControllerBase
    {
        private readonly Logger logger = logger;
        private readonly Worker storage = storage;
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
        /// Возвращает полные определения задач по расписанию, которые держит проба.
        /// Сервер забирает их для восстановления своей БД после потери данных.
        /// </summary>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpGet("[action]")]
        public async Task<ActionResult<TaskInfo[]>> Tasks(CancellationToken cancellationToken)
        {
            return Ok(await storage.GetTasksAsync(cancellationToken));
        }

        /// <summary>
        /// Возвращает состояние выполнения задач на пробе: выполняется ли сейчас,
        /// когда был последний запуск/завершение, сколько раз выполнялась,
        /// ближайший запланированный запуск и последняя ошибка.
        /// Поддерживает фильтры и постраничную выдачу — задач может быть более 10 000.
        /// </summary>
        /// <param name="skip">Сколько записей пропустить.</param>
        /// <param name="take">Размер страницы (максимум 500).</param>
        /// <param name="title">Фильтр по названию задачи (содержит).</param>
        /// <param name="outcome">Фильтр по исходу: Success / ExitCodeError / TimedOut / StartFailed / Running / NotStarted.</param>
        [HttpGet("[action]")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult TaskStatus(
            [FromQuery] int skip = 0, [FromQuery] int take = 100,
            [FromQuery] string? title = null, [FromQuery] string? outcome = null)
        {
            take = Math.Clamp(take, 1, 500);

            IEnumerable<TaskRunInfo> query = runRegistry.GetAll();
            if (!string.IsNullOrEmpty(title))
            {
                query = query.Where(t => t.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(outcome))
            {
                query = query.Where(t =>
                    (outcome.Equals("Running", StringComparison.OrdinalIgnoreCase) && t.Running > 0) ||
                    t.LastOutcome.ToString().Equals(outcome, StringComparison.OrdinalIgnoreCase));
            }

            List<TaskRunInfo> filtered = [.. query];
            // Сначала выполняющиеся и проблемные, затем по названию.
            filtered.Sort((a, b) =>
            {
                int byRunning = b.Running.CompareTo(a.Running);
                if (byRunning != 0)
                {
                    return byRunning;
                }
                static bool Bad(RunOutcome o) => o is RunOutcome.ExitCodeError or RunOutcome.StartFailed or RunOutcome.TimedOut;
                int byBad = Bad(b.LastOutcome).CompareTo(Bad(a.LastOutcome));
                return byBad != 0 ? byBad : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });

            return Ok(new
            {
                Total = filtered.Count,
                Items = filtered.Skip(Math.Max(0, skip)).Take(take)
            });
        }
    }
}
