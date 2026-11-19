using System.Collections.Concurrent;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Collector;

public sealed class CollectorWorker(DeliveryBuffer buffer, IProbeClient probe, IClickHouseWriter writer,
    IHttpClientFactory http, IConfiguration settings, ILogger<CollectorWorker> log) : BackgroundService
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

    public CollectorSnapshot? Snapshot(Guid? instance,long? version)
    {
        long current = Interlocked.Read(ref _version);
        return instance == _instance && version == current ? null :
            new(_instance, current, new(_states), new(_results));
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

    private async Task RunProbeAsync(string url, CancellationToken ct)
    {
        long received = 0;
        bool schemaReady = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var path = buffer.Peek(url);
                if (path == null)
                {
                    var catalog = _catalog;
                    if (!catalog.Probes.Contains(url)) return;
                    var batch = await probe.GetResultsAsync(url, ct);
                    if (batch.Items.Length > 0)
                    {
                        var titles = await GetLegacyTitlesAsync(batch, ct);
                        path = await buffer.StageAsync(url, batch, titles, ct);
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
                    if (!DeliveryBuffer.IsStored(path))
                    {
                        try
                        {
                            if (!schemaReady) { await writer.EnsureTableAsync(ct); schemaReady = true; }
                            path = await DeliveryBuffer.StoreAsync(path, writer, ct);
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
                    await DeliveryBuffer.AcknowledgeAsync(path, url, probe, ct);
                }
                _states[url] = new(DateTime.Now, null, null, received, 0);
                Interlocked.Increment(ref _version);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _states.TryGetValue(url, out var previous);
                _states[url] = new(previous?.LastSuccess, DateTime.Now, ex.Message, received, (int)_retry.TotalSeconds);
                Interlocked.Increment(ref _version);
                log.LogWarning("Доставка с {Probe}: {Reason}", url, ex.Message);
                await Task.Delay(_retry, ct);
            }
        }
    }
}
