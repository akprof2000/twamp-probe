// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Mvc;
using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using System.ComponentModel.DataAnnotations;

namespace SPI.Twamp.Server.Controllers
{
    /// <summary>
    /// Управление задачами: CRUD, страница с фильтрами и серверной пагинацией,
    /// массовые операции. Часть API оператора (<c>api/userinterface</c>).
    /// Контроллер тонкий — вся логика в прикладных сервисах.
    /// </summary>
    [Route("api/userinterface")]
    [ApiController]
    public class TasksController(
        Logger logger, ITaskService taskService, IProbeStatusProvider probeStatus)
        : ControllerBase
    {
        private readonly Logger _logger = logger;
        private readonly ITaskService _taskService = taskService;
        private readonly IProbeStatusProvider _probeStatus = probeStatus;

        /// <summary>Возвращает полный список задач.</summary>
        [HttpGet("tasks")]
        public async Task<ActionResult<IEnumerable<TaskInfo>>> Get()
        {
            _logger.Info("Запрос полного списка задач");
            return Ok(await _taskService.GetAllAsync());
        }

        /// <summary>Возвращает задачи указанной пробы.</summary>
        /// <param name="requestInfo">Адрес пробы (RequestInfo).</param>
        [HttpGet("tasks/{RequestInfo}")]
        public async Task<ActionResult<IEnumerable<TaskInfo>>> Get([Required] string requestInfo)
        {
            _logger.Info("Запрос задач пробы {RequestInfo}", requestInfo);
            return Ok(await _taskService.GetByRequestInfoAsync(requestInfo));
        }

        /// <summary>Создаёт или обновляет задачу.</summary>
        /// <param name="task">Описание задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("tasks")]
        public async Task<ActionResult> PostAsync([FromBody][Required] TaskInfo task, CancellationToken cancellationToken)
        {
            if (task.End <= DateTime.Now)
            {
                _logger.Warn("Задача {@Task} имеет некорректную дату окончания", task);
                return BadRequest("Дата окончания не может быть раньше текущего момента");
            }

            await _taskService.AddAsync(task, cancellationToken);
            return Ok();
        }

        /// <summary>Удаляет задачу по идентификатору.</summary>
        /// <param name="id">Идентификатор задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpDelete("tasks/{id}")]
        public async Task<ActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            await _taskService.DeleteAsync(id, cancellationToken);
            return Ok();
        }

        /// <summary>Восстанавливает удалённую задачу (снимает пометку удаления).</summary>
        /// <param name="id">Идентификатор задачи.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpPost("tasks/{id}/restore")]
        public async Task<ActionResult> RestoreAsync(Guid id, CancellationToken cancellationToken)
        {
            bool restored = await _taskService.RestoreAsync(id, cancellationToken);
            return restored ? Ok() : NotFound("Задача не найдена или не была удалена");
        }

        /// <summary>Удаляет все задачи пробы по её адресу.</summary>
        /// <param name="IPAddress">Адрес пробы (RequestInfo).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        [HttpDelete("tasks")]
        public async Task<ActionResult> DeleteByIPAsync([FromQuery][Required] string IPAddress, CancellationToken cancellationToken)
        {
            await _taskService.DeleteByRequestInfoAsync(IPAddress, cancellationToken);
            return Ok();
        }

        /// <summary>
        /// Страница списка задач с фильтрами по всем столбцам и серверной пагинацией —
        /// интерфейс не загружает десятки тысяч задач целиком.
        /// </summary>
        /// <param name="skip">Сколько задач пропустить.</param>
        /// <param name="take">Размер страницы (максимум 500).</param>
        /// <param name="filter">Фильтры по столбцам (см. <see cref="TaskFilter"/>).</param>
        [HttpGet("[action]")]
        public async Task<ActionResult> TasksPage(
            [FromQuery] int skip = 0, [FromQuery] int take = 100, [FromQuery] TaskFilter? filter = null)
        {
            take = Math.Clamp(take, 1, 500);
            List<(TaskInfo Task, TaskLastResult? Last)> filtered = await FilterTasksAsync(filter ?? new TaskFilter());

            var items = filtered
                .OrderBy(x => x.Task.Title, StringComparer.OrdinalIgnoreCase)
                .Skip(Math.Max(0, skip)).Take(take)
                .Select(x => new
                {
                    x.Task.Id,
                    x.Task.Title,
                    x.Task.RequestInfo,
                    x.Task.EndNode,
                    Type = x.Task.Type.ToString(),
                    Mode = x.Task.Mode.ToString(),
                    x.Task.CronExpression,
                    x.Task.End,
                    x.Task.TimeoutSec,
                    x.Task.Delete,
                    Last = x.Last is null ? null : new
                    {
                        x.Last.Time,
                        x.Last.HasError,
                        x.Last.Outcome,
                        x.Last.ExitCode,
                        x.Last.Error
                    }
                });

            // Счётчики для кнопок массовых операций: сколько в выборке реально можно
            // удалить (активных) и восстановить (удалённых). По ним интерфейс скрывает
            // ненужную кнопку, когда действовать не над чем.
            int activeTotal = filtered.Count(x => !x.Task.Delete);
            int deletedTotal = filtered.Count(x => x.Task.Delete);

            return Ok(new { Total = filtered.Count, ActiveTotal = activeTotal, DeletedTotal = deletedTotal, Items = items });
        }

        /// <summary>
        /// Массовая операция над отфильтрованным списком задач: удаление всех
        /// совпавших с фильтром (action=delete) или восстановление (action=restore).
        /// Фильтры те же, что у <see cref="TasksPage"/>, — «удалить отфильтрованное одним нажатием».
        /// </summary>
        /// <param name="action">delete — пометить удалёнными; restore — восстановить.</param>
        /// <param name="filter">Фильтры по столбцам (см. <see cref="TaskFilter"/>).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Число изменённых задач.</returns>
        [HttpPost("[action]")]
        public async Task<ActionResult> TasksBulk(
            [FromQuery][Required] string action,
            [FromQuery] TaskFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            bool delete = action.Equals("delete", StringComparison.OrdinalIgnoreCase);
            if (!delete && !action.Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("action должен быть delete или restore");
            }

            List<(TaskInfo Task, TaskLastResult? Last)> filtered = await FilterTasksAsync(filter ?? new TaskFilter());

            int affected = await _taskService.SetDeletedManyAsync(
                [.. filtered.Select(x => x.Task)], delete, cancellationToken);

            _logger.Info("Массовая операция {Action}: изменено {Count} задач", action, affected);
            return Ok(new { Affected = affected });
        }

        /// <summary>Возвращает отфильтрованные задачи вместе с их последними результатами.</summary>
        private async Task<List<(TaskInfo Task, TaskLastResult? Last)>> FilterTasksAsync(TaskFilter filter)
        {
            IReadOnlyList<TaskInfo> all = await _taskService.GetAllAsync();
            IReadOnlyDictionary<Guid, TaskLastResult> lastResults = _probeStatus.GetLastResults();

            List<(TaskInfo, TaskLastResult?)> filtered = [];
            foreach (TaskInfo t in all)
            {
                TaskLastResult? last = lastResults.TryGetValue(t.Id, out TaskLastResult? lr) ? lr : null;
                if (MatchesFilter(t, last, filter))
                {
                    filtered.Add((t, last));
                }
            }
            return filtered;
        }

        /// <summary>
        /// Проверяет соответствие задачи фильтрам списка (все текстовые фильтры —
        /// «содержит», без учёта регистра).
        /// </summary>
        private static bool MatchesFilter(TaskInfo t, TaskLastResult? last, TaskFilter f)
        {
            static bool Has(string source, string? term) =>
                string.IsNullOrEmpty(term) || source.Contains(term, StringComparison.OrdinalIgnoreCase);

            if (!Has(t.Title, f.Title) || !Has(t.RequestInfo, f.Probe) || !Has(t.EndNode, f.Node))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(f.Type) && !t.Type.ToString().Equals(f.Type, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.IsNullOrEmpty(f.Mode) && !t.Mode.ToString().Equals(f.Mode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (f.Status == "active" && t.Delete)
            {
                return false;
            }
            if (f.Status == "deleted" && !t.Delete)
            {
                return false;
            }
            return MatchesResult(last, f.Outcome, f.Error);
        }

        /// <summary>Проверяет фильтры по исходу последнего запуска и тексту ошибки.</summary>
        private static bool MatchesResult(TaskLastResult? last, string? outcome, string? error)
        {
            if (!string.IsNullOrEmpty(outcome) &&
                !ResolveOutcome(last).Equals(outcome, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(error) &&
                (last?.Error is null || !last.Error.Contains(error, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            return true;
        }

        /// <summary>Исход последнего запуска для фильтра: «none» — данных нет, иначе Outcome/error/Success.</summary>
        private static string ResolveOutcome(TaskLastResult? last)
        {
            if (last is null)
            {
                return "none";
            }
            return last.Outcome ?? (last.HasError ? "error" : "Success");
        }
    }
}
