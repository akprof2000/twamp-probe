using LiteDB;
using SPI.Twamp.HighAvailability;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Abstractions;

namespace SPI.Twamp.Server.Infrastructure;

/// <summary>Ревизия автономного назначения и состояние её доставки.</summary>
public sealed class ProbeRouteRecord
{
    /// <summary>Адрес пробы и ключ локальной записи.</summary>
    [BsonId] public string Probe { get; set; } = "";
    /// <summary>Ревизия, которую разрешено заменить на пробе.</summary>
    public long BaseRevision { get; set; }
    /// <summary>Отображаемое имя при открытии на другом рабочем месте.</summary>
    public string Name { get; set; } = "";
    /// <summary>Ревизия этого назначения.</summary>
    public long Revision { get; set; }
    /// <summary>Разрешённые съёмники в порядке приоритета.</summary>
    public CollectorPeer[] Members { get; set; } = [];
    /// <summary>Прежние участники, ожидающие снятия локального назначения.</summary>
    public CollectorPeer[] Retired { get; set; } = [];
    /// <summary>Проба подтвердила запись после подготовки съёмников.</summary>
    public bool Applied { get; set; }
    /// <summary>Другой конфигуратор уже заменил исходную ревизию.</summary>
    public bool Conflict { get; set; }
}

/// <summary>Сохраняет назначения и подтверждает применение после записи на съёмниках и пробе.</summary>
public sealed class RoutePublisher(LiteDbContext db, CollectorRegistry registry, IClientRepository clients,
    KeeperPlanStore keeper, OrderedSchedules schedules, ILogger<RoutePublisher> log) : BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Receipt(string Id, long Revision);
    private sealed record LiveRoute(long Revision, CollectorPeer[]? Peers, string? Name);
    private async Task<LiveRoute> ReadRoute(string url, CancellationToken ct)
    {
        using var probe = registry.Connect(new Uri(PlanBuilder.Address(url)));
        return await probe.GetFromJsonAsync<LiveRoute>("api/probeinterface/collectorroute", ct)
            ?? throw new InvalidDataException("Проба не вернула назначение.");
    }
    private const string Outdated = "Назначение изменено. Откройте конфигурацию со съёмника и повторите изменение.";
    /// <summary>Результат чтения доступной группы и адреса, для которых нужна повторная сверка.</summary>
    public sealed record ImportResult(int Probes, int Collectors, List<string> Unavailable);

    /// <summary>Открывает топологию другого конфигуратора по одному известному адресу.</summary>
    public async Task<ImportResult> ImportAsync(string seed, CancellationToken ct)
    {
        if (keeper.Enabled) throw new ArgumentException("Для этого режима используется реестр Keeper.");
        var first = await registry.RegisterAsync(new Uri(PlanBuilder.Address(seed)), ct);
        var queue = new Queue<CollectorRegistration>();
        queue.Enqueue(first);
        var visited = new HashSet<string>();
        var probes = new HashSet<string>();
        var unavailable = new List<string>();
        int restored = 0;
        await gate.WaitAsync(ct);
        try
        {
            while (queue.TryDequeue(out var node))
            {
                if (!visited.Add(node.Url)) continue;
                string[] catalog;
                try
                {
                    using var http = registry.Connect(new Uri(node.Url));
                    catalog = await http.GetFromJsonAsync<string[]>("peer/catalog", ct) ?? throw new InvalidDataException();
                }
                catch (Exception ex) when (node != first && !ct.IsCancellationRequested && IsRemoteError(ex))
                { unavailable.Add(node.Url); continue; }
                foreach (var url in catalog.Where(probes.Add))
                {
                    try
                    {
                        var live = await ReadRoute(url, ct);
                        if (live.Revision == 0) continue;
                        var members = live.Peers ?? [];
                        var record = new ProbeRouteRecord
                        {
                            Probe = url,
                            Revision = live.Revision,
                            Name = live.Name ?? url,
                            Members = members,
                            Applied = true
                        };
                        var local = await db.ProbeRoutes.FindByIdAsync(url);
                        if (local is { Applied: false, Conflict: false } && local.BaseRevision == live.Revision)
                        { unavailable.Add(url + " (ожидается применение местных изменений)"); continue; }
                        if (members.Length == 0)
                        {
                            await db.ProbeRoutes.UpsertAsync(record);
                            await clients.DeleteAsync(url);
                            await db.ScheduleReceipts.DeleteAsync(url);
                            continue;
                        }
                        foreach (var member in members)
                        {
                            if (!CollectorRegistry.IsValidId(member.Id)) throw new InvalidDataException("Некорректный ID съёмника.");
                            var entry = new CollectorRegistration { Id = member.Id, Url = PlanBuilder.Address(member.Url) };
                            await db.Collectors.UpsertAsync(entry);
                            queue.Enqueue(entry);
                        }
                        await schedules.ImportAsync(record, ct);
                        var client = await clients.GetByRequestInfoAsync(url);
                        bool fresh = client == null;
                        client ??= new Client { RequestInfo = url };
                        client.CollectorId = members[0].Id;
                        client.Name = record.Name;
                        if (fresh) await clients.InsertAsync(client);
                        else await clients.UpdateAsync(client);
                        await clients.RemoveCleanupAsync(url);
                        restored++;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && IsRemoteError(ex))
                    { unavailable.Add(url); }
                }
            }
        }
        finally { gate.Release(); }
        return new(restored, visited.Count, unavailable);
    }
    private static bool IsRemoteError(Exception ex) => ex is HttpRequestException or OperationCanceledException or
        InvalidDataException or Flurl.Http.FlurlHttpException or System.Text.Json.JsonException;
    /// <summary>Возвращает локальные назначения и состояние доставки.</summary>
    public async Task<ProbeRouteRecord[]> ListAsync() => (await db.ProbeRoutes.FindAllAsync()).ToArray();

    /// <summary>Проверяет версию оператора, подготавливает съёмники и применяет назначение.</summary>
    public async Task<ProbeRouteRecord> SetAsync(string url, string[] ids, long expected, bool delete, CancellationToken ct)
    {
        if (keeper.Enabled) throw new ArgumentException("Назначения Keeper управляются в реестре HA.");
        _ = PlanBuilder.Address(url);
        if (ids.Length > 3 || ids.Distinct().Count() != ids.Length || (!delete && ids.Length == 0))
            throw new ArgumentException("Выберите основной и до двух разных резервных съёмников.");
        await gate.WaitAsync(ct);
        try
        {
            var client = await clients.GetByRequestInfoAsync(url) ?? throw new ArgumentException("Сначала подтвердите пробу.");
            var old = await db.ProbeRoutes.FindByIdAsync(url);
            var nodes = (await registry.ListAsync()).ToDictionary(n => n.Id);
            var members = ids.Select(id => nodes.TryGetValue(id, out var n) ? new CollectorPeer(n.Id, PlanBuilder.Address(n.Url))
                : throw new ArgumentException("Неизвестный съёмник: " + id)).ToArray();
            if (members.DistinctBy(n => n.Url).Count() != members.Length) throw new ArgumentException("Адреса съёмников должны различаться.");
            if ((old?.Revision ?? 0) != expected) throw new InvalidOperationException(Outdated);
            var actual = await ReadRoute(url, ct);
            bool pendingBase = old is { Applied: false } && old.BaseRevision == actual.Revision;
            if (actual.Revision != expected && !pendingBase || old?.Conflict == true)
                throw new InvalidOperationException(Outdated);
            if (old != null && old.Members.SequenceEqual(members) && old.Name == client.Name)
            {
                if (!old.Applied) await Distribute(old, ct);
                return old;
            }
            var record = new ProbeRouteRecord
            {
                Probe = url,
                Name = client.Name,
                BaseRevision = actual.Revision,
                Revision = Math.Max(checked(expected + 1), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1024 + Random.Shared.Next(1024)),
                Members = members,
                Retired = (old?.Members ?? []).Concat(old?.Retired ?? []).Except(members).Distinct().ToArray()
            };
            await db.ProbeRoutes.UpsertAsync(record);
            await Distribute(record, ct);
            return record;
        }
        finally { gate.Release(); }
    }

    private async Task Send(CollectorPeer member, CollectorRoute route, CancellationToken ct)
    {
        using var http = registry.Connect(new Uri(member.Url));
        using var response = await http.PostAsJsonAsync("peer/route?recipient=" + Uri.EscapeDataString(member.Id), route, ct);
        response.EnsureSuccessStatusCode();
        var receipt = await response.Content.ReadFromJsonAsync<Receipt>(ct);
        if (receipt?.Id != member.Id || receipt.Revision != route.Revision) throw new InvalidDataException("Изменился ID съёмника " + member.Id);
    }

    private async Task Distribute(ProbeRouteRecord record, CancellationToken ct)
    {
        var route = new CollectorRoute(record.Probe, record.Revision, record.Members);
        if (!record.Applied)
        {
            var live = await ReadRoute(record.Probe, ct);
            if (live.Revision != record.BaseRevision && live.Revision != record.Revision)
            {
                record.Conflict = true;
                await db.ProbeRoutes.UpdateAsync(record);
                throw new InvalidOperationException(Outdated);
            }
            await Task.WhenAll(record.Members.Select(member => Send(member, route, ct)));
            using var probe = registry.Connect(new Uri(record.Probe));
            using var response = await probe.PostAsJsonAsync("api/probeinterface/collectorroute",
                new { record.Revision, record.Name, ExpectedRevision = record.BaseRevision, Members = record.Members.Select(m => m.Id), Peers = record.Members }, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                record.Conflict = true;
                await db.ProbeRoutes.UpdateAsync(record);
                throw new InvalidOperationException(Outdated);
            }
            response.EnsureSuccessStatusCode();
            if (await response.Content.ReadFromJsonAsync<long>(ct) != record.Revision) throw new InvalidDataException("Проба не приняла назначение.");
            record.Applied = true;
            await db.ProbeRoutes.UpdateAsync(record);
            if (record.Members.Length == 0) await db.ScheduleReceipts.DeleteAsync(record.Probe);
        }
        // The probe already rejects the old revision, even if a removed peer stays offline.
        foreach (var member in record.Retired.ToArray())
        {
            try { await Send(member, route, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or InvalidDataException) { continue; }
            record.Retired = record.Retired.Where(n => n != member).ToArray();
            await db.ProbeRoutes.UpdateAsync(record);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (keeper.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(12));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await gate.WaitAsync(ct);
            try
            {
                foreach (var record in (await ListAsync()).Where(r => !r.Conflict && (!r.Applied || r.Retired.Length != 0)))
                    try { await Distribute(record, ct); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Назначение {Probe}: {Reason}", record.Probe, ex.Message); }
            }
            finally { gate.Release(); }
        }
    }
}
