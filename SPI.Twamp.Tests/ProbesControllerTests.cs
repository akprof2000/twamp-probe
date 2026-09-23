// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NLog;
using SPI.Twamp.Server.Controllers;
using SPI.Twamp.Server.Infrastructure;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace SPI.Twamp.Tests
{
    /// <summary>
    /// Действия оператора, которые обращаются к пробе, при её недоступности обязаны
    /// отвечать понятным 502/504, а не необработанным исключением и голым 500:
    /// именно такой 500 оператор видел на проде, когда проба была выключена.
    /// </summary>
    public class ProbesControllerTests
    {
        private static ProbesController Controller(int httpTimeoutSec)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Probe:HttpTimeoutSec"] = httpTimeoutSec.ToString(),
                })
                .Build();
            // Остальные зависимости в проверяемых действиях не участвуют.
            return new ProbesController(LogManager.GetCurrentClassLogger(), null!, null!, null!,
                new ProbeClient(configuration), null!);
        }

        [Fact(DisplayName = "Проба не слушает порт — 502 с причиной, а не 500")]
        public async Task ProbeState_ReturnsBadGateway_WhenProbeIsDown()
        {
            // Порт 1 закрыт: соединение отклоняется сразу.
            ActionResult result = await Controller(5).ProbeState("http://127.0.0.1:1", TestContext.Current.CancellationToken);

            ObjectResult response = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status502BadGateway, response.StatusCode);
            string text = Assert.IsType<string>(response.Value);
            Assert.Contains("127.0.0.1:1", text);
            Assert.Contains("недоступна", text);
        }

        [Fact(DisplayName = "Проба принимает соединение и молчит — 504 по таймауту")]
        public async Task ProbeTaskStatus_ReturnsGatewayTimeout_WhenProbeIsSilent()
        {
            using TcpListener silent = new(IPAddress.Loopback, 0);
            silent.Start();
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            // Принимаем соединение, но ответа не даём — так ведёт себя перегруженная проба.
            _ = silent.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();

            ActionResult result = await Controller(1).ProbeTaskStatus(
                $"http://127.0.0.1:{port}", cancellationToken: TestContext.Current.CancellationToken);

            ObjectResult response = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status504GatewayTimeout, response.StatusCode);
            Assert.Contains("не ответила", Assert.IsType<string>(response.Value));
        }
    }
}
