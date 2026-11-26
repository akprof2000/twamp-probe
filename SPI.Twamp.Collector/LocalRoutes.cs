using System.Text.Json;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Collector;

// Persist prepared revisions without replacing the active configuration before the probe commits.
public sealed class LocalRoutes
{
    private sealed record Entry(long Confirmed, CollectorRoute[] Routes);
    private readonly string path;
    private readonly object gate = new();
    private Dictionary<string, Entry> entries;

    public LocalRoutes(IConfiguration config)
    {
        path = Path.Combine(config["ClickHouse:SpoolPath"] ?? "spool", "collector-routes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Пустой файл назначений.") : new();
    }
    public CollectorRoute[] All() { lock (gate) return entries.Values.SelectMany(x => x.Routes).ToArray(); }
    public long Active(string url) { lock (gate) return entries.GetValueOrDefault(url)?.Confirmed ?? 0; }
    public bool Save(CollectorRoute route)
    {
        lock (gate)
        {
            var entry = entries.GetValueOrDefault(route.Probe) ?? new Entry(0, []);
            if (entry.Routes.FirstOrDefault(r => r.Revision == route.Revision) is { } old)
                return old.Members.SequenceEqual(route.Members);
            if (route.Revision < entry.Confirmed) return false;
            Store(route.Probe, entry with { Routes = [.. entry.Routes, route] });
            return true;
        }
    }
    public void Observe(string url, long revision)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(url, out var entry) || revision <= entry.Confirmed) return;
            Store(url, new(revision, entry.Routes.Where(r => r.Revision >= revision).ToArray()));
        }
    }
    private void Store(string url, Entry entry)
    {
        var next = new Dictionary<string, Entry>(entries) { [url] = entry };
        using (var file = File.Create(path + ".tmp")) { JsonSerializer.Serialize(file, next); file.Flush(true); }
        File.Move(path + ".tmp", path, true);
        entries = next;
    }
}
