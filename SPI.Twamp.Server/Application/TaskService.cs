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
    public sealed class TaskService(
        Logger logger, ITaskRepository tasks, IProbeClient probe, IChangeNotifier changeNotifier) : ITaskService
    {
        private readonly Logger _logger = logger;
        private readonly ITaskRepository _tasks = tasks;
        private readonly IProbeClient _probe = probe;
        private readonly IChangeNotifier _changeNotifier = changeNotifier;

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
            _changeNotifier.Notify(); // список задач изменился — событие для интерфейса
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
            _changeNotifier.Notify(); // список задач изменился — событие для интерфейса
            await PushGroupedAsync(tasks, cancellationToken);
        }

        /// <summary>
        /// Рассылает задачи пробам пакетами: группировка по адресу пробы, один SetJobs
        /// на пачку до <see cref="PushBatchSize"/> задач. Отправка best-effort —
        /// недоставленное досошлёт фоновая сверка.
        /// </summary>
        private async Task PushGroupedAsync(IReadOnlyList<TaskInfo> tasks, CancellationToken cancellationToken)
        {
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
        public async Task<bool> RestoreAsync(Guid id, CancellationToken cancellationToken)
        {
            TaskInfo? task = await _tasks.GetByIdAsync(id);
            if (task is null || !task.Delete)
            {
                return false;
            }

            _logger.Info("Восстановление задачи {Id}", id);
            task.Delete = false;
            task.DeletedAt = null;
            await _tasks.UpsertAsync(task);
            _changeNotifier.Notify();
            await TryPushAsync(task.RequestInfo, [task], cancellationToken); // проба добавит задачу заново
            return true;
        }

        /// <inheritdoc/>
        public async Task<int> SetDeletedManyAsync(IReadOnlyList<TaskInfo> tasks, bool deleted, CancellationToken cancellationToken)
        {
            // Меняем только те задачи, чьё состояние действительно отличается.
            List<TaskInfo> changed = [.. tasks.Where(t => t.Delete != deleted)];
            if (changed.Count == 0)
            {
                return 0;
            }

            DateTime now = DateTime.Now;
            foreach (TaskInfo task in changed)
            {
                task.Delete = deleted;
                task.DeletedAt = deleted ? now : null;
            }

            _logger.Info("Массовое {Action} задач: {Count}",
                deleted ? "удаление" : "восстановление", changed.Count);
            await _tasks.UpsertRangeAsync(changed);
            _changeNotifier.Notify();
            await PushGroupedAsync(changed, cancellationToken);
            return changed.Count;
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
            task.DeletedAt = DateTime.Now; // для фоновой очистки давно удалённых
            await _tasks.UpsertAsync(task);
            _changeNotifier.Notify(); // задача удалена — событие для интерфейса
            await TryPushAsync(task.RequestInfo, [task], cancellationToken); // отправляем удаление
        }

        /// <inheritdoc/>
        public async Task DeleteByRequestInfoAsync(string requestInfo, CancellationToken cancellationToken)
        {
            _logger.Info("Удаление всех задач пробы {RequestInfo}", requestInfo);
            await _tasks.MarkDeletedByRequestInfoAsync(requestInfo);
            _changeNotifier.Notify(); // задачи пробы удалены — событие для интерфейса

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
            HashSet<Guid> knownSchedulers = []; // задачи по расписанию, известные серверу

            foreach (TaskInfo task in all)
            {
                // Разовые задачи не синхронизируем: они выполняются один раз при добавлении.
                if (task.Type != TaskType.Scheduler)
                {
                    continue;
                }

                _ = knownSchedulers.Add(task.Id);

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
                    task.DeletedAt = now;
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

            // Задачи-«сироты»: есть на пробе, но серверу неизвестны (например, БД сервера
            // восстановили из резервной копии или задачу уже вычистила ретенция).
            // Отправляем пробе заглушки на удаление.
            foreach (Guid orphan in onProbe.Where(id => !knownSchedulers.Contains(id)))
            {
                toPush.Add(new TaskInfo
                {
                    Id = orphan,
                    RequestInfo = requestInfo,
                    Type = TaskType.Scheduler,
                    Delete = true
                });
            }

            if (toPush.Count > 0)
            {
                _logger.Info("Синхронизация пробы {RequestInfo}: отправляем изменений {Count}", requestInfo, toPush.Count);
                await _probe.PushTasksAsync(requestInfo, toPush, cancellationToken);
                _changeNotifier.Notify(); // сверка изменила состояние задач
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
