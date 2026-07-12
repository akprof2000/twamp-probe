using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SPI.Twamp.Server.Parser
{

    /// <summary>
    /// Разбор текстового вывода утилиты twping в структурированную статистику
    /// и формирование CSV-отчёта.
    /// </summary>
    public static partial class TwPingParser
    {
        private static string? Extract(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern, RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Разбирает один блок вывода twping в объект статистики.
        /// </summary>
        /// <param name="text">Текст вывода зонда.</param>
        /// <param name="error">Текст ошибок зонда (при наличии).</param>
        /// <param name="id">Идентификатор задачи.</param>
        /// <returns>Заполненная статистика сеанса.</returns>
        public static TwPingStats Parse(string? text, string? error, Guid? id)
        {
            TwPingStats stats = new() { Id = id };

            if (!string.IsNullOrEmpty(text))
            {
                CultureInfo culture = CultureInfo.InvariantCulture;
                ParseEndpoints(text, stats);
                ParseSession(text, stats, culture);
                ParseDelays(text, stats, culture);
                ParseJitterAndHops(text, stats, culture);
            }

            if (!string.IsNullOrEmpty(error))
            {
                stats.Errors = error.Replace("twping: ", "");
            }
            return stats;
        }

        /// <summary>Разбирает вещественное число или возвращает <c>null</c>, если значение не распознано.</summary>
        private static double? Num(string value, CultureInfo culture) =>
            double.TryParse(value, NumberStyles.Any, culture, out double v) ? v : null;

        /// <summary>Разбирает целое число или возвращает <c>null</c>.</summary>
        private static int? Int(string value) => int.TryParse(value, out int v) ? v : null;

        /// <summary>Источник/получатель и идентификатор сеанса (SID).</summary>
        private static void ParseEndpoints(string text, TwPingStats stats)
        {
            Match ft = RegExHeader().Match(text);
            if (ft.Success)
            {
                stats.FromHost = ft.Groups[1].Value;
                stats.FromPort = Int(ft.Groups[2].Value);
                stats.ToHost = ft.Groups[3].Value;
                stats.ToPort = Int(ft.Groups[4].Value);
            }

            stats.Sid = Extract(text, @"SID:\s*([a-fA-F0-9]+)");
        }

        /// <summary>Время первого/последнего пакета и счётчики отправлено/потеряно/процент потерь.</summary>
        private static void ParseSession(string text, TwPingStats stats, CultureInfo culture)
        {
            if (DateTime.TryParse(Extract(text, @"first:\s*([0-9T:\.\-]+)"), culture, DateTimeStyles.None, out DateTime first))
            {
                stats.First = first;
            }

            if (DateTime.TryParse(Extract(text, @"last:\s*([0-9T:\.\-]+)"), culture, DateTimeStyles.None, out DateTime last))
            {
                stats.Last = last;
            }

            Match sentLost = RegExSendLoss().Match(text);
            if (sentLost.Success)
            {
                stats.Sent = Int(sentLost.Groups[1].Value);
                stats.Lost = Int(sentLost.Groups[2].Value);
                stats.LossPercent = Num(sentLost.Groups[3].Value, culture);
            }
        }

        /// <summary>Задержки: круговая (RTT), прямая, обратная и время обработки на отражателе.</summary>
        private static void ParseDelays(string text, TwPingStats stats, CultureInfo culture)
        {
            Match rtt = RegExRtt().Match(text);
            if (rtt.Success)
            {
                stats.RttMin = Num(rtt.Groups[1].Value, culture);
                stats.RttMedian = Num(rtt.Groups[2].Value, culture);
                stats.RttMax = Num(rtt.Groups[3].Value, culture);
            }

            Match send = RegExSendTime().Match(text);
            if (send.Success)
            {
                stats.SendMin = Num(send.Groups[1].Value, culture);
                stats.SendMedian = Num(send.Groups[2].Value, culture);
                stats.SendMax = Num(send.Groups[3].Value, culture);
            }

            Match reflect = RegExDateTime().Match(text);
            if (reflect.Success)
            {
                stats.ReflectMin = Num(reflect.Groups[1].Value, culture);
                stats.ReflectMedian = Num(reflect.Groups[2].Value, culture);
                stats.ReflectMax = Num(reflect.Groups[3].Value, culture);
            }

            Match proc = RegExReflector().Match(text);
            if (proc.Success)
            {
                stats.ReflectProcMin = Num(proc.Groups[1].Value, culture);
                stats.ReflectProcMax = Num(proc.Groups[2].Value, culture);
            }
        }

        /// <summary>Джиттеры (двусторонний/прямой/обратный) и количество переходов (hops).</summary>
        private static void ParseJitterAndHops(string text, TwPingStats stats, CultureInfo culture)
        {
            stats.TwoWayJitter = Num(Extract(text, @"two-way jitter = ([\d\.]+)") ?? "", culture);
            stats.SendJitter = Num(Extract(text, @"send jitter = ([\d\.]+)") ?? "", culture);
            stats.ReflectJitter = Num(Extract(text, @"reflect jitter = ([\d\.]+)") ?? "", culture);
            stats.SendHops = Int(Extract(text, @"send hops = (\d+)") ?? "");
            stats.ReflectHops = Int(Extract(text, @"reflect hops = (\d+)") ?? "");
        }


        /// <summary>
        /// Разбирает вывод, содержащий несколько блоков статистики (по числу сеансов).
        /// </summary>
        /// <param name="input">Полный текст вывода зонда.</param>
        /// <param name="error">Текст ошибок зонда (при наличии).</param>
        /// <param name="id">Идентификатор задачи.</param>
        /// <returns>Список статистик по каждому найденному блоку.</returns>
        public static List<TwPingStats> ParseMany(string? input, string? error, Guid? id)
        {
            List<TwPingStats> list = [];

            if (!string.IsNullOrEmpty(input))
            {
                List<string> blocks = [.. RegExMultiLine().Split(input).Where(b => b.Contains("SID:"))];

                foreach (string block in blocks)
                {
                    try
                    {
                        list.Add(Parse(block, error, id));
                    }
                    catch (Exception)
                    {
                        // Битый блок пропускаем, чтобы не потерять остальные корректные сеансы.
                    }
                }
            }

            // Блоков twping не нашлось (например, вывод ping или прерванный замер),
            // но есть текст ошибки — фиксируем его отдельной строкой отчёта,
            // чтобы ответ можно было идентифицировать по строке вызова.
            if (list.Count == 0 && !string.IsNullOrEmpty(error))
            {
                list.Add(Parse(null, error, id));
            }

            return list;
        }

        /// <summary>
        /// Экранирует значение для CSV (кавычки и разделители).
        /// </summary>
        /// <param name="value">Исходное значение.</param>
        /// <returns>Значение, безопасное для вставки в CSV.</returns>
        public static string CsvEscape(string? value)
        {
            if (value == null)
            {
                return "";
            }

            bool mustQuote =
                value.Contains(',') ||
                value.Contains(';') ||
                value.Contains('"') ||
                value.Contains('\n') ||
                value.Contains('\r');

            string escaped = value.Replace("\"", "\"\"");

            return mustQuote ? $"\"{escaped}\"" : escaped;
        }


        /// <summary>
        /// Форматирует число с заданным десятичным разделителем.
        /// </summary>
        /// <param name="value">Значение.</param>
        /// <param name="decimalSeparator">Десятичный разделитель.</param>
        /// <returns>Строковое представление числа (или пустая строка для null).</returns>
        public static string FormatNumber(double? value, char decimalSeparator)
        {
            if (value == null)
            {
                return "";
            }

            string s = value.Value.ToString(CultureInfo.InvariantCulture);

            if (decimalSeparator != '.')
            {
                s = s.Replace('.', decimalSeparator);
            }

            return s;
        }


        /// <summary>
        /// Возвращает строку заголовка CSV-отчёта.
        /// </summary>
        /// <param name="columnSeparator">Разделитель колонок.</param>
        public static string CsvHeader(char columnSeparator) =>
            string.Join(columnSeparator, "Title", "Id", "Mode", "CallLine",
            "FromHost", "FromPort", "ToHost", "ToPort", "SID", "First", "Last", "Sent", "Lost", "LossPercent",
            "RttMin", "RttMedian", "RttMax", "SendMin", "SendMedian", "SendMax",
            "ReflectMin", "ReflectMedian", "ReflectMax", "ReflectProcMin", "ReflectProcMax",
            "TwoWayJitter", "SendJitter", "ReflectJitter", "SendHops", "ReflectHops", "Errors");

        /// <summary>
        /// Формирует одну строку CSV-отчёта из статистики (для потоковой выгрузки).
        /// </summary>
        /// <param name="s">Статистика сеанса.</param>
        /// <param name="columnSeparator">Разделитель колонок.</param>
        /// <param name="decimalSeparator">Десятичный разделитель чисел.</param>
        public static string ToCsvLine(TwPingStats s, char columnSeparator, char decimalSeparator) =>
            string.Join(columnSeparator,
            CsvEscape(s.Title),
            CsvEscape(s.Id?.ToString()),
            CsvEscape(s.Mode),
            CsvEscape(s.CallLine),
            CsvEscape(s.FromHost),
            CsvEscape(s.FromPort?.ToString()),
            CsvEscape(s.ToHost),
            CsvEscape(s.ToPort?.ToString()),
            CsvEscape(s.Sid),
            CsvEscape(s.First?.ToString("o")),
            CsvEscape(s.Last?.ToString("o")),
            CsvEscape(s.Sent?.ToString()),
            CsvEscape(s.Lost?.ToString()),
            CsvEscape(FormatNumber(s.LossPercent, decimalSeparator)),
            CsvEscape(FormatNumber(s.RttMin, decimalSeparator)),
            CsvEscape(FormatNumber(s.RttMedian, decimalSeparator)),
            CsvEscape(FormatNumber(s.RttMax, decimalSeparator)),
            CsvEscape(FormatNumber(s.SendMin, decimalSeparator)),
            CsvEscape(FormatNumber(s.SendMedian, decimalSeparator)),
            CsvEscape(FormatNumber(s.SendMax, decimalSeparator)),
            CsvEscape(FormatNumber(s.ReflectMin, decimalSeparator)),
            CsvEscape(FormatNumber(s.ReflectMedian, decimalSeparator)),
            CsvEscape(FormatNumber(s.ReflectMax, decimalSeparator)),
            CsvEscape(FormatNumber(s.ReflectProcMin, decimalSeparator)),
            CsvEscape(FormatNumber(s.ReflectProcMax, decimalSeparator)),
            CsvEscape(FormatNumber(s.TwoWayJitter, decimalSeparator)),
            CsvEscape(FormatNumber(s.SendJitter, decimalSeparator)),
            CsvEscape(FormatNumber(s.ReflectJitter, decimalSeparator)),
            CsvEscape(s.SendHops?.ToString()),
            CsvEscape(s.ReflectHops?.ToString()),
            CsvEscape(s.Errors));

        /// <summary>
        /// Формирует CSV-таблицу из набора статистик (с заголовком).
        /// </summary>
        /// <param name="stats">Набор статистик.</param>
        /// <param name="columnSeparator">Разделитель колонок.</param>
        /// <param name="decimalSeparator">Десятичный разделитель чисел.</param>
        /// <returns>Готовое содержимое CSV.</returns>
        public static string ToCsv(IEnumerable<TwPingStats> stats, char columnSeparator, char decimalSeparator)
        {
            StringBuilder sb = new();

            _ = sb.AppendLine(CsvHeader(columnSeparator));

            foreach (TwPingStats s in stats)
            {
                _ = sb.AppendLine(ToCsvLine(s, columnSeparator, decimalSeparator));
            }

            return sb.ToString();
        }


        [GeneratedRegex(@"from\s+\[([^\]]+)\]:(\d+)\s+to\s+\[([^\]]+)\]:(\d+)")]
        private static partial Regex RegExHeader();
        [GeneratedRegex(@"(\d+)\s+sent,\s+(\d+)\s+lost\s+\(([\d\.]+)%\)")]
        private static partial Regex RegExSendLoss();
        [GeneratedRegex(@"reflect time min/median/max = ([\-\d\.]+)/([\-\d\.]+)/([\-\d\.]+)")]
        private static partial Regex RegExDateTime();
        [GeneratedRegex(@"reflector processing time min/max = ([\-\d\.]+)/([\-\d\.]+)")]
        private static partial Regex RegExReflector();
        [GeneratedRegex(@"round-trip time min/median/max = ([\-\d\.]+)/([\-\d\.]+)/([\-\d\.]+)")]
        private static partial Regex RegExRtt();
        [GeneratedRegex(@"send time min/median/max = ([\-\d\.]+)/([\-\d\.]+)/([\-\d\.]+)")]
        private static partial Regex RegExSendTime();
        [GeneratedRegex(@"(?=--- twping statistics)")]
        private static partial Regex RegExMultiLine();
    }
}