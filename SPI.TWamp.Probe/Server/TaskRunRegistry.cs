// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Contracts;
using System.Collections.Concurrent;

namespace SPI.Twamp.Probe.Server
{
    /// <summary>
    /// Потокобезопасная реализация реестра состояния выполнения задач.
    /// Хранит всё в памяти — после перезапуска пробы статистика начинается заново.
    /// </summary>
    public sealed class TaskRunRegistry : ITaskRunRegistry
    {
        private readonly ConcurrentDictionary<Guid, TaskRunInfo> _states = new();

        /// <summary>Возвращает (или создаёт) запись состояния задачи.</summary>
        private TaskRunInfo Get(Guid taskId, string title = "")
        {
            TaskRunInfo info = _states.GetOrAdd(taskId, id => new TaskRunInfo { TaskId = id });
            if (!string.IsNullOrEmpty(title))
            {
                info.Title = title;
            }
            return info;
        }

        /// <inheritdoc/>
        public void MarkStarted(TaskInfo task)
        {
            TaskRunInfo info = Get(task.Id, task.Title);
            lock (info)
            {
                info.Running++;
                info.LastStart = DateTime.Now;
                info.Executions++;
                info.LastOutcome = RunOutcome.Running;
            }
        }

        /// <inheritdoc/>
        public void ReportOutcome(Guid taskId, RunOutcome outcome, int? exitCode, string? result)
        {
            TaskRunInfo info = Get(taskId);
            lock (info)
            {
                info.LastOutcome = outcome;
                info.LastExitCode = exitCode;
                info.LastResult = result;

                if (outcome == RunOutcome.Success)
                {
                    info.SuccessTotal++;
                    info.LastError = null; // успех снимает «залипшую» ошибку
                }
                else
                {
                    info.ErrorTotal++;
                    info.LastError = result;
                }
            }
        }

        /// <inheritdoc/>
        public void MarkFinished(Guid taskId)
        {
            TaskRunInfo info = Get(taskId);
            lock (info)
            {
                info.Running = Math.Max(0, info.Running - 1);
                info.LastFinish = DateTime.Now;
            }
        }

        /// <inheritdoc/>
        public void ReportError(Guid taskId, string error)
        {
            TaskRunInfo info = Get(taskId);
            lock (info)
            {
                info.LastError = error;
            }
        }

        /// <inheritdoc/>
        public void SetNextRun(Guid taskId, string title, DateTime? nextRun)
        {
            TaskRunInfo info = Get(taskId, title);
            lock (info)
            {
                info.NextRun = nextRun;
            }
        }

        /// <inheritdoc/>
        public void Remove(Guid taskId) => _ = _states.TryRemove(taskId, out _);

        /// <inheritdoc/>
        public IReadOnlyList<TaskRunInfo> GetAll() => [.. _states.Values];
    }
}
