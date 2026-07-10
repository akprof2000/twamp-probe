// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Abstractions
{
    /// <summary>
    /// Текущее состояние опроса одной пробы.
    /// </summary>
    /// <param name="LastSuccess">Момент последнего успешного опроса.</param>
    /// <param name="LastError">Момент последней ошибки связи.</param>
    /// <param name="LastErrorMessage">Текст последней ошибки.</param>
    /// <param name="TotalResults">Всего результатов, полученных от пробы с момента старта сервера.</param>
    /// <param name="BackoffSeconds">Текущая задержка повторных попыток (0 — связь в порядке).</param>
    public record ProbePollState(
        DateTime? LastSuccess,
        DateTime? LastError,
        string? LastErrorMessage,
        long TotalResults,
        int BackoffSeconds);

    /// <summary>
    /// Последний известный результат выполнения задачи.
    /// </summary>
    /// <param name="Time">Момент результата.</param>
    /// <param name="HasError">Была ли ошибка в этом результате.</param>
    public record TaskLastResult(DateTime Time, bool HasError);

    /// <summary>
    /// Доступ к состоянию фонового опроса проб — для страницы статуса.
    /// </summary>
    public interface IProbeStatusProvider
    {
        /// <summary>Возвращает состояние опроса всех проб (ключ — адрес пробы).</summary>
        IReadOnlyDictionary<string, ProbePollState> GetStates();

        /// <summary>
        /// Возвращает последние результаты по задачам (ключ — идентификатор задачи).
        /// Заполняется по мере поступления результатов после старта сервера.
        /// </summary>
        IReadOnlyDictionary<Guid, TaskLastResult> GetLastResults();
    }
}
