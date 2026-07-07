// Ignore Spelling: SPI Twamp

using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Реализация <see cref="ITaskService"/>: изменение задач в хранилище с последующей
    /// синхронизацией полного списка задач пробы на саму пробу.
    /// </summary>
    public sealed class TaskService(Logger logger, ITaskRepository tasks, IProbeClient probe) : ITaskService
    {
        private readonly Logger _logger = logger;
        private readonly ITaskRepository _tasks = tasks;
        private readonly IProbeClient _probe = probe;

        /// <inheritdoc/>
        public Task<IReadOnlyList<TaskInfo>> GetAllAsync() => _tasks.GetAllAsync();

        /// <inheritdoc/>
        public Task<IReadOnlyList<TaskInfo>> GetByRequestInfoAsync(string requestInfo) =>
            _tasks.GetByRequestInfoAsync(requestInfo);

        /// <inheritdoc/>
        public async Task AddAsync(TaskInfo task, CancellationToken cancellationToken)
        {
            _logger.Info("Сохранение задачи {@Task}", task);
            await _tasks.UpsertAsync(task);
            await SyncProbeAsync(task.RequestInfo, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            TaskInfo? task = await _tasks.GetByIdAsync(id);
            if (task is null)
            {
                _logger.Warn("Задача {Id} не найдена — удаление пропущено", id);
                return;
            }

            _logger.Info("Удаление задачи {Id}", id);
            task.Delete = true;
            await _tasks.UpsertAsync(task);
            await SyncProbeAsync(task.RequestInfo, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task DeleteByRequestInfoAsync(string requestInfo, CancellationToken cancellationToken)
        {
            _logger.Info("Удаление всех задач пробы {RequestInfo}", requestInfo);
            await _tasks.MarkDeletedByRequestInfoAsync(requestInfo);
            await SyncProbeAsync(requestInfo, cancellationToken);
        }

        /// <summary>Отправляет пробе её актуальный список задач.</summary>
        private async Task SyncProbeAsync(string requestInfo, CancellationToken cancellationToken)
        {
            IReadOnlyList<TaskInfo> current = await _tasks.GetByRequestInfoAsync(requestInfo);
            await _probe.PushTasksAsync(requestInfo, current, cancellationToken);
        }
    }
}
