using Microsoft.AspNetCore.Mvc;
using SPI.Twamp.Server.Infrastructure;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Controllers;

/// <summary>Каталог, по которому съёмник находит пробы и восстанавливает названия старых задач.</summary>
[ApiController]
public class CollectorCatalogController(IClientRepository clients, ITaskRepository tasks, CollectorRegistry collectors, SPI.Twamp.HighAvailability.KeeperPlanStore keeper, RoutePublisher routes) : ControllerBase
{
    /// <summary>Адреса подтверждённых проб.</summary>
    [HttpGet("/api/collector/catalog")]
    public async Task<ActionResult<CollectorCatalog>> Get([FromQuery] string collectorId = CollectorRegistry.DefaultId)
    {
        if(keeper.Enabled) return new CollectorCatalog([]); // Old workers cannot own HA probes.
        if ((await collectors.ListAsync()).All(c => c.Id != collectorId)) return NotFound();
        var routed=(await routes.ListAsync()).Where(r=>r.Applied).Select(r=>r.Probe).ToHashSet();
        var probes = (await clients.GetAllAsync()).Where(c => !routed.Contains(c.RequestInfo) && (c.CollectorId ?? CollectorRegistry.DefaultId) == collectorId);
        return new CollectorCatalog(probes.Select(c => c.RequestInfo).ToArray());
    }

    /// <summary>Съёмники, доступные для назначения проб.</summary>
    [HttpGet("/api/userinterface/collectors")]
    public Task<CollectorRegistration[]> List() => collectors.ListAsync();
    /// <summary>Удаляет свободный съёмник из каталога.</summary>
    [HttpDelete("/api/userinterface/collectors/{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        try { return await collectors.RemoveAsync(id) ? Ok() : NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(ex.Message); }
    }

    /// <summary>Назначения с основным и двумя резервными съёмниками.</summary>
    [HttpGet("/api/userinterface/collector-routes")]
    public Task<ProbeRouteRecord[]> Routes() => routes.ListAsync();
    /// <summary>Восстанавливает действующие назначения и расписания из работающей установки.</summary>
    [HttpPost("/api/userinterface/collector-routes/import")]
    public async Task<IActionResult> Import(CollectorRegistration input, CancellationToken ct)
    {
        try { return Ok(await routes.ImportAsync(input.Url, ct)); }
        catch (ArgumentException ex) { return BadRequest(ex.Message); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        { return StatusCode(502, "Съёмник недоступен или вернул ошибку. " + ex.Message); }
    }

    /// <summary>Порядок съёмников для одной пробы и исходная версия формы.</summary>
    public sealed record RouteInput(string Probe,string[] Members,long Revision);

    /// <summary>Сохраняет автономное назначение на всех участниках.</summary>
    [HttpPost("/api/userinterface/collector-routes")]
    public async Task<IActionResult> SetRoute(RouteInput input,CancellationToken ct)
    {
        try { return Ok(await routes.SetAsync(input.Probe,input.Members,input.Revision,false,ct)); }
        catch(ArgumentException ex){return BadRequest(ex.Message);}
        catch(InvalidOperationException ex){return Conflict(ex.Message);}
        catch(Exception ex) when(!ct.IsCancellationRequested && ex is HttpRequestException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
        { return StatusCode(502,"Назначение ещё не подтверждено. Проверьте версии программ, доступность участников и ключ API. "+ex.Message); }
    }

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
