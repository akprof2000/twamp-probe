using LiteDB;
using SPI.Twamp.Server.Abstractions;

namespace SPI.Twamp.Server.Infrastructure;

/// <summary>Подключение съёмника к конфигуратору.</summary>
public sealed class CollectorRegistration
{
    /// <summary>Идентификатор из Collector:Id.</summary>
    [BsonId]
    public string Id { get; set; } = "";
    /// <summary>HTTP-адрес API съёмника.</summary>
    public string Url { get; set; } = "";
    /// <summary>Съёмник добавлен в постоянный каталог этого конфигуратора.</summary>
    [BsonIgnore] public bool Registered { get; set; }
}

/// <summary>Постоянный каталог съёмников с поддержкой прежнего Collector:Url.</summary>
public sealed class CollectorRegistry(LiteDbContext db, IConfiguration config, IHttpClientFactory http, SPI.Twamp.HighAvailability.KeeperPlanStore keeper)
{
    /// <summary>Прежние пробы относятся к съёмнику default.</summary>
    public const string DefaultId = "default";

    /// <summary>Возвращает зарегистрированные съёмники.</summary>
    public async Task<CollectorRegistration[]> ListAsync()
    {
        if(keeper.Enabled)
            return (await keeper.ReadAsync(CancellationToken.None)).Plan.Collectors.Select(c=>new CollectorRegistration{ Id=c.Id,Url=c.Url }).ToArray();
        var entries = (await db.Collectors.FindAllAsync()).ToList();
        foreach (var entry in entries) entry.Registered = true;
        if (entries.All(c => c.Id != DefaultId) && (entries.Count == 0 ||
            (await db.Clients.FindAllAsync()).Any(c => (c.CollectorId ?? DefaultId) == DefaultId)))
            entries.Add(new() { Id = DefaultId, Url = config["Collector:Url"] ?? "http://localhost:9001" });
        return entries.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Удаляет адрес из локального каталога после снятия всех назначений.</summary>
    public async Task<bool> RemoveAsync(string id)
    {
        if (keeper.Enabled) throw new InvalidOperationException("Используйте реестр HA.");
        var routes = (await db.ProbeRoutes.FindAllAsync()).ToArray();
        if (routes.Any(r => r.Members.Any(m => m.Id == id) || !r.Applied && r.Retired.Any(m => m.Id == id)))
            throw new InvalidOperationException("Сначала замените съёмник в назначениях проб.");
        var ordered = routes.Where(r => r.Applied).Select(r => r.Probe).ToHashSet();
        if ((await db.Clients.FindAllAsync()).Any(c => !ordered.Contains(c.RequestInfo) && (c.CollectorId ?? DefaultId) == id))
            throw new InvalidOperationException("Сначала измените съёмник в назначениях проб.");
        return await db.Collectors.DeleteAsync(id);
    }

    /// <summary>Проверяет идентичность и сохраняет адрес.</summary>
    public async Task<CollectorRegistration> RegisterAsync(Uri address, CancellationToken ct)
    {
        using var client = Connect(address);
        var identity = await client.GetFromJsonAsync<CollectorRegistration>("identity", ct)
            ?? throw new InvalidDataException("Съёмник не вернул идентификатор.");
        if (!IsValidId(identity.Id))
            throw new InvalidDataException("Идентификатор съёмника должен содержать 1–64 символа a–z, 0–9, дефис или подчёркивание.");
        identity.Url = address.AbsoluteUri.TrimEnd('/');
        await db.Collectors.UpsertAsync(identity);
        return identity;
    }

    /// <summary>Проверяет формат идентификатора.</summary>
    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 64 } &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    /// <summary>Открывает клиент с общим ключом API.</summary>
    public HttpClient Connect(Uri address)
    {
        var client = http.CreateClient("collector");
        client.BaseAddress = new Uri(address.AbsoluteUri.TrimEnd('/') + "/");
        return client;
    }

    /// <summary>Выбирает адрес съёмника по идентификатору.</summary>
    public async Task<HttpClient> ConnectAsync(string? id)
    {
        var entry = (await ListAsync()).FirstOrDefault(c => c.Id == (id ?? DefaultId))
            ?? throw new HttpRequestException("Съёмник не зарегистрирован.");
        return Connect(new Uri(entry.Url));
    }
}
