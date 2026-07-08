// Ignore Spelling: SPI Twamp

using NLog;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Реализация <see cref="ITaskService"/>: изменения задач в хранилище с инкрементальной
    /// доставкой пробе и фоновой сверкой состояния.
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

        /// <summary>Максимальный размер одной пачки задач, отправляемой пробе за раз.</summary>
        private const int PushBatchSize = 1000;

        /// <inheritdoc/>
        public async Task AddAsync(TaskInfo task, CancellationToken cancellationToken)
        {
            _logger.Info("Сохранение задачи {@Task}", task);
            await _tasks.UpsertAsync(task);
            // Передаём пробе только изменившуюся задачу. Если проба недоступна —
            // ничего страшного: недостающее досошлёт фоновая сверка.
            await TryPushAsync(task.RequestInfo, [task], cancellationToken);
        }

        /// <inheritdoc/>
        public async Task AddRangeAsync(IReadOnlyList<TaskInfo> tasks, CancellationToken cancellationToken)
        {
            if (tasks.Count == 0)
            {
                return;
            }

            _logger.Info("Массовое сохранение задач: {Count}", tasks.Count);
            await _tasks.UpsertRangeAsync(tasks);

            // Группируем по пробе и отправляем большими пачками: один HTTP-запрос
            // на пачку вместо запроса на каждую задачу. Отправка best-effort —
            // недоставленное досошлёт фоновая сверка.
            foreach (IGrouping<string, TaskInfo> group in tasks.GroupBy(t => t.RequestInfo))
            {
                TaskInfo[] all = [.. group];
                for (int offset = 0; offset < all.Length; offset += PushBatchSize)
                {
                    TaskInfo[] chunk = all[offset..Math.Min(offset + PushBatchSize, all.Length)];
                    await TryPushAsync(group.Key, chunk, cancellationToken);
                }
            }
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
            await TryPushAsync(task.RequestInfo, [task], cancellationToken); // отправляем удаление
        }

        /// <inheritdoc/>
        public async Task DeleteByRequestInfoAsync(string requestInfo, CancellationToken cancellationToken)
        {
            _logger.Info("Удаление всех задач пробы {RequestInfo}", requestInfo);
            await _tasks.MarkDeletedByRequestInfoAsync(requestInfo);

            IReadOnlyList<TaskInfo> all = await _tasks.GetByRequestInfoAsync(requestInfo);
            TaskInfo[] deleted = [.. all.Where(t => t.Delete)];
            await TryPushAsync(requestInfo, deleted, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task ReconcileAsync(string requestInfo, CancellationToken cancellationToken)
        {
            // Что проба знает прямо сейчас (задачи по расписанию).
            Guid[] probeIds = await _probe.GetTaskIdsAsync(requestInfo, cancellationToken);
            HashSet<Guid> onProbe = [.. probeIds];

            IReadOnlyList<TaskInfo> all = await _tasks.GetByRequestInfoAsync(requestInfo);
            DateTime now = DateTime.Now;
            List<TaskInfo> toPush = [];

            foreach (TaskInfo task in all)
            {
                // Разовые задачи не синхронизируем: они выполняются один раз при добавлении.
                if (task.Type != TaskType.Scheduler)
                {
                    continue;
                }

                // Уже помеченные на удаление — убрать с пробы, если она их ещё держит.
                if (task.Delete)
                {
                    if (onProbe.Contains(task.Id))
                    {
                        toPush.Add(task);
                    }
                    continue;
                }

                // Устаревшие (истекла дата окончания) — помечаем удалёнными и убираем с пробы.
                if (task.End <= now)
                {
                    task.Delete = true;
                    await _tasks.UpsertAsync(task);
                    if (onProbe.Contains(task.Id))
                    {
                        toPush.Add(task);
                    }
                    continue;
                }

                // Активная задача, которой у пробы нет, — досылаем (так чистая проба получает всё).
                if (!onProbe.Contains(task.Id))
                {
                    toPush.Add(task);
                }
            }

            if (toPush.Count > 0)
            {
                _logger.Info("Синхронизация пробы {RequestInfo}: отправляем изменений {Count}", requestInfo, toPush.Count);
                await _probe.PushTasksAsync(requestInfo, toPush, cancellationToken);
            }
        }

        /// <summary>Пытается передать пробе изменения; при недоступности пробы только логирует.</summary>
        private async Task TryPushAsync(string requestInfo, IReadOnlyList<TaskInfo> changed, CancellationToken cancellationToken)
        {
            if (changed.Count == 0)
            {
                return;
            }

            try
            {
                await _probe.PushTasksAsync(requestInfo, changed, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Проба {RequestInfo} недоступна — изменения досошлёт фоновая сверка", requestInfo);
            }
        }
    }
}
