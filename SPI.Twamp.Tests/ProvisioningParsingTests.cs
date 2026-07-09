// Ignore Spelling: SPI Twamp

using SPI.Twamp.Server.Application;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Тесты разбора строк файла маршрутизаторов и детерминированных идентификаторов задач.
    /// </summary>
    public class ProvisioningParsingTests
    {
        [Theory(DisplayName = "Строка «ИМЯ|IP:адрес …» разбирается независимо от остальных колонок")]
        [InlineData("231101|IP:10.106.23.33\t4\tHUAWEI\t7", "231101", "10.106.23.33")]
        [InlineData("ADCTO24G|IP:10.23.179.54 4 HUAWEI", "ADCTO24G", "10.23.179.54")]
        [InlineData("  SOCHKRASN24G|IP:10.23.167.121", "SOCHKRASN24G", "10.23.167.121")]
        public void RouterLine_Parsed(string line, string expectedName, string expectedIp)
        {
            bool ok = ProvisioningService.TryParseRouterLine(line, out string name, out string ip);

            Assert.True(ok);
            Assert.Equal(expectedName, name);
            Assert.Equal(expectedIp, ip);
        }

        [Theory(DisplayName = "Заголовок и мусор не распознаются как маршрутизатор")]
        [InlineData("SNODE\tCELL_TYPE\tVENDOR\tIP\tRNUM")]
        [InlineData("просто текст без адреса")]
        [InlineData("")]
        public void RouterLine_Rejected(string line)
        {
            Assert.False(ProvisioningService.TryParseRouterLine(line, out _, out _));
        }

        [Fact(DisplayName = "CSV «;» с заголовком: SNODE вида «ИМЯ|IP:адрес»")]
        public void RouterFile_CsvWithSnodePipe()
        {
            string[] lines =
            [
                "SNODE;CELL_TYPE;VENDOR;IP;RNUM",
                "231101|IP:10.106.23.33;4;HUAWEI;10.106.23.33;1",
                "ADCTO24G|IP:10.23.179.54;4;HUAWEI;10.23.179.54;2"
            ];

            var (routers, rejected) = SPI.Twamp.Server.Application.ProvisioningService.ParseRouterFile(lines);

            Assert.Empty(rejected);
            Assert.Equal(2, routers.Count);
            Assert.Equal(("231101", "10.106.23.33"), routers[0]);
        }

        [Fact(DisplayName = "CSV «;» с заголовком: имя из SNODE, адрес из колонки IP")]
        public void RouterFile_CsvWithSeparateIpColumn()
        {
            string[] lines =
            [
                "SNODE;VENDOR;IP;RNUM",
                "231101;HUAWEI;10.106.23.33;1",
                "R2;HUAWEI;10.0.0.2;2"
            ];

            var (routers, rejected) = SPI.Twamp.Server.Application.ProvisioningService.ParseRouterFile(lines);

            Assert.Empty(rejected);
            Assert.Equal(2, routers.Count);
            Assert.Equal(("231101", "10.106.23.33"), routers[0]);
            Assert.Equal(("R2", "10.0.0.2"), routers[1]);
        }

        [Fact(DisplayName = "Формат с табуляцией без заголовка по-прежнему работает")]
        public void RouterFile_TabWithoutHeader()
        {
            string[] lines =
            [
                "231101|IP:10.106.23.33\t4\tHUAWEI",
                "СЛОМАННАЯ СТРОКА",
                "ADCTO24G|IP:10.23.179.54\t4\tHUAWEI"
            ];

            var (routers, rejected) = SPI.Twamp.Server.Application.ProvisioningService.ParseRouterFile(lines);

            Assert.Equal(2, routers.Count);
            _ = Assert.Single(rejected);
        }

        [Fact(DisplayName = "Дубликаты имя+IP отбрасываются, одно имя с разными IP — две записи")]
        public void RouterFile_Deduplication()
        {
            string[] lines =
            [
                "SNODE;IP",
                "R1;10.0.0.1",
                "R1;10.0.0.1",
                "R1;10.0.0.2"
            ];

            var (routers, rejected) = SPI.Twamp.Server.Application.ProvisioningService.ParseRouterFile(lines);

            Assert.Empty(rejected);
            Assert.Equal(2, routers.Count);
        }

        [Fact(DisplayName = "Детерминированный Id: одинаковые входные данные — одинаковый Guid")]
        public void DeterministicId_Stable()
        {
            Guid a = ProvisioningService.DeterministicTaskId("http://p:443", "10.0.0.1", "d46", "R1");
            Guid b = ProvisioningService.DeterministicTaskId("http://p:443", "10.0.0.1", "d46", "R1");

            Assert.Equal(a, b);
        }

        [Fact(DisplayName = "Детерминированный Id: разные узлы или шаблоны — разные Guid")]
        public void DeterministicId_Distinct()
        {
            Guid baseId = ProvisioningService.DeterministicTaskId("http://p:443", "10.0.0.1", "d46", "R1");

            Assert.NotEqual(baseId, ProvisioningService.DeterministicTaskId("http://p:443", "10.0.0.2", "d46", "R1"));
            Assert.NotEqual(baseId, ProvisioningService.DeterministicTaskId("http://p:443", "10.0.0.1", "d47", "R1"));
            Assert.NotEqual(baseId, ProvisioningService.DeterministicTaskId("http://other:443", "10.0.0.1", "d46", "R1"));
        }
    }
}
