// Ignore Spelling: SPI Twamp

namespace SPI.Twamp.Server.Contracts
{
    /// <summary>
    /// Набор фильтров списка задач (по всем столбцам). Связывается из строки запроса
    /// (<c>[FromQuery]</c>), поэтому имена свойств совпадают с прежними query-параметрами:
    /// один объект вместо восьми аргументов у методов страницы/массовых операций.
    /// Все текстовые фильтры — «содержит», без учёта регистра.
    /// </summary>
    public sealed record TaskFilter
    {
        /// <summary>Фильтр по названию задачи.</summary>
        public string? Title { get; init; }

        /// <summary>Фильтр по адресу пробы (RequestInfo).</summary>
        public string? Probe { get; init; }

        /// <summary>Фильтр по конечному узлу.</summary>
        public string? Node { get; init; }

        /// <summary>Фильтр по типу задачи: Scheduler / Repeater.</summary>
        public string? Type { get; init; }

        /// <summary>Фильтр по режиму: WinPing / TWamp / TWampy.</summary>
        public string? Mode { get; init; }

        /// <summary>Статус: active / deleted / all.</summary>
        public string Status { get; init; } = "active";

        /// <summary>Исход последнего запуска: Success / ExitCodeError / TimedOut / StartFailed / none.</summary>
        public string? Outcome { get; init; }

        /// <summary>Фильтр по тексту ошибки.</summary>
        public string? Error { get; init; }
    }
}
