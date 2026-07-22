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
            // Доставка пробе — в фоне (ответ оператору не ждёт SetJobs).
            QueueBackgroundPush(ct => TryPushAsync(task.RequestInfo, [task], ct));
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
            // Рассылка пробам — в фоне: массовая заливка не должна держать HTTP-ответ,
            // пока все пробы примут пачки (медленная/недоступная проба зависла бы ответ).
            QueueBackgroundPush(ct => PushGroupedAsync(tasks, ct));
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
            QueueBackgroundPush(ct => TryPushAsync(task.RequestInfo, [task], ct)); // проба добавит задачу заново
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
            QueueBackgroundPush(ct => PushGroupedAsync(changed, ct));
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
            QueueBackgroundPush(ct => TryPushAsync(task.RequestInfo, [task], ct)); // отправляем удаление
        }

        /// <inheritdoc/>
        public async Task<bool> PurgeAsync(Guid id, CancellationToken cancellationToken)
        {
            TaskInfo? task = await _tasks.GetByIdAsync(id);
            if (task is null)
            {
                return false;
            }

            // Полностью стираем только уже удалённую задачу (двойная операция: сначала
            // «удалить» — потом «стереть»). Активную сначала помечаем удалённой.
            if (!task.Delete)
            {
                await DeleteAsync(id, cancellationToken);
            }

            _logger.Info("Полное удаление задачи {Id} из БД", id);

            // Best-effort снимаем задачу с пробы ПЕРЕД стиранием: иначе, если проба ещё
            // держит её, восстановление (усыновление сирот) вернуло бы задачу обратно.
            task.Delete = true;
            QueueBackgroundPush(ct => TryPushAsync(task.RequestInfo, [task], ct));

            await _tasks.RemoveAsync(id);
            _changeNotifier.Notify();
            return true;
        }

        /// <inheritdoc/>
        public async Task DeleteByRequestInfoAsync(string requestInfo, CancellationToken cancellationToken)
        {
            _logger.Info("Удаление всех задач пробы {RequestInfo}", requestInfo);
            await _tasks.MarkDeletedByRequestInfoAsync(requestInfo);
            _changeNotifier.Notify(); // задачи пробы удалены — событие для интерфейса

            IReadOnlyList<TaskInfo> all = await _tasks.GetByRequestInfoAsync(requestInfo);
            TaskInfo[] deleted = [.. all.Where(t => t.Delete)];
            QueueBackgroundPush(ct => TryPushAsync(requestInfo, deleted, ct));
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<Guid>> PurgeByRequestInfoAsync(string requestInfo)
        {
            IReadOnlyList<Guid> removed = await _tasks.RemoveByRequestInfoAsync(requestInfo);
            if (removed.Count > 0)
            {
                _logger.Info("Окончательно удалено задач пробы {RequestInfo}: {Count}", requestInfo, removed.Count);
                _changeNotifier.Notify();
            }
            return removed;
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

                if (await NeedsPushAsync(task, onProbe, now))
                {
                    toPush.Add(task);
                }
            }

            if (toPush.Count > 0)
            {
                _logger.Info("Синхронизация пробы {RequestInfo}: отправляем изменений {Count}", requestInfo, toPush.Count);
                await _probe.PushTasksAsync(requestInfo, toPush, cancellationToken);
                _changeNotifier.Notify(); // сверка изменила состояние задач
            }

            // Задачи-«сироты»: есть на пробе, но серверу неизвестны — сервер переустановили
            // с потерей данных. Не удаляем их, а ЗАБИРАЕМ с пробы и добавляем обратно на
            // сервер (восстановление). Тем самым чистый сервер перенимает задачи живой пробы.
            List<Guid> orphans = [.. onProbe.Where(id => !knownSchedulers.Contains(id))];
            if (orphans.Count > 0)
            {
                await AdoptFromProbeAsync(requestInfo, orphans, cancellationToken);
            }
        }

        /// <summary>
        /// Восстанавливает на сервере задачи, которые есть на пробе, но отсутствуют в БД
        /// сервера (после его переустановки с потерей данных). Полные определения задач
        /// забираются с пробы и добавляются в БД; удалённые (Delete) не усыновляются.
        /// </summary>
        private async Task AdoptFromProbeAsync(string requestInfo, IReadOnlyList<Guid> orphanIds, CancellationToken cancellationToken)
        {
            IReadOnlyList<TaskInfo> probeTasks;
            try
            {
                probeTasks = await _probe.GetTasksAsync(requestInfo, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Старая проба без эндпоинта Tasks или временная ошибка — не трогаем сироты
                // (лучше оставить как есть, чем потерять). Повторим на следующей сверке.
                _logger.Warn(ex, "Не удалось забрать задачи с пробы {RequestInfo} для восстановления", requestInfo);
                return;
            }

            HashSet<Guid> orphanSet = [.. orphanIds];
            List<TaskInfo> adopted = [.. probeTasks.Where(t =>
                orphanSet.Contains(t.Id) && !t.Delete && t.Type == TaskType.Scheduler)];

            if (adopted.Count > 0)
            {
                await _tasks.UpsertRangeAsync(adopted);
                _logger.Info(
                    "Восстановлено задач с пробы {RequestInfo}: {Count} (сервер был переустановлен, задачи перенесены с пробы)",
                    requestInfo, adopted.Count);
                _changeNotifier.Notify();
            }
        }

        /// <summary>
        /// Решает, нужно ли досылать пробе задачу по расписанию, и при необходимости
        /// помечает истёкшую задачу удалённой. Возвращает <c>true</c>, если задачу надо отправить.
        /// </summary>
        private async Task<bool> NeedsPushAsync(TaskInfo task, HashSet<Guid> onProbe, DateTime now)
        {
            // Уже помеченные на удаление — убрать с пробы, если она их ещё держит.
            if (task.Delete)
            {
                return onProbe.Contains(task.Id);
            }

            // Устаревшие (истекла дата окончания) — помечаем удалёнными и убираем с пробы.
            if (task.End <= now)
            {
                task.Delete = true;
                task.DeletedAt = now;
                await _tasks.UpsertAsync(task);
                return onProbe.Contains(task.Id);
            }

            // Активная задача, которой у пробы нет, — досылаем (так чистая проба получает всё).
            return !onProbe.Contains(task.Id);
        }

        /// <summary>
        /// Ставит доставку изменений пробам в ФОН: HTTP-ответ оператору не должен ждать
        /// SetJobs — проба может быть медленной или недоступной, а таймаут запроса к ней —
        /// до минуты. Недоставленное гарантированно досылает фоновая сверка. Токен запроса
        /// не используется (он отменится при возврате ответа) — доставка идёт своим ходом.
        /// </summary>
        private void QueueBackgroundPush(Func<CancellationToken, Task> push) =>
            _ = Task.Run(async () =>
            {
                try
                {
                    await push(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Фоновая доставка изменений пробе не удалась — досошлёт сверка");
                }
            });

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
