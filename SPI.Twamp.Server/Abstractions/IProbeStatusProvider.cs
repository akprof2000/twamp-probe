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
    /// Последний известный результат выполнения задачи — два статуса:
    /// как завершился процесс зонда (сам / убит по таймауту / не запустился)
    /// и каков результат приложения (код выхода, текст ошибки).
    /// </summary>
    /// <param name="Time">Момент результата.</param>
    /// <param name="HasError">Была ли ошибка (любого вида) в этом результате.</param>
    /// <param name="Outcome">Исход запуска: Success / ExitCodeError / TimedOut / StartFailed; null — данные старой версии.</param>
    /// <param name="ExitCode">Код выхода процесса зонда (null — не запустился или старая версия).</param>
    /// <param name="Error">Краткий текст ошибки (обрезан для интерфейса).</param>
    public record TaskLastResult(DateTime Time, bool HasError, string? Outcome, int? ExitCode, string? Error);

    /// <summary>
    /// Доступ к состоянию фонового опроса проб — для страницы статуса.
    /// </summary>
    public interface IProbeStatusProvider
    {
        /// <summary>Возвращает состояние опроса всех проб (ключ — адрес пробы).</summary>
        IReadOnlyDictionary<string, ProbePollState> GetStates();

        /// <summary>
        /// Возвращает последние результаты по задачам (ключ — идентификатор задачи).
        /// Свежие данные копятся по мере поступления результатов; при старте сервера
        /// реестр прогревается последними записями из БД, поэтому после перезапуска
        /// история выполнения не выглядит пустой.
        /// </summary>
        IReadOnlyDictionary<Guid, TaskLastResult> GetLastResults();
    }
}
