using Microsoft.AspNetCore.Mvc;
using SPI.Twamp.HighAvailability;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Infrastructure;
using org.apache.zookeeper;

namespace SPI.Twamp.Server.Controllers;

/// <summary>Реестр, расчёт и атомарная публикация парного плана сбора.</summary>
[ApiController, Route("api/ha")]
public sealed class HaController(KeeperPlanStore keeper, CollectorRegistry registry, IProbeStatusProvider monitor,
    ITaskRepository tasks, IClientRepository clients, IConfiguration settings, SPI.Twamp.Server.BackgroundServices.CollectorMonitor fleet) : ControllerBase
{
    /// <summary>Идентичность экземпляра и его фактического пространства Keeper.</summary>
    public sealed record Identity(string Id, string Machine, bool Ha, string Root, string Target, Guid Registry);
    /// <summary>Возвращает выбранный при запуске режим работы.</summary>
    [HttpGet("mode")]
    public object Mode() => new { keeper.Enabled };
    /// <summary>Текущие владельцы, нагрузка и доступность съёмников.</summary>
    [HttpGet("state")]
    public object State() => new { states = monitor.GetStates(), health = fleet.GetHealth() };

    /// <summary>Возвращает конфигурацию, фактическое владение и обнаруженные пробы.</summary>
    [HttpGet]
    public async Task<IActionResult> Catalog(CancellationToken ct)
    {
        if (!keeper.Enabled) return NotFound();
        var plan = await keeper.ReadAsync(ct);
        return Ok(new
        {
            plan.Version,
            plan.Plan,
            states = monitor.GetStates(),
            health = fleet.GetHealth(),
            existing = (await clients.GetAllAsync()).Select(p => new { url = p.RequestInfo, p.Name }),
            discovered = (await clients.GetUnidentifiedAsync()).Select(p => new { url = p.RequestInfo, name = p.HostName })
        });
    }

    /// <summary>Проверяет идентификатор, машину, таблицу и Keeper нового съёмника.</summary>
    [HttpGet("identify")]
    public async Task<IActionResult> Identify(string url, CancellationToken ct)
    {
        if (!keeper.Enabled) return NotFound();
        try { return Ok(await ReadIdentity(url, ct)); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is ArgumentException or HttpRequestException or InvalidDataException or TaskCanceledException)
        { return BadRequest(ex.Message); }
    }
    private async Task<Identity> ReadIdentity(string url, CancellationToken ct)
    {
        using var client = registry.Connect(new Uri(PlanBuilder.Address(url)));
        var id = await client.GetFromJsonAsync<Identity>("identity", ct);
        if (id == null || !id.Ha || id.Root != keeper.Namespace || id.Registry != await keeper.IdentityAsync(ct) || !CollectorRegistry.IsValidId(id.Id) || string.IsNullOrWhiteSpace(id.Machine)
            || id.Target != (settings["HighAvailability:Target"] ?? "twamp.results_ha"))
            throw new InvalidDataException("Проверьте ID, машину, режим HA, имя пространства Keeper и целевую таблицу съёмника.");
        return id;
    }
    /// <summary>Проверяет пары и предлагает распределение по загрузке.</summary>
    [HttpPost("preview")]
    public Task<IActionResult> Preview(PlanDraft draft, CancellationToken ct) => Calculate(draft, false, ct);
    /// <summary>Публикует просмотренный план, если его исходная версия актуальна.</summary>
    [HttpPost("apply")]
    public Task<IActionResult> Apply(PlanDraft draft, CancellationToken ct) => Calculate(draft, true, ct);

    private async Task<IActionResult> Calculate(PlanDraft draft, bool apply, CancellationToken ct)
    {
        if (!keeper.Enabled) return NotFound();
        try
        {
            var current = await keeper.ReadAsync(ct);
            if (current.Version != draft.Version) return Conflict("Реестр изменился. Перечитайте план.");
            var states = monitor.GetStates();
            var weights = (await tasks.GetAllAsync()).Where(t => !t.Delete).GroupBy(t => t.RequestInfo)
                .ToDictionary(g => g.Key, g => g.Sum(EstimateWeight));
            foreach (var (url, state) in states)
                if (state.WorkPerSecond > 0) weights[url] = state.WorkPerSecond;
            var plan = PlanBuilder.Build(draft, current.Plan, weights);
            if (apply)
            {
                foreach (var c in plan.Collectors.Where(c => !current.Plan.Collectors.Any(old => old.Id == c.Id && old.Url == c.Url && old.Machine == c.Machine)))
                {
                    var identity = await ReadIdentity(c.Url, ct);
                    if (identity.Id != c.Id || !string.Equals(identity.Machine, c.Machine, StringComparison.OrdinalIgnoreCase))
                        return BadRequest("Изменилась идентичность съёмника " + c.Id);
                }
                // Apply the exact reviewed assignments, even if observed throughput changed since preview.
                if (draft.Approved != null)
                {
                    if (draft.Approved.Count != plan.Assignments.Length) return BadRequest("Неполный просмотренный план.");
                    foreach (var a in plan.Assignments)
                    {
                        if (!draft.Approved.TryGetValue(a.Probe.Url, out var owner) || !a.Probe.Contains(owner) ||
                            plan.Collectors.Single(n => n.Id == owner).Standby || a.Probe.Pin is { Length: > 0 } pin && pin != owner)
                            return BadRequest("Недопустимый основной съёмник.");
                    }
                    plan = plan with
                    {
                        Assignments = plan.Assignments.Select(a =>
                        {
                            var primary = draft.Approved[a.Probe.Url];
                            var old = current.Plan.Assignments.FirstOrDefault(o => o.Probe.Url == a.Probe.Url);
                            bool keep = old != null && old.Primary == primary && old.Probe.First == a.Probe.First && old.Probe.Second == a.Probe.Second &&
                                !(draft.Reassign?.Contains(a.Probe.Url) ?? false);
                            return a with { Primary = primary, Generation = keep ? old!.Generation : (long)current.Version + 1 };
                        }).ToArray()
                    };
                }
                else return BadRequest("Сначала рассчитайте и просмотрите план.");
                int revision = await keeper.PublishAsync(current.Version, plan, ct);
                return Ok(new { version = revision, plan });
            }
            return Ok(new { current.Version, plan, weights });
        }
        catch (KeeperException.BadVersionException) { return Conflict("Реестр изменился. Перечитайте план."); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is ArgumentException or InvalidDataException or HttpRequestException or TaskCanceledException)
        { return BadRequest(ex.Message); }
    }
    private static double EstimateWeight(SPI.Twamp.Server.Contracts.TaskInfo task)
    {
        var repeats = Math.Max(1d, task.Repeats);
        if (task.Type != SPI.Twamp.Server.Contracts.TaskType.Scheduler) return repeats;
        var cron = NCrontab.CrontabSchedule.TryParse(task.CronExpression, new NCrontab.CrontabSchedule.ParseOptions { IncludingSeconds = task.CronWithSeconds });
        if (cron == null) return repeats;
        var first = cron.GetNextOccurrence(DateTime.UtcNow);
        if (first == DateTime.MaxValue) return 0.01;
        return repeats / Math.Max(1, (cron.GetNextOccurrence(first) - first).TotalSeconds);
    }
    /// <summary>Проверяет доступность пробы со стороны выбранного съёмника.</summary>
    [HttpGet("link")]
    public async Task<IActionResult> Link(string url, string probe, CancellationToken ct)
    {
        if (!keeper.Enabled) return NotFound();
        try
        {
            await ReadIdentity(url, ct);
            using var client = registry.Connect(new Uri(PlanBuilder.Address(url)));
            using var res = await client.GetAsync("probe-link?url=" + Uri.EscapeDataString(PlanBuilder.Address(probe)), ct);
            return Ok(new { reachable = res.IsSuccessStatusCode });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or ArgumentException or InvalidDataException) { return Ok(new { reachable = false }); }
    }
}
