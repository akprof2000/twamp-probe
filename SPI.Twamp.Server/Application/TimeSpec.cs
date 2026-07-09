// Ignore Spelling: SPI Twamp

using System.Globalization;
using System.Text.RegularExpressions;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Разбор моментов времени из шаблонов задач: абсолютная дата либо относительная
    /// длительность от опорного момента («2 week 3 day 2 hour», поддерживаются
    /// week/day/hour/min/sec и опечатка «weak»).
    /// </summary>
    public static partial class TimeSpec
    {
        /// <summary>Компонент длительности: число + единица.</summary>
        [GeneratedRegex(@"(\d+)\s*(week|weak|day|hour|min|sec)", RegexOptions.IgnoreCase)]
        private static partial Regex DurationRegex();

        /// <summary>
        /// Разбирает значение времени: пустое — <paramref name="fallback"/>, дата — как есть,
        /// длительность — смещение от <paramref name="origin"/>.
        /// Возвращаемое значение имеет Kind=Local, чтобы при передаче пробе в другом часовом
        /// поясе JSON содержал смещение и время корректно пересчиталось.
        /// </summary>
        /// <param name="value">Строка из шаблона (дата, длительность или пусто).</param>
        /// <param name="origin">Опорный момент для относительной длительности.</param>
        /// <param name="fallback">Значение по умолчанию для пустой строки.</param>
        /// <param name="error">Текст ошибки разбора или <c>null</c>.</param>
        public static DateTime Resolve(string? value, DateTime origin, DateTime fallback, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(value))
            {
                return DateTime.SpecifyKind(fallback, DateTimeKind.Local);
            }

            string text = value.Trim();

            // Абсолютная дата: сначала точные форматы, затем общий разбор.
            string[] formats = ["dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy"];
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exact))
            {
                return DateTime.SpecifyKind(exact, DateTimeKind.Local);
            }
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Local);
            }

            // Относительная длительность: «2 week 3 day 2 hour 30 min».
            MatchCollection parts = DurationRegex().Matches(text);
            if (parts.Count == 0)
            {
                error = $"не удалось разобрать время «{text}» (ожидалась дата или «N week N day N hour»)";
                return DateTime.SpecifyKind(fallback, DateTimeKind.Local);
            }

            TimeSpan offset = TimeSpan.Zero;
            foreach (Match part in parts.Cast<Match>())
            {
                int amount = int.Parse(part.Groups[1].Value);
                offset += part.Groups[2].Value.ToLowerInvariant() switch
                {
                    "week" or "weak" => TimeSpan.FromDays(7 * amount),
                    "day" => TimeSpan.FromDays(amount),
                    "hour" => TimeSpan.FromHours(amount),
                    "min" => TimeSpan.FromMinutes(amount),
                    "sec" => TimeSpan.FromSeconds(amount),
                    _ => TimeSpan.Zero
                };
            }

            return DateTime.SpecifyKind(origin + offset, DateTimeKind.Local);
        }
    }
}
