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

        /// <summary>
        /// Вывод клиента twping-go, снятый с настоящего замера против twampd
        /// из perfSONAR. Подписи русские — как их печатает этот клиент.
        /// </summary>
        private const string SampleBlockGo = """
            --- статистика twping от [127.0.0.1]:43873 к [127.0.0.1]:8960 ---
            SID:	7f000001ee1854f631ea1405d4f25363
            первый:	2026-08-01T11:20:23.210
            последний:	2026-08-01T11:20:24.373
            отправлено 100, потеряно 0 (0.000%), дубликатов при отправке 0, при отражении 0
            время кругового обхода мин/медиана/макс = 0.124/0.35/0.677 мс, (погрешность=0.322 мс)
            время до отражателя мин/медиана/макс = 0.0787/0.15/0.399 мс, (погрешность=0.161 мс)
            время от отражателя мин/медиана/макс = 0.0458/0.15/0.407 мс, (погрешность=0.161 мс)
            время обработки на отражателе мин/макс = 0.003/0.055 мс
            джиттер (двусторонний) = 0.2 мс (P95-P50)
            джиттер (до отражателя) = 0.1 мс (P95-P50)
            джиттер (от отражателя) = 0.2 мс (P95-P50)
            число хопов (до отражателя) = 0 (неизменно)
            число хопов (от отражателя) = 0 (неизменно)
            """;

        [Fact(DisplayName = "Разбирается вывод twping-go: подписи русские, величины те же")]
        public void Parse_GoClientBlock()
        {
            Guid id = Guid.NewGuid();
            TwPingStats stats = TwPingParser.Parse(SampleBlockGo, null, id);

            // Без поддержки русских подписей статистика вышла бы пустой, и замер
            // не попал бы ни в отчёт, ни в графики — при том что сам он удался.
            Assert.Equal("127.0.0.1", stats.FromHost);
            Assert.Equal(43873, stats.FromPort);
            Assert.Equal("127.0.0.1", stats.ToHost);
            Assert.Equal(8960, stats.ToPort);
            Assert.Equal("7f000001ee1854f631ea1405d4f25363", stats.Sid);

            Assert.Equal(100, stats.Sent);
            Assert.Equal(0, stats.Lost);
            Assert.Equal(0.0, stats.LossPercent);

            Assert.Equal(0.124, stats.RttMin);
            Assert.Equal(0.35, stats.RttMedian);
            Assert.Equal(0.677, stats.RttMax);

            Assert.Equal(0.0787, stats.SendMin);
            Assert.Equal(0.0458, stats.ReflectMin);
            Assert.Equal(0.003, stats.ReflectProcMin);
            Assert.Equal(0.055, stats.ReflectProcMax);

            Assert.Equal(0.2, stats.TwoWayJitter);
            Assert.Equal(0.1, stats.SendJitter);
            Assert.Equal(0, stats.SendHops);
        }

        [Fact(DisplayName = "Оба вида вывода дают одинаковый набор заполненных полей")]
        public void Parse_BothClients_SameFields()
        {
            TwPingStats en = TwPingParser.Parse(SampleBlock, null, Guid.NewGuid());
            TwPingStats ru = TwPingParser.Parse(SampleBlockGo, null, Guid.NewGuid());

            // Сравниваем не значения (замеры разные), а то, что разбор дошёл до
            // всех величин: пустое поле означает, что подпись не распознана.
            Assert.NotEqual(0, en.Sent);
            Assert.NotEqual(0, ru.Sent);
            Assert.NotEqual(0.0, en.RttMedian);
            Assert.NotEqual(0.0, ru.RttMedian);
            Assert.NotEqual(0.0, en.SendMin);
            Assert.NotEqual(0.0, ru.SendMin);
            Assert.NotEqual(0.0, en.ReflectMin);
            Assert.NotEqual(0.0, ru.ReflectMin);
            Assert.False(string.IsNullOrEmpty(en.Sid));
            Assert.False(string.IsNullOrEmpty(ru.Sid));
        }

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

        [Fact(DisplayName = "Первая колонка отчёта — Started (дата-время запуска, dd.MM.yyyy HH.mm.ss)")]
        public void ToCsvLine_StartedFirstColumn()
        {
            TwPingStats stats = new()
            {
                Started = new DateTime(2026, 7, 22, 16, 9, 5, DateTimeKind.Local),
                Title = "t"
            };
            string[] header = TwPingParser.CsvHeader(';').Split(';');
            string[] cells = TwPingParser.ToCsvLine(stats, ';', ',').Split(';');

            Assert.Equal("Started", header[0]); // первая колонка
            Assert.Equal("22.07.2026 16.09.05", cells[0]);
        }
    }
}
