// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Contracts;

namespace SPI.Twamp.Probe.Abstractions
{
    /// <summary>
    /// Текущее состояние выполнения одной задачи на пробе.
    /// </summary>
    public class TaskRunInfo
    {
        /// <summary>Идентификатор задачи.</summary>
        public Guid TaskId { get; set; }
        /// <summary>Название задачи.</summary>
        public string Title { get; set; } = "";
        /// <summary>Сколько экземпляров задачи выполняется прямо сейчас.</summary>
        public int Running { get; set; }
        /// <summary>Момент последнего старта выполнения.</summary>
        public DateTime? LastStart { get; set; }
        /// <summary>Момент последнего завершения выполнения.</summary>
        public DateTime? LastFinish { get; set; }
        /// <summary>Сколько раз задача выполнялась с момента старта пробы.</summary>
        public long Executions { get; set; }
        /// <summary>Ближайший запланированный запуск (для задач по расписанию).</summary>
        public DateTime? NextRun { get; set; }
        /// <summary>Текст последней ошибки выполнения (например, зонд не найден).</summary>
        public string? LastError { get; set; }
    }

    /// <summary>
    /// Реестр состояния выполнения задач — отвечает на вопрос «что сейчас происходит
    /// с задачами на пробе»: запускались ли, выполняются ли прямо сейчас, когда
    /// следующий запуск и какая была последняя ошибка.
    /// </summary>
    public interface ITaskRunRegistry
    {
        /// <summary>Фиксирует начало выполнения задачи.</summary>
        void MarkStarted(TaskInfo task);

        /// <summary>Фиксирует завершение выполнения задачи.</summary>
        void MarkFinished(Guid taskId);

        /// <summary>Фиксирует ошибку выполнения (не сбрасывается до следующей ошибки или успеха).</summary>
        void ReportError(Guid taskId, string error);

        /// <summary>Обновляет время следующего запланированного запуска.</summary>
        void SetNextRun(Guid taskId, string title, DateTime? nextRun);

        /// <summary>Удаляет задачу из реестра (задача удалена с пробы).</summary>
        void Remove(Guid taskId);

        /// <summary>Снимок состояния всех задач.</summary>
        IReadOnlyList<TaskRunInfo> GetAll();
    }
}
