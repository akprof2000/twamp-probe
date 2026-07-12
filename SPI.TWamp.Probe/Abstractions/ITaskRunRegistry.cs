// Ignore Spelling: SPI Twamp

using SPI.Twamp.Probe.Contracts;

namespace SPI.Twamp.Probe.Abstractions
{
    /// <summary>
    /// Исход последнего запуска зонда — составной статус жизненного цикла:
    /// запускалась ли задача, корректно ли завершился процесс и каков результат.
    /// </summary>
    public enum RunOutcome
    {
        /// <summary>Задача ещё ни разу не запускалась пробой.</summary>
        NotStarted,
        /// <summary>Задача выполняется прямо сейчас.</summary>
        Running,
        /// <summary>Процесс зонда завершился корректно (код выхода 0).</summary>
        Success,
        /// <summary>Процесс завершился с ненулевым кодом выхода (см. LastExitCode).</summary>
        ExitCodeError,
        /// <summary>Процесс зонда не удалось запустить (например, файл не найден).</summary>
        StartFailed,
        /// <summary>Процесс превысил таймаут задачи и был принудительно завершён.</summary>
        TimedOut
    }

    /// <summary>
    /// Текущее состояние выполнения одной задачи на пробе.
    /// </summary>
    public class TaskRunInfo
    {
        /// <summary>Идентификатор задачи.</summary>
        public Guid TaskId { get; set; }
        /// <summary>Название задачи.</summary>
        public string Title { get; set; } = "";
        /// <summary>Тип запроса (WinPing / TWamp / TWampy).</summary>
        public string Mode { get; set; } = "";
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

        /// <summary>Исход последнего завершившегося запуска зонда.</summary>
        public RunOutcome LastOutcome { get; set; } = RunOutcome.NotStarted;
        /// <summary>Код выхода последнего процесса зонда (0 — корректное завершение).</summary>
        public int? LastExitCode { get; set; }
        /// <summary>
        /// Краткий результат последнего запуска: при успехе — итоговая строка вывода
        /// зонда (статистика), при ошибке — её текст.
        /// </summary>
        public string? LastResult { get; set; }
        /// <summary>Число успешных запусков (код выхода 0) с момента старта пробы.</summary>
        public long SuccessTotal { get; set; }
        /// <summary>Число неуспешных запусков (ошибка запуска, таймаут, код ≠ 0).</summary>
        public long ErrorTotal { get; set; }
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

        /// <summary>
        /// Фиксирует исход одного запуска зонда: запустился ли процесс, как завершился
        /// (код выхода) и каков краткий результат. Ведёт счётчики успехов и ошибок.
        /// </summary>
        /// <param name="taskId">Идентификатор задачи.</param>
        /// <param name="outcome">Исход запуска.</param>
        /// <param name="exitCode">Код выхода процесса (если процесс запускался).</param>
        /// <param name="result">Краткий результат: итоговая строка вывода или текст ошибки.</param>
        void ReportOutcome(Guid taskId, RunOutcome outcome, int? exitCode, string? result);

        /// <summary>Фиксирует ошибку выполнения (не сбрасывается до следующей ошибки или успеха).</summary>
        void ReportError(Guid taskId, string error);

        /// <summary>Обновляет время следующего запланированного запуска.</summary>
        void SetNextRun(Guid taskId, string title, string mode, DateTime? nextRun);

        /// <summary>Удаляет задачу из реестра (задача удалена с пробы).</summary>
        void Remove(Guid taskId);

        /// <summary>Снимок состояния всех задач.</summary>
        IReadOnlyList<TaskRunInfo> GetAll();
    }
}
