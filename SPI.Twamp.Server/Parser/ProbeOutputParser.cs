// Ignore Spelling: SPI Twamp twampy twping

namespace SPI.Twamp.Server.Parser
{
    /// <summary>
    /// Выбирает парсер вывода зонда по режиму задачи и приводит результат к единому
    /// набору полей <see cref="TwPingStats"/>: twampy разбирается <see cref="TwampyParser"/>,
    /// остальные режимы (twping/TWamp, ping) — <see cref="TwPingParser"/>.
    /// </summary>
    public static class ProbeOutputParser
    {
        /// <summary>
        /// Разбирает вывод зонда в статистику с учётом режима задачи и проставляет
        /// каждой записи тип запроса (<paramref name="mode"/>).
        /// </summary>
        /// <param name="mode">Режим задачи (WinPing / TWamp / TWampy).</param>
        /// <param name="console">Стандартный вывод зонда.</param>
        /// <param name="error">Текст ошибки (при наличии).</param>
        /// <param name="taskId">Идентификатор задачи.</param>
        public static List<TwPingStats> Parse(string? mode, string? console, string? error, Guid taskId)
        {
            List<TwPingStats> stats = IsTwampy(mode)
                ? TwampyParser.ParseMany(console, error, taskId)
                : TwPingParser.ParseMany(console, error, taskId);

            foreach (TwPingStats row in stats)
            {
                row.Mode = mode;
            }
            return stats;
        }

        /// <summary>Проверяет, что режим — TWampy (без учёта регистра).</summary>
        private static bool IsTwampy(string? mode) =>
            string.Equals(mode, "TWampy", StringComparison.OrdinalIgnoreCase);
    }
}
