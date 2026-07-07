using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SPI.Twamp.Server.Parser
{

    /// <summary>
    /// 
    /// </summary>
    public static partial class TwPingParser
    {
        private static string? Extract(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern, RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Parses the specified text.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="error">The error.</param>
        /// <param name="id">The identifier.</param>
        /// <returns></returns>
        public static TwPingStats Parse(string? text, string? error, Guid? id)
        {

            TwPingStats stats = new() { Id = id };
            CultureInfo culture = CultureInfo.InvariantCulture;

            if (!string.IsNullOrEmpty(text))
            {
                // from/to
                Match ft = RegExHeader().Match(text);

                if (ft.Success)
                {
                    stats.FromHost = ft.Groups[1].Value;

                    if (int.TryParse(ft.Groups[2].Value, out int fromPort))
                    {
                        stats.FromPort = fromPort;
                    }

                    stats.ToHost = ft.Groups[3].Value;

                    if (int.TryParse(ft.Groups[4].Value, out int toPort))
                    {
                        stats.ToPort = toPort;
                    }
                }

                // SID
                stats.Sid = Extract(text, @"SID:\s*([a-fA-F0-9]+)");

                // first / last
                string? firstStr = Extract(text, @"first:\s*([0-9T:\.\-]+)");
                if (DateTime.TryParse(firstStr, culture, DateTimeStyles.None, out DateTime first))
                {
                    stats.First = first;
                }

                string? lastStr = Extract(text, @"last:\s*([0-9T:\.\-]+)");
                if (DateTime.TryParse(lastStr, culture, DateTimeStyles.None, out DateTime last))
                {
                    stats.Last = last;
                }

                // sent / lost / loss%
                Match sentLost = RegExSendLoss().Match(text);
                if (sentLost.Success)
                {
                    if (int.TryParse(sentLost.Groups[1].Value, out int sent))
                    {
                        stats.Sent = sent;
                    }

                    if (int.TryParse(sentLost.Groups[2].Value, out int lost))
                    {
                        stats.Lost = lost;
                    }

                    if (double.TryParse(sentLost.Groups[3].Value, NumberStyles.Any, culture, out double loss))
                    {
                        stats.LossPercent = loss;
                    }
                }

                // RTT
                Match rtt = RegExRtt().Match(text);
                if (rtt.Success)
                {
                    if (double.TryParse(rtt.Groups[1].Value, NumberStyles.Any, culture, out double v1))
                    {
                        stats.RttMin = v1;
                    }

                    if (double.TryParse(rtt.Groups[2].Value, NumberStyles.Any, culture, out double v2))
                    {
                        stats.RttMedian = v2;
                    }

                    if (double.TryParse(rtt.Groups[3].Value, NumberStyles.Any, culture, out double v3))
                    {
                        stats.RttMax = v3;
                    }
                }

                // send time
                Match send = RegExSendTime().Match(text);
                if (send.Success)
                {
                    if (double.TryParse(send.Groups[1].Value, NumberStyles.Any, culture, out double s1))
                    {
                        stats.SendMin = s1;
                    }

                    if (double.TryParse(send.Groups[2].Value, NumberStyles.Any, culture, out double s2))
                    {
                        stats.SendMedian = s2;
                    }

                    if (double.TryParse(send.Groups[3].Value, NumberStyles.Any, culture, out double s3))
                    {
                        stats.SendMax = s3;
                    }
                }

                // reflect time
                Match reflect = RegExDateTime().Match(text);
                if (reflect.Success)
                {
                    if (double.TryParse(reflect.Groups[1].Value, NumberStyles.Any, culture, out double r1))
                    {
                        stats.ReflectMin = r1;
                    }

                    if (double.TryParse(reflect.Groups[2].Value, NumberStyles.Any, culture, out double r2))
                    {
                        stats.ReflectMedian = r2;
                    }

                    if (double.TryParse(reflect.Groups[3].Value, NumberStyles.Any, culture, out double r3))
                    {
                        stats.ReflectMax = r3;
                    }
                }

                // reflector processing
                Match proc = RegExReflector().Match(text);
                if (proc.Success)
                {
                    if (double.TryParse(proc.Groups[1].Value, NumberStyles.Any, culture, out double p1))
                    {
                        stats.ReflectProcMin = p1;
                    }

                    if (double.TryParse(proc.Groups[2].Value, NumberStyles.Any, culture, out double p2))
                    {
                        stats.ReflectProcMax = p2;
                    }
                }

                // jitter
                string? twoWay = Extract(text, @"two-way jitter = ([\d\.]+)");
                if (double.TryParse(twoWay, NumberStyles.Any, culture, out double jw))
                {
                    stats.TwoWayJitter = jw;
                }

                string? sendJ = Extract(text, @"send jitter = ([\d\.]+)");
                if (double.TryParse(sendJ, NumberStyles.Any, culture, out double js))
                {
                    stats.SendJitter = js;
                }

                string? reflJ = Extract(text, @"reflect jitter = ([\d\.]+)");
                if (double.TryParse(reflJ, NumberStyles.Any, culture, out double jr))
                {
                    stats.ReflectJitter = jr;
                }

                // hops
                string? sendHops = Extract(text, @"send hops = (\d+)");
                if (int.TryParse(sendHops, out int sh))
                {
                    stats.SendHops = sh;
                }

                string? reflHops = Extract(text, @"reflect hops = (\d+)");
                if (int.TryParse(reflHops, out int rh))
                {
                    stats.ReflectHops = rh;
                }
            }

            if (!string.IsNullOrEmpty(error))
            {
                stats.Errors = error.Replace("twping: ", "");
            }
            return stats;
        }


        /// <summary>
        /// Parses the many.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="error">The error.</param>
        /// <param name="id">The identifier.</param>
        /// <returns></returns>
        public static List<TwPingStats> ParseMany(string? input, string? error, Guid? id)
        {
            List<TwPingStats> list = [];

            if (!string.IsNullOrEmpty(input))
            {
                List<string> blocks = [.. RegExMultiLine().Split(input).Where(b => b.Contains("SID:"))];

                foreach (string block in blocks)
                {
                    try { list.Add(Parse(block, error, id)); }
                    catch { }
                }
            }
            else if (!string.IsNullOrEmpty(error))
            {
                list.Add(Parse(input, error, id));
            }

            return list;
        }

        /// <summary>
        /// CSVs the escape.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns></returns>
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
        /// Formats the number.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="decimalSeparator">The decimal separator.</param>
        /// <returns></returns>
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
        /// Converts to csv.
        /// </summary>
        /// <param name="stats">The stats.</param>
        /// <param name="columnSeparator">The column separator.</param>
        /// <param name="decimalSeparator">The decimal separator.</param>
        /// <returns></returns>
        public static string ToCsv(IEnumerable<TwPingStats> stats, char columnSeparator, char decimalSeparator)
        {
            StringBuilder sb = new();

            _ = sb.AppendLine(string.Join(columnSeparator, ["Title", "Id",
            "FromHost","FromPort","ToHost","ToPort","SID","First","Last","Sent","Lost","LossPercent",
            "RttMin","RttMedian","RttMax","SendMin","SendMedian","SendMax",
            "ReflectMin","ReflectMedian","ReflectMax","ReflectProcMin","ReflectProcMax",
            "TwoWayJitter","SendJitter","ReflectJitter","SendHops","ReflectHops","Errors"]));

            foreach (var s in stats)
            {
                _ = sb.AppendLine(string.Join(columnSeparator, new[]
                {
            CsvEscape(s.Title),
            CsvEscape(s.Id?.ToString()),
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
            CsvEscape(s.Errors)}));
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