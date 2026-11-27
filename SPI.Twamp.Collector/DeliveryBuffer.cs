using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;
using SPI.Twamp.Server.Parser;

namespace SPI.Twamp.Collector;

// Each probe exposes one immutable batch at a time. Its file is also the INSERT body.
public sealed class DeliveryBuffer : IDisposable
{
    private readonly string _root;
    private readonly FileStream _owner;
    public DeliveryBuffer(string spoolPath)
    {
        _root = Path.Combine(Path.GetFullPath(spoolPath), "delivery");
        Directory.CreateDirectory(_root);
        _owner = new FileStream(Path.Combine(_root, "owner.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private string Folder(string url) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))));

    public string[] PendingProbes() => Directory.GetDirectories(_root)
        .Where(dir => Directory.EnumerateFiles(dir, "*.ndjson").Any())
        .Select(dir => File.ReadAllText(Path.Combine(dir, "source"))).ToArray();

    public string? Peek(string url)
    {
        var dir = Folder(url);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.ndjson").SingleOrDefault() : null;
    }

    public async Task<string> StageAsync(string url, ProbeResultBatch batch,
        IReadOnlyDictionary<Guid,string> titles, CancellationToken ct, bool utc = false)
    {
        if (batch.BatchId == Guid.Empty || batch.Items.Length == 0)
            throw new InvalidDataException("Пустая пачка или идентификатор");
        string dir = Folder(url);
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "source");
        if (!File.Exists(source))
        {
            using (var file = File.Create(source + ".tmp"))
            {
                file.Write(Encoding.UTF8.GetBytes(url));
                file.Flush(true);
            }
            File.Move(source + ".tmp", source);
        }
        int count = 0;
        var temp = Path.Combine(dir, "staging.tmp");
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true);
            foreach (var item in batch.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (item.ResultId == Guid.Empty) throw new InvalidDataException("Результат без ResultId");
                if(utc && item.Creation is DateTime started)
                    item.Creation = started.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(started,DateTimeKind.Utc) : started.ToUniversalTime();
                var parsed = ProbeOutputParser.Parse(item.Mode, item.Console, item.ErrorConsole, item.TaskId);
                if (parsed.Count == 0) parsed.Add(new TwPingStats { Mode = item.Mode, Id = item.TaskId });
                for (int i = 0; i < parsed.Count; i++)
                {
                    var row = ExportRow.Create(item, parsed[i], i, item.TaskTitle ?? titles.GetValueOrDefault(item.TaskId) ?? "");
                    if (utc) row.Started = (item.Creation ?? DateTime.UnixEpoch).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture);
                    row.SourceProbe = url;
                    await writer.WriteLineAsync(JsonSerializer.Serialize(row, SpoolJson.Options).AsMemory(), ct);
                    count++;
                }
            }
            await writer.FlushAsync(ct);
            file.Flush(true);
        }
        string path = Path.Combine(dir, $"{batch.BatchId:N}.{count}.pending.ndjson");
        File.Move(temp, path);
        return path;
    }

    public static int RowCount(string path) => int.Parse(Path.GetFileName(path).Split('.')[1], CultureInfo.InvariantCulture);
    public static bool IsQuorumStored(string path) => path.EndsWith(".quorum.ndjson", StringComparison.Ordinal);
    public static bool IsStored(string path) => path.EndsWith(".stored.ndjson", StringComparison.Ordinal) || IsQuorumStored(path);

    // A failed INSERT leaves pending in place. A failed ACK leaves stored in place.
    public static async Task<(string Path, string Endpoint)> StoreAsync(string path, IClickHouseWriter writer, CancellationToken ct, bool quorum = false)
    {
        var endpoint = await writer.InsertSegmentAsync(path, ct);
        var stored = Path.ChangeExtension(Path.ChangeExtension(path, null), quorum ? "quorum.ndjson" : "stored.ndjson");
        File.Move(path, stored);
        return (stored, endpoint);
    }

    public static async Task AcknowledgeAsync(string path, string url, IProbeClient probe, CancellationToken ct)
    {
        if (!IsStored(path)) throw new InvalidOperationException("Пачка ещё не записана в ClickHouse");
        var batchId = Guid.ParseExact(Path.GetFileName(path).Split('.')[0], "N");
        await probe.ConfirmResultsAsync(url, batchId, ct);
        File.Delete(path);
    }

    public int AwaitingConfirmation => Directory.EnumerateFiles(_root, "*.ndjson", SearchOption.AllDirectories).Count(IsStored);

    public string[] PendingFiles() => Directory.GetFiles(_root, "*.pending.ndjson", SearchOption.AllDirectories);

    public async IAsyncEnumerable<ExportRow> ReadPendingAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var path in PendingFiles())
        {
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (FileNotFoundException) { continue; }
            using (stream)
            using (var reader = new StreamReader(stream))
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                    yield return JsonSerializer.Deserialize<ExportRow>(line, SpoolJson.Options)!;
            }
        }
    }
    public void Dispose() => _owner.Dispose();
}
