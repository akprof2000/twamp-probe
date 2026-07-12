// Ignore Spelling: SPI Twamp twampy twping

using SPI.Twamp.Server.Parser;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты разбора вывода nokia/twampy и сходимости значений с парсером twping.
    /// </summary>
    public class TwampyParserTests
    {
        /// <summary>Реальный образец таблицы twampy sender (значения в микросекундах).</summary>
        private const string SampleTable = """
            ===============================================================================
            Direction         Min         Max         Avg          Jitter     Loss
            -------------------------------------------------------------------------------
              Outbound:    38us  299us  138us  130us      0.0%
              Inbound:     71us  193us  147us  21us      0.0%
              Roundtrip:   110us  488us  285us  143us      0.0%
            -------------------------------------------------------------------------------
                                                                Jitter Algorithm [RFC1889]
            ===============================================================================
            """;

        [Fact(DisplayName = "Таблица twampy разбирается в поля RTT/Send/Reflect")]
        public void Parse_Table()
        {
            Guid id = Guid.NewGuid();
            TwPingStats stats = TwampyParser.Parse(SampleTable, null, id);

            Assert.Equal(id, stats.Id);

            // Roundtrip → RTT, единицы us приведены к ms (110us = 0.110 ms).
            Assert.Equal(0.110, stats.RttMin);
            Assert.Equal(0.285, stats.RttMedian); // Avg twampy → медиана twping
            Assert.Equal(0.488, stats.RttMax);
            Assert.Equal(0.143, stats.TwoWayJitter);
            Assert.Equal(0.0, stats.LossPercent);

            // Outbound → Send.
            Assert.Equal(0.038, stats.SendMin);
            Assert.Equal(0.138, stats.SendMedian);
            Assert.Equal(0.299, stats.SendMax);
            Assert.Equal(0.130, stats.SendJitter);

            // Inbound → Reflect.
            Assert.Equal(0.071, stats.ReflectMin);
            Assert.Equal(0.147, stats.ReflectMedian);
            Assert.Equal(0.193, stats.ReflectMax);
            Assert.Equal(0.021, stats.ReflectJitter);
        }

        [Theory(DisplayName = "Единицы измерения приводятся к миллисекундам")]
        [InlineData("500us", 0.5)]
        [InlineData("12.34ms", 12.34)]
        [InlineData("1.5sec", 1500.0)]
        public void Parse_UnitsNormalizedToMs(string value, double expectedMs)
        {
            string table = $"""
                Direction         Min         Max         Avg          Jitter     Loss
                  Roundtrip:   {value}  {value}  {value}  {value}      0.0%
                """;

            TwPingStats stats = TwampyParser.Parse(table, null, Guid.NewGuid());
            Assert.Equal(expectedMs, stats.RttMax);
        }

        [Fact(DisplayName = "Полная потеря пакетов: LossPercent = 100")]
        public void Parse_TotalLoss()
        {
            string table = """
                Direction         Min         Max         Avg          Jitter     Loss
                  NO STATS AVAILABLE (100% loss)
                """;

            TwPingStats stats = TwampyParser.Parse(table, null, Guid.NewGuid());
            Assert.Equal(100, stats.LossPercent);
            Assert.Null(stats.RttMin);
        }

        [Fact(DisplayName = "ParseMany: без таблицы, но с ошибкой — запись с ошибкой")]
        public void ParseMany_ErrorOnly()
        {
            List<TwPingStats> list = TwampyParser.ParseMany(null, "connection refused", Guid.NewGuid());

            Assert.Single(list);
            Assert.Equal("connection refused", list[0].Errors);
        }

        [Fact(DisplayName = "ProbeOutputParser выбирает twampy по режиму и проставляет Mode")]
        public void Dispatcher_SelectsTwampy()
        {
            List<TwPingStats> list = ProbeOutputParser.Parse("TWampy", SampleTable, null, Guid.NewGuid());

            Assert.Single(list);
            Assert.Equal("TWampy", list[0].Mode);
            Assert.Equal(0.285, list[0].RttMedian);
        }
    }
}
