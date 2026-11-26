using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;
using SPI.Twamp.Server.Abstractions;

namespace SPI.Twamp.Collector;

// Peer readiness implements priority. Only the token returned by the probe permits delivery.
public sealed class OrderedCollectors(LocalRoutes routes, CollectorWorker worker, IConfiguration config,
    IHttpClientFactory clients, IClickHouseWriter writer, IHostApplicationLifetime lifetime, ILogger<OrderedCollectors> log) : BackgroundService
{
    private readonly string id = config["Collector:Id"] ?? "default";
    private readonly string instance = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<(string Url, long Revision), long> readiness = new();
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private readonly object storageGate = new();
    private long lastStorageCheck;
    private Task? storageCheck;

    private Task CheckStorage(CancellationToken ct)
    {
        lock (storageGate)
        {
            if (storageCheck == null || storageCheck.IsCompleted && Environment.TickCount64 - lastStorageCheck >= 4000)
            {
                lastStorageCheck = Environment.TickCount64;
                storageCheck = writer.CheckAvailableAsync(lifetime.ApplicationStopping);
            }
            return storageCheck.WaitAsync(ct);
        }
    }
    private sealed record Grant(string Token);
    private sealed record ProbeRevision(long Revision);
    public PeerPresence Presence(string probe)
    {
        var rev = routes.Active(probe);
        bool ready = readiness.TryGetValue((probe, rev), out var seen)
            && Environment.TickCount64 - seen < 15000;
        return new(id, rev, ready);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (config.GetValue("HighAvailability:Enabled", false)) return;
        var running = new Dictionary<(string Url, long Rev), (CancellationTokenSource Stop, Task Work)>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var mine = routes.All().Where(r => r.Members.Any(m => m.Id == id)).ToDictionary(r => (r.Probe, r.Revision));
                foreach (var (url, run) in running.ToArray())
                {
                    if (!mine.ContainsKey(url)) await run.Stop.CancelAsync();
                    if (!run.Work.IsCompleted) continue;
                    try { await run.Work; }
                    catch (Exception) when (run.Stop.IsCancellationRequested) { }
                    catch (Exception ex) { log.LogError(ex, "Перезапуск опроса {Probe}", url.Url); }
                    run.Stop.Dispose(); running.Remove(url);
                }
                foreach (var route in mine.Values.Where(r => !running.ContainsKey((r.Probe, r.Revision))))
                {
                    var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    running[(route.Probe, route.Revision)] = (stop, RunRoute(route, stop.Token));
                }
                await Task.Delay(Tick, ct);
            }
        }
        finally
        {
            foreach (var run in running.Values) await run.Stop.CancelAsync();
            try { await Task.WhenAll(running.Values.Select(r => r.Work)); } catch (Exception) when (ct.IsCancellationRequested) { }
            foreach (var run in running.Values) run.Stop.Dispose();
        }
    }

    private async Task<bool[]> ReadPeers(HttpClient client, CollectorRoute route, CancellationToken ct)
    {
        var checks = route.Members.TakeWhile(m => m.Id != id).Select(async peer =>
        {
            try
            {
                var p = await client.GetFromJsonAsync<PeerPresence>(peer.Url + "/peer/presence?probe=" + Uri.EscapeDataString(route.Probe), ct);
                return p?.Id == peer.Id && p.Revision == route.Revision && p.Ready;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { return false; }
        });
        return await Task.WhenAll(checks);
    }

    private async Task<string?> Change(HttpClient client, CollectorRoute route, string action, string? token, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(route.Probe.TrimEnd('/') + "/api/probeinterface/collectorlease",
            new { action, collector = id, instance, token, route.Revision }, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Grant>(ct))?.Token;
    }

    private async Task RunRoute(CollectorRoute route, CancellationToken ct)
    {
        using var client = clients.CreateClient("peers");
        var endpoint = route.Probe.TrimEnd('/') + "/api/probeinterface/collectorlease";
        var priority = new CollectorPriority(Array.FindIndex(route.Members, m => m.Id == id));
        while (!ct.IsCancellationRequested)
        {
            long cycleStarted = Environment.TickCount64;
            var delay = Tick;
            try
            {
                var probe = await client.GetFromJsonAsync<ProbeRevision>(endpoint, ct);
                if (probe != null) routes.Observe(route.Probe, probe.Revision);
                if (probe?.Revision == route.Revision)
                {
                    await CheckStorage(ct);
                    readiness[(route.Probe, route.Revision)] = Environment.TickCount64;
                    if (priority.MayCollect(await ReadPeers(client, route, ct)) && await Change(client, route, "acquire", null, ct) is { Length: > 0 } token)
                        await Collect(client, route, token, priority, ct);
                }
                else readiness.TryRemove((route.Probe, route.Revision), out _);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                readiness.TryRemove((route.Probe, route.Revision), out _);
                log.LogWarning("Сбор {Probe} временно передан резерву: {Reason}", route.Probe, ex.Message);
                delay = TimeSpan.FromSeconds(12);
            }
            var remaining = delay - TimeSpan.FromMilliseconds(Environment.TickCount64 - cycleStarted);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        }
    }

    private async Task Collect(HttpClient client, CollectorRoute route, string token, CollectorPriority priority, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Guard()
        {
            if (routes.Active(route.Probe) != route.Revision || await Change(client, route, "renew", token, stop.Token) != token)
                throw new IOException("Изменилось право сбора.");
        }
        var work = worker.RunOrderedAsync(route.Probe, token, Guard, stop.Token);
        try
        {
            while (!work.IsCompleted)
            {
                if (await Task.WhenAny(work, Task.Delay(Tick, stop.Token)) == work) break;
                await CheckStorage(stop.Token);
                await Guard();
                readiness[(route.Probe, route.Revision)] = Environment.TickCount64;
                var peers = await ReadPeers(client, route, stop.Token);
                if (!priority.MayCollect(peers)) break;
            }
        }
        finally
        {
            bool interrupted = !work.IsCompleted || ct.IsCancellationRequested;
            await stop.CancelAsync();
            try { await work; }
            catch (Exception) when (interrupted) { }
            finally
            {
                try { await Change(client, route, "release", token, CancellationToken.None); }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) { }
            }
        }
    }
}
