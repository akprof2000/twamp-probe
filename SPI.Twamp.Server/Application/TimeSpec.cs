// Ignore Spelling: SPI Twamp

using System.Globalization;
using System.Text.RegularExpressions;

namespace SPI.Twamp.Server.Application
{
    /// <summary>
    /// Разбор моментов времени из шаблонов задач: абсолютная дата либо относительная
    /// длительность от опорного момента.
    /// <para>
    /// Длительность — компоненты «[число] единица» в любом порядке; число можно опускать
    /// (подразумевается 1): «2 week 3 day», «year month weak», «1 год 2 месяца 2 недели 15 дней»,
    /// «2 года 1 неделя день». Единицы — английские (year/month/week/day/hour/min/sec,
    /// в т.ч. опечатка «weak») и русские во всех склонениях (год/года/лет, месяц/месяца/месяцев,
    /// неделя/недели/недель, день/дня/дней, час/часа/часов, минута/минут, секунда/секунд).
    /// Годы и месяцы применяются календарно (AddYears/AddMonths), а не как фиксированное число дней.
    /// </para>
    /// </summary>
    public static partial class TimeSpec
    {
        /// <summary>Единица длительности.</summary>
        private enum Unit
        {
            Year,
            Month,
            Week,
            Day,
            Hour,
            Minute,
            Second
        }

        /// <summary>
        /// Сопоставление начала слова с единицей. Префиксы покрывают склонения:
        /// «год|года|году…», «месяц|месяца|месяцев», «неделя|недели|недель|неделю»,
        /// «день» («ден»), «дня|дней» («дн»), «час|часа|часов», «минута|минут|мин»,
        /// «секунда|секунд|сек»; английские — единственное и множественное число.
        /// Порядок важен: более специфичные префиксы идут раньше.
        /// </summary>
        private static readonly (string Prefix, Unit Unit)[] UnitPrefixes =
        [
            ("год", Unit.Year), ("year", Unit.Year),
            ("мес", Unit.Month), ("month", Unit.Month),
            ("недел", Unit.Week), ("week", Unit.Week), ("weak", Unit.Week),
            ("ден", Unit.Day), ("дн", Unit.Day), ("day", Unit.Day),
            ("час", Unit.Hour), ("hour", Unit.Hour),
            ("мин", Unit.Minute), ("min", Unit.Minute),
            ("сек", Unit.Second), ("sec", Unit.Second)
        ];

        /// <summary>Лексема длительности: число либо слово (латиница/кириллица).</summary>
        [GeneratedRegex(@"\d+|\p{L}+")]
        private static partial Regex TokenRegex();

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

            // Относительная длительность: «2 week 3 day», «1 год 2 месяца 2 недели 15 дней».
            if (!TryParseDuration(text, origin, out DateTime result))
            {
                error = $"не удалось разобрать время «{text}» " +
                        "(ожидалась дата или длительность вида «2 недели 3 дня» / «1 year 2 month»)";
                return DateTime.SpecifyKind(fallback, DateTimeKind.Local);
            }

            return DateTime.SpecifyKind(result, DateTimeKind.Local);
        }

        /// <summary>
        /// Разбирает длительность: пары «[число] единица» в любом порядке; пропущенное
        /// число означает 1 («year month weak» = 1 год 1 месяц 1 неделя). Неизвестные
        /// слова пропускаются; нужна хотя бы одна распознанная единица.
        /// </summary>
        private static bool TryParseDuration(string text, DateTime origin, out DateTime result)
        {
            int years = 0;
            int months = 0;
            TimeSpan offset = TimeSpan.Zero;
            bool recognized = false;
            int? amount = null; // число, ожидающее свою единицу

            foreach (Match token in TokenRegex().Matches(text))
            {
                if (char.IsDigit(token.Value[0]))
                {
                    amount = int.Parse(token.Value);
                    continue;
                }

                Unit? unit = MapUnit(token.Value.ToLowerInvariant());
                if (unit is null)
                {
                    continue; // связки вроде «и» просто пропускаем
                }

                Apply(unit.Value, amount ?? 1, ref years, ref months, ref offset);
                recognized = true;
                amount = null;
            }

            // Годы и месяцы — календарно от опорного момента, остальное — точным смещением.
            result = origin.AddYears(years).AddMonths(months) + offset;
            return recognized;
        }

        /// <summary>Определяет единицу по началу слова (склонения покрываются префиксом).</summary>
        private static Unit? MapUnit(string word)
        {
            // «лет» (2 года 5 лет) не начинается с «год» — отдельный случай.
            if (word == "лет")
            {
                return Unit.Year;
            }

            foreach ((string prefix, Unit unit) in UnitPrefixes)
            {
                if (word.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return unit;
                }
            }
            return null;
        }

        /// <summary>Добавляет компонент длительности к накопителям.</summary>
        private static void Apply(Unit unit, int amount, ref int years, ref int months, ref TimeSpan offset)
        {
            switch (unit)
            {
                case Unit.Year:
                    years += amount;
                    break;
                case Unit.Month:
                    months += amount;
                    break;
                case Unit.Week:
                    offset += TimeSpan.FromDays(7 * amount);
                    break;
                case Unit.Day:
                    offset += TimeSpan.FromDays(amount);
                    break;
                case Unit.Hour:
                    offset += TimeSpan.FromHours(amount);
                    break;
                case Unit.Minute:
                    offset += TimeSpan.FromMinutes(amount);
                    break;
                case Unit.Second:
                    offset += TimeSpan.FromSeconds(amount);
                    break;
            }
        }
    }
}
