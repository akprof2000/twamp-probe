// Ignore Spelling: SPI Twamp twampy twping

using System.Globalization;
using System.Text.RegularExpressions;

namespace SPI.Twamp.Server.Parser
{
    /// <summary>
    /// Разбор вывода утилиты <c>twampy</c> (nokia/twampy, режим <c>sender</c>) в те же
    /// поля <see cref="TwPingStats"/>, что и <see cref="TwPingParser"/> для twping.
    /// <para>
    /// twampy печатает таблицу с направлениями Outbound / Inbound / Roundtrip и
    /// колонками Min / Max / Avg / Jitter / Loss. Значения — с единицами (us/ms/sec/min),
    /// приводятся к миллисекундам. Сопоставление с полями twping:
    /// Roundtrip → Rtt (Avg → медиана), Outbound → Send, Inbound → Reflect,
    /// джиттеры и процент потерь — напрямую. Так данные twampy в отчётах, БД и
    /// интерфейсе неотличимы по значениям от twping.
    /// </para>
    /// </summary>
    public static partial class TwampyParser
    {
        /// <summary>Строка направления: «Outbound: min max avg jitter loss%».</summary>
        [GeneratedRegex(@"^\s*(Outbound|Inbound|Roundtrip):\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+([\d.]+)%",
            RegexOptions.IgnoreCase | RegexOptions.Multiline)]
        private static partial Regex DirectionRegex();

        /// <summary>Значение длительности с единицей измерения (us/ms/sec/min).</summary>
        [GeneratedRegex(@"(-?[\d.]+)\s*(us|ms|sec|min)", RegexOptions.IgnoreCase)]
        private static partial Regex DurationRegex();

        /// <summary>
        /// Разбирает вывод одного запуска twampy sender. Возвращает список с одной
        /// записью статистики (пустой, если данных нет). При наличии текста ошибки и
        /// отсутствии данных возвращает строку с ошибкой — как <see cref="TwPingParser.ParseMany"/>.
        /// </summary>
        /// <param name="input">Стандартный вывод twampy.</param>
        /// <param name="error">Текст ошибки (при наличии).</param>
        /// <param name="id">Идентификатор задачи.</param>
        public static List<TwPingStats> ParseMany(string? input, string? error, Guid? id)
        {
            List<TwPingStats> list = [];

            if (!string.IsNullOrEmpty(input) && input.Contains("Direction", StringComparison.OrdinalIgnoreCase))
            {
                TwPingStats stats = Parse(input, error, id);
                list.Add(stats);
            }

            // Нет таблицы, но есть ошибка (например, зонд не запустился/таймаут) —
            // фиксируем отдельной строкой, чтобы ответ попал в отчёт.
            if (list.Count == 0 && !string.IsNullOrEmpty(error))
            {
                list.Add(new TwPingStats { Id = id, Errors = error.Trim() });
            }

            return list;
        }

        /// <summary>Разбирает таблицу twampy в статистику сеанса.</summary>
        public static TwPingStats Parse(string? text, string? error, Guid? id)
        {
            TwPingStats stats = new() { Id = id };

            if (!string.IsNullOrEmpty(text))
            {
                // Полная потеря пакетов: таблица направлений не печатается, только пометка.
                if (text.Contains("NO STATS AVAILABLE", StringComparison.OrdinalIgnoreCase))
                {
                    stats.LossPercent = 100;
                }

                foreach (GroupCollection g in DirectionRegex().Matches(text).Select(m => m.Groups))
                {
                    double? min = ToMs(g[2].Value);
                    double? max = ToMs(g[3].Value);
                    double? avg = ToMs(g[4].Value);
                    double? jitter = ToMs(g[5].Value);
                    double loss = double.Parse(g[6].Value, CultureInfo.InvariantCulture);

                    switch (g[1].Value.ToLowerInvariant())
                    {
                        case "outbound": // прямое направление ≈ send time в twping
                            stats.SendMin = min;
                            stats.SendMedian = avg;
                            stats.SendMax = max;
                            stats.SendJitter = jitter;
                            break;

                        case "inbound": // обратное направление ≈ reflect time в twping
                            stats.ReflectMin = min;
                            stats.ReflectMedian = avg;
                            stats.ReflectMax = max;
                            stats.ReflectJitter = jitter;
                            break;

                        case "roundtrip": // круговая задержка = RTT в twping
                            stats.RttMin = min;
                            stats.RttMedian = avg; // у twampy среднее, а не медиана — берём как центральное
                            stats.RttMax = max;
                            stats.TwoWayJitter = jitter;
                            stats.LossPercent = loss;
                            break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(error))
            {
                stats.Errors = error.Trim();
            }
            return stats;
        }

        /// <summary>Переводит значение twampy (например «38us», «12.34ms», «1.5sec») в миллисекунды.</summary>
        private static double? ToMs(string token)
        {
            Match m = DurationRegex().Match(token);
            if (!m.Success)
            {
                return null;
            }

            double value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return m.Groups[2].Value.ToLowerInvariant() switch
            {
                "us" => value / 1000.0,
                "ms" => value,
                "sec" => value * 1000.0,
                "min" => value * 60000.0,
                _ => value
            };
        }
    }
}
