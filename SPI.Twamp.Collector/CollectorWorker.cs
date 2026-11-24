using System.Collections.Concurrent;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.HighAvailability;
using System.Diagnostics;
using System.Text.Json;

namespace SPI.Twamp.Collector;

public sealed class CollectorWorker(DeliveryBuffer buffer, IProbeClient probe, IClickHouseWriter writer,
    IHttpClientFactory http, IConfiguration settings, ILogger<CollectorWorker> log, KeeperPlanStore keeper) : BackgroundService
{
    private volatile CollectorCatalog _catalog = new([]);
    private readonly Dictionary<string,Task> _workers = new();
    private readonly ConcurrentDictionary<string,ProbePollState> _states = new();
    private readonly ConcurrentDictionary<Guid,TaskLastResult> _results = new();
    private readonly Guid _instance = Guid.NewGuid();
    private long _version, _uploaded, _rows;
    private readonly TimeSpan _retry = TimeSpan.FromSeconds(Math.Max(1,settings.GetValue("ClickHouse:RetrySeconds",5)));
    private readonly object _storage = new();
    private DateTime? _lastUpload, _lastFailure;
    private string? _error;
    private readonly string _id = settings["Collector:Id"] ?? "default";
    private readonly string _machine = settings["Collector:Machine"] is {Length:>0} machine ? machine : Environment.MachineName;
    private volatile VersionedPlan _plan = new(-1,CollectionPlan.Empty);

    public CollectorSnapshot? Snapshot(Guid? instance,long? version)
    {
        long current = Interlocked.Read(ref _version);
        lock(_storage)
            return instance == _instance && version == current ? null :
                new(_instance, current, new(_states), new(_results),_lastUpload);
    }

    public ClickHouseState Storage(ClickHouseState legacy)
    {
        var pending = buffer.PendingFiles();
        lock (_storage)
            return legacy with {
                AwaitingConfirmation = buffer.AwaitingConfirmation,
                PendingRows = legacy.PendingRows + pending.Sum(p => (long)DeliveryBuffer.RowCount(p)),
                SealedSegments = legacy.SealedSegments + pending.Length,
                LastUploadAt = _lastUpload ?? legacy.LastUploadAt,
                Online = _error != null ? false : _lastUpload != null || legacy.Online == true ? true : legacy.Online,
                LastError = _error ?? legacy.LastError, LastErrorAt = _lastFailure ?? legacy.LastErrorAt,
                SegmentsUploaded = _uploaded + legacy.SegmentsUploaded, RowsUploaded = _rows + legacy.RowsUploaded
            };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (keeper.Enabled) { await RunClusterAsync(ct); return; }
        using var client = http.CreateClient("configurator");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Resume saved batches even if the configurator or the probe has gone offline.
                foreach (var url in buffer.PendingProbes()) StartWorker(url, ct);
                try
                {
                    _catalog = await client.GetFromJsonAsync<CollectorCatalog>("api/collector/catalog?collectorId=" + Uri.EscapeDataString(settings["Collector:Id"] ?? "default"), ct)
                        ?? throw new InvalidDataException("Конфигуратор вернул пустой каталог");
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _catalog = new([]);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                { log.LogWarning("Каталог проб недоступен: {Reason}", ex.Message); }
                foreach (var url in _catalog.Probes) StartWorker(url, ct);
                foreach (var url in _workers.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
                {
                    await _workers[url];
                    _workers.Remove(url);
                    _states.TryRemove(url, out _);
                    Interlocked.Increment(ref _version);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        finally { await Task.WhenAll(_workers.Values); }
    }

    private async Task RunClusterAsync(CancellationToken ct)
    {
        string file = Path.Combine(settings["ClickHouse:SpoolPath"] ?? "spool", "applied-plan.json");
        try
        {
            if(File.Exists(file)) _plan=JsonSerializer.Deserialize<VersionedPlan>(await File.ReadAllBytesAsync(file,ct)) ?? _plan;
        }
        catch(Exception ex) when(ex is IOException or JsonException) {log.LogWarning("Локальный план будет восстановлен из Keeper: {Reason}",ex.Message);}
        bool verified=false;
        var active = new Dictionary<string,(long Generation, CancellationTokenSource Stop, Task Work)>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var snapshot = await keeper.ReadAsync(ct);
                    if (!verified || _plan.Version != snapshot.Version)
                    {
                        await File.WriteAllBytesAsync(file+".tmp",JsonSerializer.SerializeToUtf8Bytes(snapshot),ct);
                        File.Move(file+".tmp",file,true);
                        _plan = snapshot;
                        verified=true;
                    }
                    var mine = snapshot.Plan.Assignments.Where(a=>Eligible(snapshot,a)).ToDictionary(a=>a.Probe.Url);
                    foreach (var (url,run) in active.ToArray())
                    {
                        if (!mine.TryGetValue(url,out var row) || row.Generation!=run.Generation)
                            await run.Stop.CancelAsync();
                        if (!run.Work.IsCompleted) continue;
                        try { await run.Work; } catch(OperationCanceledException) when(run.Stop.IsCancellationRequested) { }
                        run.Stop.Dispose();active.Remove(url);
                    }
                    foreach (var a in mine.Values.Where(a=>!active.ContainsKey(a.Probe.Url)))
                    {
                        var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
                        active.Add(a.Probe.Url,(a.Generation,stop,CollectWithLeaseAsync(a,stop.Token)));
                    }
                }
                catch(Exception ex) when(!ct.IsCancellationRequested) {log.LogWarning("План Keeper недоступен: {Reason}",ex.Message);}
                await Task.Delay(TimeSpan.FromSeconds(2),ct);
            }
        }
        finally
        {
            foreach(var run in active.Values) await run.Stop.CancelAsync();
            try { await Task.WhenAll(active.Values.Select(r=>r.Work)); } catch(OperationCanceledException) { }
            foreach(var run in active.Values) run.Stop.Dispose();
        }
    }
    private bool Eligible(VersionedPlan plan, Assignment assignment) => assignment.Probe.Contains(_id) &&
        plan.Plan.Collectors.Any(n=>n.Id==_id && string.Equals(n.Machine,_machine,StringComparison.OrdinalIgnoreCase));

    private async Task CollectWithLeaseAsync(Assignment assignment,CancellationToken ct)
    {
        string url=assignment.Probe.Url;
        var grace=TimeSpan.FromSeconds(Math.Max(2,settings.GetValue("HighAvailability:StandbyDelaySeconds",6)));
        if(assignment.Primary!=_id) await Task.Delay(grace,ct);
        while(!ct.IsCancellationRequested)
        {
            KeeperPlanStore.Lease? lease=null;
            bool failed=false;
            try
            {
                lease=await keeper.AcquireAsync(url,_id,_plan.Version,ct);
                if(lease!=null)
                {
                    _states[url]=new(null,null,null,0,0,_id);
                    Interlocked.Increment(ref _version);
                    await RunProbeAsync(url,ct,lease,assignment.Generation);
                }
            }
            catch(Exception ex) when(!ct.IsCancellationRequested)
            {
                failed=true;
                log.LogWarning("Передача владения {Probe}: {Reason}",url,ex.Message);
            }
            finally
            {
                if(lease!=null)
                {
                    _states.TryRemove(url,out _);Interlocked.Increment(ref _version);
                    try {await keeper.ReleaseAsync(lease);}
                    catch(Exception ex) {log.LogWarning("Освобождение {Probe}: {Reason}",url,ex.Message);}
                }
            }
            await Task.Delay(failed ? grace+grace : TimeSpan.FromSeconds(1),ct);
        }
    }

    private async Task<Dictionary<Guid,string>> GetLegacyTitlesAsync(ProbeResultBatch batch, CancellationToken ct)
    {
        var ids = batch.Items.Where(item => item.TaskTitle == null).Select(item => item.TaskId).Distinct().ToArray();
        if (ids.Length == 0) return new();
        using var client = http.CreateClient("configurator");
        using var response = await client.PostAsJsonAsync("api/collector/titles", ids, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Dictionary<Guid,string>>(ct)
            ?? throw new InvalidDataException("Конфигуратор вернул пустой ответ со списком задач");
    }

    private void StartWorker(string url, CancellationToken ct)
    {
        if (!_workers.ContainsKey(url)) _workers[url] = RunProbeAsync(url, ct);
    }

    private async Task RunProbeAsync(string url, CancellationToken ct, KeeperPlanStore.Lease? lease = null,long generation = 0)
    {
        long received = 0;
        bool schemaReady = false;
        var since=Stopwatch.StartNew();
        double rate=0;
        async Task Guard()
        {
            if(lease==null)return;
            var plan=_plan;
            if(!plan.Plan.Assignments.Any(a=>a.Probe.Url==url && a.Generation==generation && Eligible(plan,a)))
                throw new IOException("Назначение пробы изменилось.");
            await keeper.GuardAsync(lease,plan.Version,ct);
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var path = buffer.Peek(url);
                if (path == null)
                {
                    var catalog = _catalog;
                    if (lease==null && !catalog.Probes.Contains(url)) return;
                    await Guard();
                    var batch = await probe.GetResultsAsync(url, ct);
                    if (batch.Items.Length > 0)
                    {
                        Dictionary<Guid,string> titles;
                        try { titles = await GetLegacyTitlesAsync(batch,ct); }
                        catch(Exception ex) when(lease!=null && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
                        { titles=new(); log.LogDebug("Имена старых задач временно недоступны для {Probe}",url); }
                        var parse = Stopwatch.StartNew();
                        path = await buffer.StageAsync(url, batch, titles, ct,lease!=null);
                        double cost = batch.Items.Length + new FileInfo(path).Length/4096d + parse.Elapsed.TotalMilliseconds;
                        var sample=cost/Math.Max(1,since.Elapsed.TotalSeconds);
                        rate=rate==0?sample:0.8*rate+0.2*sample;
                        since.Restart();
                        foreach (var item in batch.Items)
                        {
                            var error = item.ErrorConsole ?? "";
                            var hasError = string.IsNullOrEmpty(item.Outcome)
                                ? error.Length > 0 || item.ExitCode is not (null or 0) : item.Outcome != "Success";
                            _results[item.TaskId] = new(item.Creation ?? DateTime.Now, hasError, item.Outcome,
                                item.ExitCode, error.Length > 300 ? error[..300] : error);
                        }
                        received += batch.Items.Length;
                    }
                }
                if (path != null)
                {
                    if (lease!=null ? !DeliveryBuffer.IsQuorumStored(path) : !DeliveryBuffer.IsStored(path))
                    {
                        try
                        {
                            if (!schemaReady) { await writer.EnsureTableAsync(ct); schemaReady = true; }
                            await Guard();
                            path = await DeliveryBuffer.StoreAsync(path, writer, ct,lease!=null);
                            lock (_storage)
                            {
                                _lastUpload = DateTime.Now; _error = null;
                                _uploaded++; _rows += DeliveryBuffer.RowCount(path);
                            }
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            schemaReady = false;
                            lock (_storage) { _lastFailure = DateTime.Now; _error = ex.Message; }
                            throw;
                        }
                    }
                    await Guard();
                    await DeliveryBuffer.AcknowledgeAsync(path, url, probe, ct);
                }
                _states[url] = new(DateTime.Now, null, null, received, 0,_id,rate);
                Interlocked.Increment(ref _version);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _states.TryGetValue(url, out var previous);
                _states[url] = new(previous?.LastSuccess, DateTime.Now, ex.Message, received, (int)_retry.TotalSeconds);
                Interlocked.Increment(ref _version);
                log.LogWarning("Доставка с {Probe}: {Reason}", url, ex.Message);
                if(lease!=null) throw;
                await Task.Delay(_retry, ct);
            }
        }
    }
}
