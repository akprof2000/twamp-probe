using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.AspNetCore.Mvc;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Infrastructure;

namespace SPI.Twamp.Server.Controllers;

/// <summary>Текущие показания зарегистрированных узлов. Опрос не читает и не подтверждает пачки.</summary>
[ApiController]
[Route("api/userinterface/exchange")]
public sealed class ExchangeController(IClientRepository probes, CollectorRegistry collectors) : ControllerBase
{
    private sealed record Node(string Kind, string Id, string Url);
    private sealed record Sample(string Kind, string Id, string Url, DateTimeOffset At, JObject? Data, string? Error);

    /// <summary>Читает очереди и последние подтверждения. Ошибки возвращаются отдельно для каждого узла.</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var nodes = (await probes.GetAllAsync()).Select(p => new Node("probe", p.RequestInfo, p.RequestInfo))
            .Concat((await collectors.ListAsync()).Select(c => new Node("collector", c.Id, c.Url))).ToArray();
        var samples = new Sample[nodes.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, nodes.Length),
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 16 }, async (index, token) =>
        {
            var node = nodes[index];
            JObject? data = null;
            string? error = null;
            try
            {
                using var client = collectors.Connect(new Uri(node.Url));
                client.Timeout = TimeSpan.FromSeconds(3);
                var path = node.Kind == "probe" ? "api/probeinterface/probestate" : "exchange";
                using var response = await client.GetAsync(path, token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound && node.Kind == "collector")
                    throw new HttpRequestException("Статистика обмена недоступна. Проверьте версию съёмника (требуется 1.29.1 или новее).");
                response.EnsureSuccessStatusCode();
                data = JObject.Parse(await response.Content.ReadAsStringAsync(token));
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException or JsonException or UriFormatException)
            {
                error = ex is OperationCanceledException ? "Истекло время ожидания ответа" : ex.Message;
            }
            samples[index] = new(node.Kind, node.Id, node.Url, DateTimeOffset.UtcNow, data, error);
        });
        return Ok(samples);
    }
}
