// Ignore Spelling: SPI Twamp

using System.Collections.Concurrent;

namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Реестр активных запусков зондов: позволяет оборвать выполнение задачи прямо
    /// в процессе, а не ждать её естественного завершения.
    /// <para>
    /// Зачем: задача вроде <c>twping -c 300 -i 1</c> живёт минутами. Если её удалили
    /// на сервере, недостаточно снять расписание — уже запущенный процесс зонда надо
    /// завершить принудительно, иначе он продолжит мерить и занимать ресурсы ещё
    /// несколько минут после удаления.
    /// </para>
    /// </summary>
    public sealed class RunCancelRegistry
    {
        /// <summary>Активные запуски: задача → (номер запуска → источник отмены).</summary>
        private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<long, CancellationTokenSource>> _active = new();

        /// <summary>Счётчик номеров запусков.</summary>
        private long _nextId;

        /// <summary>
        /// Регистрирует запуск задачи. Возвращаемый объект снимает запуск с учёта
        /// при освобождении — исполнителю достаточно обернуть его в <c>using</c>.
        /// </summary>
        /// <param name="taskId">Идентификатор задачи.</param>
        /// <param name="cts">Источник отмены этого запуска.</param>
        public IDisposable Track(Guid taskId, CancellationTokenSource cts)
        {
            long id = Interlocked.Increment(ref _nextId);
            ConcurrentDictionary<long, CancellationTokenSource> runs =
                _active.GetOrAdd(taskId, _ => new ConcurrentDictionary<long, CancellationTokenSource>());
            runs[id] = cts;

            return new Untracker(this, taskId, id);
        }

        /// <summary>Обрывает все активные запуски задачи. Возвращает, сколько оборвано.</summary>
        /// <param name="taskId">Идентификатор задачи.</param>
        public int CancelTask(Guid taskId)
        {
            if (!_active.TryRemove(taskId, out ConcurrentDictionary<long, CancellationTokenSource>? runs))
            {
                return 0;
            }

            int stopped = 0;
            foreach (CancellationTokenSource cts in runs.Values)
            {
                stopped += TryCancel(cts) ? 1 : 0;
            }
            return stopped;
        }

        /// <summary>
        /// Обрывает все активные запуски (проба считает себя удалённой).
        /// Возвращает, сколько запусков оборвано.
        /// </summary>
        public int CancelAll()
        {
            int stopped = 0;
            foreach (Guid taskId in _active.Keys)
            {
                stopped += CancelTask(taskId);
            }
            return stopped;
        }

        /// <summary>Число активных запусков задачи (для тестов и диагностики).</summary>
        /// <param name="taskId">Идентификатор задачи.</param>
        public int ActiveRuns(Guid taskId) =>
            _active.TryGetValue(taskId, out ConcurrentDictionary<long, CancellationTokenSource>? runs) ? runs.Count : 0;

        /// <summary>Отменяет запуск, не падая на уже завершённом (гонка с концом замера).</summary>
        private static bool TryCancel(CancellationTokenSource cts)
        {
            try
            {
                cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false; // запуск успел завершиться сам — обрывать нечего
            }
        }

        /// <summary>Снятие запуска с учёта по завершении.</summary>
        private sealed class Untracker(RunCancelRegistry registry, Guid taskId, long id) : IDisposable
        {
            public void Dispose()
            {
                if (!registry._active.TryGetValue(taskId, out ConcurrentDictionary<long, CancellationTokenSource>? runs))
                {
                    return;
                }
                _ = runs.TryRemove(id, out _);
                if (runs.IsEmpty)
                {
                    _ = registry._active.TryRemove(taskId, out _);
                }
            }
        }
    }
}
