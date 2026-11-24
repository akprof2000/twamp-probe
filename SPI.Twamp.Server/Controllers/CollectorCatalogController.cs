using Microsoft.AspNetCore.Mvc;
using SPI.Twamp.Server.Infrastructure;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Controllers;

/// <summary>Каталог, по которому съёмник находит пробы и восстанавливает названия старых задач.</summary>
[ApiController]
public class CollectorCatalogController(IClientRepository clients, ITaskRepository tasks, CollectorRegistry collectors, SPI.Twamp.HighAvailability.KeeperPlanStore keeper) : ControllerBase
{
    /// <summary>Адреса подтверждённых проб.</summary>
    [HttpGet("/api/collector/catalog")]
    public async Task<ActionResult<CollectorCatalog>> Get([FromQuery] string collectorId = CollectorRegistry.DefaultId)
    {
        if(keeper.Enabled) return new CollectorCatalog([]); // Old workers cannot own HA probes.
        if ((await collectors.ListAsync()).All(c => c.Id != collectorId)) return NotFound();
        var probes = (await clients.GetAllAsync()).Where(c => (c.CollectorId ?? CollectorRegistry.DefaultId) == collectorId);
        return new CollectorCatalog(probes.Select(c => c.RequestInfo).ToArray());
    }

    /// <summary>Съёмники, доступные для назначения проб.</summary>
    [HttpGet("/api/userinterface/collectors")]
    public Task<CollectorRegistration[]> List() => collectors.ListAsync();

    /// <summary>Проверяет API съёмника и сохраняет адрес под его идентификатором.</summary>
    [HttpPost("/api/userinterface/collectors")]
    public async Task<ActionResult<CollectorRegistration>> Register([FromBody] CollectorRegistration input, CancellationToken ct)
    {
        if(keeper.Enabled) return Conflict("Используйте реестр HA и примените план.");
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            return BadRequest("Введите базовый HTTP(S)-адрес съёмника.");
        try { return await collectors.RegisterAsync(url, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return StatusCode(502, "Не удалось проверить API съёмника. Проверьте адрес и общий ключ.");
        }
    }

    /// <summary>Названия известных задач для результатов проб прежних версий.</summary>
    [HttpPost("/api/collector/titles")]
    public async Task<Dictionary<Guid,string>> Titles([FromBody] Guid[] ids)
    {
        Dictionary<Guid,string> found = new();
        foreach (Guid id in ids.Distinct())
            if (await tasks.GetByIdAsync(id) is { } task) found[id] = task.Title;
        return found;
    }
}
