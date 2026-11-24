using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;

namespace SPI.Twamp.Server.BackgroundServices;

/// <summary>Собирает снимки съёмников и публикует состояние активных проб.</summary>
public sealed class CollectorMonitor(CollectorRegistry registry, IClientRepository probes, SPI.Twamp.HighAvailability.KeeperPlanStore keeper,
    IChangeNotifier changes, ILogger<CollectorMonitor> log) : BackgroundService, IProbeStatusProvider
{
    private volatile CollectorSnapshot _snapshot = new(Guid.Empty, 0, new(), new());
    /// <summary>Доступность HTTP API съёмника и время его последней выгрузки.</summary>
    public sealed record Health(bool Online, DateTime? LastUploadAt);
    private volatile Dictionary<string, Health> _health = new();
    /// <summary>Возвращает последнее известное состояние зарегистрированных съёмников.</summary>
    public IReadOnlyDictionary<string, Health> GetHealth() => _health;
    /// <inheritdoc/>
    public IReadOnlyDictionary<string, ProbePollState> GetStates() => _snapshot.States;
    /// <inheritdoc/>
    public IReadOnlyDictionary<Guid, TaskLastResult> GetLastResults() => _snapshot.LastResults;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var previous = new Dictionary<string, CollectorSnapshot>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var assignments = (await probes.GetAllAsync()).ToDictionary(p => p.RequestInfo,
                    p => p.CollectorId ?? CollectorRegistry.DefaultId, StringComparer.Ordinal);
                var entries = await registry.ListAsync();
                var responses = await Task.WhenAll(entries.Select(async entry =>
                    (entry.Id, Snapshot: await ReadAsync(entry, previous.GetValueOrDefault(entry.Id), ct))));
                var current = responses.Where(r => r.Snapshot != null).ToDictionary(r => r.Id, r => r.Snapshot!);
                _health = responses.ToDictionary(r => r.Id, r => new Health(r.Snapshot != null, r.Snapshot?.LastUploadAt ?? _health.GetValueOrDefault(r.Id)?.LastUploadAt));
                var states = new Dictionary<string, ProbePollState>();
                var results = new Dictionary<Guid, TaskLastResult>();
                foreach (var (id, snapshot) in current)
                {
                    foreach (var (url, state) in snapshot.States)
                        if (keeper.Enabled ? state.Owner == id : assignments.GetValueOrDefault(url) == id)
                            if (!states.TryGetValue(url, out var prior) || (state.LastSuccess ?? DateTime.MinValue) >= (prior.LastSuccess ?? DateTime.MinValue)) states[url] = state;
                    foreach (var (task, result) in snapshot.LastResults)
                        if (!results.TryGetValue(task, out var old) || result.Time > old.Time) results[task] = result;
                }
                var last = _snapshot;
                if (states.Count != last.States.Count || results.Count != last.LastResults.Count ||
                    states.Any(pair => !Equals(pair.Value, last.States.GetValueOrDefault(pair.Key))) ||
                    results.Any(pair => !Equals(pair.Value, last.LastResults.GetValueOrDefault(pair.Key))))
                {
                    _snapshot = new(Guid.Empty, 0, states, results);
                    changes.Notify();
                }
                previous = current;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Мониторинг съёмников: {Reason}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task<CollectorSnapshot?> ReadAsync(CollectorRegistration entry, CollectorSnapshot? previous, CancellationToken ct)
    {
        try
        {
            using var client = registry.Connect(new Uri(entry.Url));
            string path = previous == null ? "snapshot" : $"snapshot?instance={previous.Instance}&version={previous.Version}";
            using var response = await client.GetAsync(path, ct);
            response.EnsureSuccessStatusCode();
            return response.StatusCode == System.Net.HttpStatusCode.NoContent ? previous :
                await response.Content.ReadFromJsonAsync<CollectorSnapshot>(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogDebug(ex, "Съёмник {Collector} недоступен", entry.Id);
            return null;
        }
    }
}
