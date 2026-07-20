// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Mvc;
using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using System.ComponentModel.DataAnnotations;

namespace SPI.Twamp.Server.Controllers
{
    /// <summary>
    /// Пробы и мониторинг: регистрация/подтверждение проб, состояние связи, статус
    /// выполнения задач, «длинный опрос» изменений. Часть API оператора (<c>api/userinterface</c>).
    /// </summary>
    [Route("api/userinterface")]
    [ApiController]
    public class ProbesController(
        Logger logger, ITaskService taskService, IClientService clientService,
        IProbeStatusProvider probeStatus, IProbeClient probeClient, IChangeNotifier changeNotifier)
        : ControllerBase
    {
        private readonly Logger _logger = logger;
        private readonly ITaskService _taskService = taskService;
        private readonly IClientService _clientService = clientService;
        private readonly IProbeStatusProvider _probeStatus = probeStatus;
        private readonly IProbeClient _probeClient = probeClient;
        private readonly IChangeNotifier _changeNotifier = changeNotifier;

        /// <summary>Возвращает список неопознанных проб (ожидающих подтверждения).</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult<IEnumerable<Identify>>> ListNotIdentifyClients()
        {
            _logger.Info("Запрос списка неопознанных проб");
            return Ok(await _clientService.GetUnidentifiedAsync());
        }

        /// <summary>Возвращает список подтверждённых проб.</summary>
        [HttpGet("[action]")]
        public async Task<ActionResult<IEnumerable<Client>>> ListClients()
        {
            _logger.Info("Запрос списка проб");
            return Ok(await _clientService.GetClientsAsync());
        }

        /// <summary>Подтверждает пробу и запускает её опрос.</summary>
        /// <param name="client">Данные пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("[action]")]
        public async Task<ActionResult> SetInfoClient([FromBody][Required] Client client, CancellationToken cancellationToken)
        {
            await _clientService.SetInfoAsync(client, cancellationToken);
            return Ok();
        }

        /// <summary>Регистрирует пробу (CheckIn) по её адресу.</summary>
        /// <param name="client">Базовый адрес пробы.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("[action]")]
        public async Task<ActionResult> CheckIn([FromQuery][Required] string client, CancellationToken cancellationToken)
        {
            await _clientService.CheckInAsync(client, cancellationToken);
            return Ok();
        }

        /// <summary>
        /// Удаляет подтверждённую пробу: останавливает опрос, убирает из списка;
        /// при <paramref name="deleteTasks"/>=true помечает удалёнными и все её задачи.
        /// </summary>
        /// <param name="requestInfo">Адрес пробы (RequestInfo).</param>
        /// <param name="deleteTasks">Удалить ли вместе с пробой все её задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpDelete("clients")]
        public async Task<ActionResult> DeleteClient(
            [FromQuery][Required] string requestInfo, [FromQuery] bool deleteTasks = false,
            CancellationToken cancellationToken = default)
        {
            bool removed = await _clientService.DeleteAsync(requestInfo, deleteTasks, cancellationToken);
            return removed ? Ok() : NotFound($"Проба «{requestInfo}» не найдена");
        }

        /// <summary>
        /// Отклоняет неопознанную пробу — убирает её из очереди на подтверждение.
        /// Пустой адрес допустим: так вычищаются битые записи без RequestInfo.
        /// </summary>
        /// <param name="requestInfo">Адрес пробы (RequestInfo); пусто — записи без адреса.</param>
        [HttpDelete("unidentified")]
        public async Task<ActionResult> RejectUnidentified([FromQuery] string? requestInfo = null)
        {
            await _clientService.RejectUnidentifiedAsync(requestInfo ?? "");
            return Ok();
        }

        /// <summary>
        /// «Длинный опрос» изменений для веб-интерфейса: висит до 25 секунд и отвечает
        /// сразу, как только на сервере изменились задачи, результаты или состояние проб.
        /// Клиент передаёт последнюю известную версию и обновляет экран при её росте —
        /// это события вместо периодического поллинга, ожидание не держит поток.
        /// </summary>
        /// <param name="version">Версия состояния, известная клиенту (0 — первая загрузка).</param>
        /// <param name="cancellationToken">Токен отмены (разрыв соединения клиентом).</param>
        /// <returns>Актуальная версия состояния.</returns>
        [HttpGet("[action]")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult> WaitChanges([FromQuery] long version = 0, CancellationToken cancellationToken = default)
        {
            long current = await _changeNotifier.WaitAsync(version, TimeSpan.FromSeconds(25), cancellationToken);
            return Ok(new { Version = current });
        }

        /// <summary>
        /// Возвращает последние результаты по задачам (момент и признак ошибки) —
        /// колонка «последний результат» в списке задач. Заполняется по мере
        /// поступления результатов после старта сервера.
        /// </summary>
        [HttpGet("[action]")]
        public ActionResult LastResults()
        {
            IReadOnlyDictionary<Guid, TaskLastResult> results = _probeStatus.GetLastResults();
            return Ok(results.Select(kv => new
            {
                TaskId = kv.Key,
                kv.Value.Time,
                kv.Value.HasError,
                kv.Value.Outcome,
                kv.Value.ExitCode,
                kv.Value.Error
            }));
        }

        /// <summary>
        /// Проксирует состояние выполнения задач с пробы: запущена ли задача сейчас,
        /// последний старт/завершение, счётчик выполнений, ближайший запуск, ошибка.
        /// Отвечает на вопрос «запустились ли задачи», не дожидаясь первых результатов.
        /// </summary>
        /// <param name="probe">Адрес пробы (RequestInfo).</param>
        /// <param name="skip">Сколько записей пропустить.</param>
        /// <param name="take">Размер страницы (максимум 500).</param>
        /// <param name="title">Фильтр по названию задачи (содержит).</param>
        /// <param name="outcome">Фильтр по исходу запуска.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpGet("[action]")]
        public async Task<ActionResult> ProbeTaskStatus(
            [FromQuery][Required] string probe,
            [FromQuery] int skip = 0, [FromQuery] int take = 100,
            [FromQuery] string? title = null, [FromQuery] string? outcome = null,
            CancellationToken cancellationToken = default)
        {
            // Пагинация и фильтры выполняются на пробе — сервер лишь проксирует ответ.
            string query = $"skip={skip}&take={take}" +
                (string.IsNullOrEmpty(title) ? "" : $"&title={Uri.EscapeDataString(title)}") +
                (string.IsNullOrEmpty(outcome) ? "" : $"&outcome={Uri.EscapeDataString(outcome)}");
            string json = await _probeClient.GetTaskStatusRawAsync(probe, query, cancellationToken);
            return Content(json, "application/json");
        }

        /// <summary>
        /// Возвращает состояние всех проб: связь (последний успешный опрос, ошибки,
        /// backoff), версию пробы и число её задач — для страницы мониторинга.
        /// </summary>
        [HttpGet("[action]")]
        public async Task<ActionResult> ProbeStatus()
        {
            IReadOnlyList<Client> clients = await _clientService.GetClientsAsync();
            IReadOnlyDictionary<string, ProbePollState> states = _probeStatus.GetStates();
            IReadOnlyList<TaskInfo> allTasks = await _taskService.GetAllAsync();

            // Считаем задачи по пробам за один проход (O(задачи)), а не перебором всех
            // задач для каждой пробы (O(пробы × задачи)) — важно при сотнях проб и
            // десятках тысяч задач, тем более что статус перечитывается по событиям.
            Dictionary<string, (int Active, int Deleted)> taskCounts = [];
            foreach (TaskInfo task in allTasks)
            {
                (int active, int deleted) = taskCounts.GetValueOrDefault(task.RequestInfo);
                taskCounts[task.RequestInfo] = task.Delete ? (active, deleted + 1) : (active + 1, deleted);
            }

            var status = clients.Select(c =>
            {
                ProbePollState? state = states.TryGetValue(c.RequestInfo, out ProbePollState? s) ? s : null;
                (int activeTasks, int deletedTasks) = taskCounts.GetValueOrDefault(c.RequestInfo);
                return new
                {
                    c.RequestInfo,
                    c.Name,
                    c.HostName,
                    c.IPAddress,
                    c.Version,
                    LastSuccess = state?.LastSuccess,
                    LastError = state?.LastError,
                    LastErrorMessage = state?.LastErrorMessage,
                    BackoffSeconds = state?.BackoffSeconds ?? 0,
                    TotalResults = state?.TotalResults ?? 0,
                    ActiveTasks = activeTasks,
                    DeletedTasks = deletedTasks
                };
            });

            return Ok(status);
        }
    }
}
