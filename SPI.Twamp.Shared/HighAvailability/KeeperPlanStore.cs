using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using org.apache.zookeeper;

namespace SPI.Twamp.HighAvailability;

// Versions are Keeper CAS versions. The ephemeral node and plan version are checked in one quorum operation.
public sealed class KeeperPlanStore(IConfiguration config) : IAsyncDisposable
{
    private ZooKeeper? session;
    private Guid registryId;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string root = config["HighAvailability:Root"] ?? "/twamp-ha";
    private readonly string? digest = config["HighAvailability:Digest"];
    public bool Enabled => config.GetValue("HighAvailability:Enabled", false);
    public string Namespace => root;
    private static List<org.apache.zookeeper.data.ACL> Permissions => ZooDefs.Ids.CREATOR_ALL_ACL;
    private string PlanPath => root + "/plan";
    private string LockPath(string url) => root + "/owners/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
    private sealed class Events : Watcher
    {
        public override Task process(WatchedEvent evt) => Task.CompletedTask;
    }
    private async Task<ZooKeeper> ConnectAsync(CancellationToken ct)
    {
        if (!Enabled) throw new InvalidOperationException("Кластерный режим выключен.");
        if (string.IsNullOrWhiteSpace(digest) || digest.IndexOf(':') <= 0 || digest.EndsWith(':'))
            throw new InvalidOperationException("Для HA задайте HighAvailability:Digest в формате пользователь:пароль.");
        await gate.WaitAsync(ct);
        try
        {
            if (session != null && session.getState() is not (ZooKeeper.States.CLOSED or ZooKeeper.States.AUTH_FAILED)) return session;
            if (root.Length < 2 || root[0] != '/' || root[1..].Contains('/')) throw new InvalidOperationException("HighAvailability:Root должен быть одним узлом Keeper.");
            var endpoints = config["HighAvailability:Keeper"] ?? throw new InvalidOperationException("Укажите HighAvailability:Keeper.");
            // Keeper accepts the standard fixed-width initial handshake. ZooKeeperNetEx defaults to an empty password field.
            var zk = new ZooKeeper(endpoints, Math.Clamp(config.GetValue("HighAvailability:SessionTimeoutMs", 15000), 2000, 120000), new Events(), 0, new byte[16]);
            zk.addAuthInfo("digest", Encoding.UTF8.GetBytes(digest));
            try
            {
                foreach (var (path, bytes) in new[] { (root, Array.Empty<byte>()), (root + "/identity", Guid.NewGuid().ToByteArray()), (root + "/owners", Array.Empty<byte>()), (PlanPath, JsonSerializer.SerializeToUtf8Bytes(CollectionPlan.Empty)) })
                {
                    try { await zk.createAsync(path, bytes, Permissions, CreateMode.PERSISTENT).WaitAsync(ct); }
                    catch (KeeperException.NodeExistsException) { }
                }
                registryId = new Guid((await zk.getDataAsync(root + "/identity").WaitAsync(ct)).Data);
                return session = zk;
            }
            catch { await zk.closeAsync(); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task<Guid> IdentityAsync(CancellationToken ct)
    {
        var zk = await ConnectAsync(ct);
        // A quorum read barrier also rejects an isolated collector during registration.
        await zk.multiAsync([Op.check(root + "/identity", 0)]).WaitAsync(ct);
        return registryId;
    }
    public async Task<VersionedPlan> ReadAsync(CancellationToken ct)
    {
        var zk = await ConnectAsync(ct);
        var data = await zk.getDataAsync(PlanPath).WaitAsync(ct);
        return new(data.Stat.getVersion(), JsonSerializer.Deserialize<CollectionPlan>(data.Data) ?? throw new InvalidDataException("Пустой план Keeper"));
    }
    public async Task<int> PublishAsync(int version, CollectionPlan plan, CancellationToken ct)
    {
        if (version < 0) throw new ArgumentException("Нужна версия исходного плана.");
        var body = JsonSerializer.SerializeToUtf8Bytes(plan);
        if (body.Length > 900_000) throw new ArgumentException("План превышает размер записи Keeper.");
        var zk = await ConnectAsync(ct);
        return (await zk.setDataAsync(PlanPath, body, version).WaitAsync(ct)).getVersion();
    }
    public sealed record Lease(ZooKeeper Session, string Path, string Owner);
    public async Task<Lease?> AcquireAsync(string url, string owner, int version, CancellationToken ct)
    {
        var zk = await ConnectAsync(ct);
        var path = LockPath(url);
        ct.ThrowIfCancellationRequested();
        try
        {
            // Once submitted, resolve the outcome or retire the session. Cancelling just the wait can orphan a lock.
            try { await zk.multiAsync([Op.check(PlanPath, version), Op.create(path, Encoding.UTF8.GetBytes(owner), Permissions, CreateMode.EPHEMERAL)]); }
            catch (KeeperException.NodeExistsException) { }
            var data = await zk.getDataAsync(path);
            return data.Stat.getEphemeralOwner() == zk.getSessionId() && Encoding.UTF8.GetString(data.Data) == owner ? new(zk, path, owner) : null;
        }
        catch (KeeperException.NoNodeException) { return null; }
        catch (KeeperException.BadVersionException) { return null; }
        catch { await RetireAsync(zk); throw; }
    }
    public async Task GuardAsync(Lease lease, int version, CancellationToken ct)
    {
        if (lease.Session.getState() != ZooKeeper.States.CONNECTED) throw new IOException("Нет связи с Keeper.");
        await lease.Session.multiAsync([Op.check(PlanPath, version), Op.check(lease.Path, -1)]).WaitAsync(ct);
    }
    public async Task ReleaseAsync(Lease lease)
    {
        // Never reuse another session to remove a lock. Expiry removes this session's nodes.
        try { await lease.Session.deleteAsync(lease.Path); }
        catch (KeeperException.NoNodeException) { }
        catch (KeeperException.SessionExpiredException) { }
        catch { await RetireAsync(lease.Session); throw; }
    }
    private async Task RetireAsync(ZooKeeper zk)
    {
        await gate.WaitAsync();
        try { if (ReferenceEquals(session, zk)) { session = null; await zk.closeAsync(); } }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() { if (session != null) await session.closeAsync(); }
}
