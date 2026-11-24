using SPI.Twamp.HighAvailability;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.BackgroundServices;

// Reconcile the management database from the versioned, atomic assignment registry after restart.
internal sealed class HaRegistrySync(KeeperPlanStore keeper, IClientService clients, ILogger<HaRegistrySync> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!keeper.Enabled) return;
        int synced = -1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var current = await keeper.ReadAsync(ct);
                if (current.Version != synced)
                {
                    var known = (await clients.GetClientsAsync()).ToDictionary(p => p.RequestInfo);
                    foreach (var a in current.Plan.Assignments)
                    {
                        if (!known.TryGetValue(a.Probe.Url, out var probe) || probe.Name != a.Probe.Name || probe.CollectorId != a.Primary)
                            await clients.SetInfoAsync(new Client { RequestInfo = a.Probe.Url, Name = a.Probe.Name, CollectorId = a.Primary }, ct);
                    }
                    foreach (string url in current.Plan.Removed)
                        if (known.ContainsKey(url)) await clients.DeleteAsync(url, false, ct);
                    synced = current.Version;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Сверка реестра HA: {Reason}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
}
