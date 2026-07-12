// Ignore Spelling: SPI Twamp twping

using SPI.Twamp.Server.Parser;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты разбора вывода twping и формирования CSV.
    /// </summary>
    public class TwPingParserTests
    {
        /// <summary>Минимальный образец блока статистики twping.</summary>
        private const string SampleBlock = """
            --- twping statistics from [10.0.0.1]:8888 to [10.0.0.2]:9999 ---
            SID: abcdef0123456789
            first: 2026-07-08T10:00:00.0
            last: 2026-07-08T10:05:00.0
            300 sent, 3 lost (1.000%)
            round-trip time min/median/max = 1.5/2.0/9.9 ms
            send time min/median/max = 0.7/1.0/5.0 ms
            reflect time min/median/max = 0.6/0.9/4.4 ms
            reflector processing time min/max = 0.01/0.05 ms
            two-way jitter = 0.8 ms
            send hops = 7
            """;

        [Fact(DisplayName = "Блок twping разбирается: адреса, потери, RTT")]
        public void Parse_Block()
        {
            Guid id = Guid.NewGuid();
            TwPingStats stats = TwPingParser.Parse(SampleBlock, null, id);

            Assert.Equal(id, stats.Id);
            Assert.Equal("10.0.0.1", stats.FromHost);
            Assert.Equal(8888, stats.FromPort);
            Assert.Equal("10.0.0.2", stats.ToHost);
            Assert.Equal("abcdef0123456789", stats.Sid);
            Assert.Equal(300, stats.Sent);
            Assert.Equal(3, stats.Lost);
            Assert.Equal(1.0, stats.LossPercent);
            Assert.Equal(1.5, stats.RttMin);
            Assert.Equal(2.0, stats.RttMedian);
            Assert.Equal(9.9, stats.RttMax);
            Assert.Equal(7, stats.SendHops);
        }

        [Fact(DisplayName = "ParseMany: несколько блоков — несколько записей")]
        public void ParseMany_MultipleBlocks()
        {
            string input = SampleBlock + "\n" + SampleBlock;
            List<TwPingStats> list = TwPingParser.ParseMany(input, null, Guid.NewGuid());

            Assert.Equal(2, list.Count);
        }

        [Fact(DisplayName = "ParseMany: вывод без блоков twping, но с ошибкой — одна запись с ошибкой")]
        public void ParseMany_ErrorOnly()
        {
            List<TwPingStats> list = TwPingParser.ParseMany(
                "вывод ping без блоков", "Задача прервана по таймауту", Guid.NewGuid());

            TwPingStats stats = Assert.Single(list);
            Assert.Contains("таймауту", stats.Errors);
        }

        [Fact(DisplayName = "ParseMany: пустые вход и ошибка — пустой список")]
        public void ParseMany_Empty()
        {
            Assert.Empty(TwPingParser.ParseMany("", "", Guid.NewGuid()));
        }

        [Theory(DisplayName = "CsvEscape экранирует разделители и кавычки")]
        [InlineData("simple", "simple")]
        [InlineData("a;b", "\"a;b\"")]
        [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
        [InlineData(null, "")]
        public void CsvEscape_Works(string? input, string expected)
        {
            Assert.Equal(expected, TwPingParser.CsvEscape(input));
        }

        [Fact(DisplayName = "FormatNumber подставляет десятичный разделитель")]
        public void FormatNumber_Separator()
        {
            Assert.Equal("1,5", TwPingParser.FormatNumber(1.5, ','));
            Assert.Equal("1.5", TwPingParser.FormatNumber(1.5, '.'));
            Assert.Equal("", TwPingParser.FormatNumber(null, ','));
        }

        [Fact(DisplayName = "Строка CSV содержит колонки Mode и CallLine в правильном порядке")]
        public void ToCsvLine_ContainsModeAndCallLine()
        {
            TwPingStats stats = new()
            {
                Title = "t",
                Id = Guid.Empty,
                Mode = "TWampy",
                CallLine = "./twampy -c 1 10.0.0.1"
            };
            string line = TwPingParser.ToCsvLine(stats, ';', ',');
            string[] header = TwPingParser.CsvHeader(';').Split(';');
            string[] cells = line.Split(';');

            // Порядок ячеек строго соответствует заголовку.
            Assert.Equal("TWampy", cells[Array.IndexOf(header, "Mode")]);
            Assert.Equal("./twampy -c 1 10.0.0.1", cells[Array.IndexOf(header, "CallLine")]);
        }
    }
}
