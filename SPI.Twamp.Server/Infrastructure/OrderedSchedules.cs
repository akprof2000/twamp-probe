using System.Collections.Concurrent;
using System.Security.Cryptography;
using JsonSerializer = System.Text.Json.JsonSerializer;
using LiteDB;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Contracts;

namespace SPI.Twamp.Server.Infrastructure;

/// <summary>Доставленные определения расписаний по идентификатору задачи.</summary>
public sealed class ScheduleReceipt
{
    /// <summary>Адрес пробы, подтвердившей расписание.</summary>
    [BsonId] public string Probe { get; set; } = "";
    /// <summary>Отпечатки локальных определений, подтверждённых пробой.</summary>
    public Dictionary<string, string> Tasks { get; set; } = new();
    /// <summary>Версия полного расписания на момент последней доставки или чтения.</summary>
    public string? ETag { get; set; }
}

/// <summary>Отправляет изменения расписаний автономной пробе и хранит квитанцию после успешного SetJobs.</summary>
public sealed class OrderedSchedules(LiteDbContext db, ITaskRepository tasks, CollectorRegistry registry)
{
    private static string Tag(HttpResponseMessage response) => response.Headers.ETag?.ToString()
        ?? throw new InvalidDataException("Проба не подтвердила версию расписания.");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    private static string Fingerprint(TaskInfo task) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(task)));

    /// <summary>Перечитывает расписание с действующей пробы без отправки заданий.</summary>
    public async Task ImportAsync(ProbeRouteRecord record, CancellationToken ct)
    {
        var url = record.Probe;
        var gate = locks.GetOrAdd(url, _ => new(1));
        await gate.WaitAsync(ct);
        try
        {
            using var http = registry.Connect(new Uri(url));
            using var response = await http.GetAsync("api/probeinterface/tasks", ct);
            response.EnsureSuccessStatusCode();
            var rows = await response.Content.ReadFromJsonAsync<TaskInfo[]>(ct) ?? throw new InvalidDataException();
            var live = rows.Where(t => t.Type == TaskType.Scheduler && !t.Delete).ToArray();
            foreach (var task in live) task.RequestInfo = url;
            await tasks.UpsertRangeAsync(live);
            var ids = live.Select(t => t.Id).ToHashSet();
            var receipt = new ScheduleReceipt { Probe = url, ETag = Tag(response) };
            foreach (var task in (await tasks.GetByRequestInfoAsync(url)).Where(t => t.Type == TaskType.Scheduler))
            {
                if (ids.Contains(task.Id)) receipt.Tasks[task.Id.ToString("N")] = Fingerprint(task);
                else await tasks.RemoveAsync(task.Id);
            }
            await db.ScheduleReceipts.UpsertAsync(receipt);
            await db.ProbeRoutes.UpsertAsync(record);
        }
        finally { gate.Release(); }
    }
    /// <summary>Проверяет, применено ли автономное назначение.</summary>
    public async Task<bool> IsRouted(string url) => await db.ProbeRoutes.FindByIdAsync(url) is { Applied: true, Members.Length: > 0 };

    /// <summary>Отправляет только местные правки расписания по подтверждённой версии.</summary>
    public async Task<bool> SyncIfRouted(string url, CancellationToken ct)
    {
        var route = await db.ProbeRoutes.FindByIdAsync(url);
        if (route is not { Applied: true, Members.Length: > 0 }) return false;
        var gate = locks.GetOrAdd(url, _ => new(1));
        await gate.WaitAsync(ct);
        try
        {
            var previous = await db.ScheduleReceipts.FindByIdAsync(url) ?? new ScheduleReceipt { Probe = url };
            var next = new ScheduleReceipt { Probe = url };
            var changes = new List<TaskInfo>();
            foreach (var task in (await tasks.GetByRequestInfoAsync(url)).Where(t => t.Type == TaskType.Scheduler))
            {
                string id = task.Id.ToString("N");
                string hash = Fingerprint(task);
                next.Tasks[id] = hash;
                if (previous.Tasks.GetValueOrDefault(id) != hash) changes.Add(task);
            }
            foreach (var removed in previous.Tasks.Keys.Except(next.Tasks.Keys))
                changes.Add(new TaskInfo { Id = Guid.ParseExact(removed, "N"), RequestInfo = url, Type = TaskType.Scheduler, Delete = true });
            if (changes.Count > 0)
            {
                using var http = registry.Connect(new Uri(url));
                string? tag = previous.ETag;
                if (tag == null)
                {
                    using var current = await http.GetAsync("api/probeinterface/tasks", ct);
                    current.EnsureSuccessStatusCode();
                    tag = Tag(current);
                }
                using var req = new HttpRequestMessage(HttpMethod.Post, "api/probeinterface/setjobs") { Content = JsonContent.Create(changes) };
                req.Headers.TryAddWithoutValidation("If-Match", tag);
                req.Headers.Add("X-Twamp-Route-Revision", route.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
                using var sent = await http.SendAsync(req, ct);
                if (sent.StatusCode == System.Net.HttpStatusCode.Conflict)
                    throw new ScheduleConflictException();
                sent.EnsureSuccessStatusCode();
                next.ETag = Tag(sent);
                await db.ScheduleReceipts.UpsertAsync(next);
            }
            return true;
        }
        finally { gate.Release(); }
    }
}

/// <summary>Расписание или назначение на пробе изменено другим конфигуратором.</summary>
public sealed class ScheduleConflictException() : Exception("Расписание изменилось. Откройте конфигурацию со съёмника и повторите правку.");
