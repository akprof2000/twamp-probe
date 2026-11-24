using Microsoft.Extensions.Hosting.WindowsServices;
using NLog;
using SPI.Twamp.Collector;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.BackgroundServices;
using SPI.Twamp.Server.Contracts;
using SPI.Twamp.Server.Infrastructure;
using SPI.Twamp.HighAvailability;

if (args.Length > 0 && args[0] == ConfigMerger.CommandLineSwitch)
    return ConfigMerger.RunCommandLine(args);
if (WindowsServiceHelpers.IsWindowsService()) Directory.SetCurrentDirectory(AppContext.BaseDirectory);
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd().UseWindowsService();
var config = builder.Configuration;
config["ClickHouse:SpoolPath"] = Path.GetFullPath(config["ClickHouse:SpoolPath"] ?? "spool");
if (!config.GetValue("ClickHouse:Enabled", true))
    throw new InvalidOperationException("Для запуска съёмника требуется ClickHouse:Enabled=true");
builder.Services.AddSingleton(LogManager.GetCurrentClassLogger());
builder.Services.AddHttpClient("configurator", client =>
{
    client.BaseAddress = new Uri(config["Configurator:Url"] ?? "http://localhost:9000");
    client.Timeout = TimeSpan.FromSeconds(10);
    if (config["Auth:ApiKey"] is { Length: > 0 } key) client.DefaultRequestHeaders.Add("X-Api-Key", key);
});
builder.Services.AddSingleton<IProbeClient, ProbeClient>();
builder.Services.AddSingleton<KeeperPlanStore>();
builder.Services.AddSingleton<IClickHouseWriter, ClickHouseWriter>();
builder.Services.AddSingleton(_ => new DeliveryBuffer(config["ClickHouse:SpoolPath"] ?? "spool"));
builder.Services.AddSingleton<IResultSpool, ResultSpool>();
builder.Services.AddSingleton<ClickHouseExportService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClickHouseExportService>());
builder.Services.AddSingleton<CollectorWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorWorker>());
var app = builder.Build();
// Acquire ownership before either worker recovers files left by the previous process.
_ = app.Services.GetRequiredService<DeliveryBuffer>();
if (config["Auth:ApiKey"] is { Length: > 0 } apiKey)
{
    app.Use(async (context,next) => {
        if (context.Request.Headers["X-Api-Key"] != apiKey) { context.Response.StatusCode = 401; return; }
        await next();
    });
}
app.MapGet("/identity", async (KeeperPlanStore keeper,CancellationToken ct) => new { id = config["Collector:Id"] ?? "default",
    machine = config["Collector:Machine"] is {Length:>0} machine ? machine : Environment.MachineName,
    ha = keeper.Enabled, root = keeper.Namespace, registry = keeper.Enabled ? await keeper.IdentityAsync(ct) : Guid.Empty,
    target = (config["ClickHouse:Database"] ?? "twamp")+"."+(config["ClickHouse:Table"] ?? "results") });
app.MapGet("/probe-link", async (string url,IProbeClient probe,CancellationToken ct) =>
{
    await probe.CheckInAsync(PlanBuilder.Address(url),ct);
    return Results.Ok();
});
app.MapGet("/snapshot", (Guid? instance, long? version, CollectorWorker worker) =>
    worker.Snapshot(instance,version) is { } snapshot ? Results.Ok(snapshot) : Results.NoContent());
app.MapGet("/storage", (CollectorWorker worker, ClickHouseExportService legacy) => worker.Storage(legacy.GetState()));
app.MapGet("/report", async (char? separator, char? decimalSeparator, HttpResponse response,
    DeliveryBuffer buffer, IResultSpool legacy, CancellationToken ct) =>
{
    response.ContentType = "text/csv; charset=utf-8";
    char sep = separator ?? ';', dec = decimalSeparator ?? ',';
    await using var writer = new StreamWriter(response.Body, new System.Text.UTF8Encoding(false), leaveOpen: true);
    await writer.WriteLineAsync(ExportRow.CsvHeader(sep));
    foreach (var source in new[]{ legacy.ReadPendingAsync(ct), buffer.ReadPendingAsync(ct) })
        await foreach (var row in source.WithCancellation(ct))
            await writer.WriteLineAsync(row.ToCsvLine(sep, dec).AsMemory(), ct);
});
await app.RunAsync();
return 0;
