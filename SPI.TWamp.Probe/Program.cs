

// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.HttpOverrides;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using NLog;
using NLog.Extensions.Logging;
using NLog.Web;
using spi.twamp.Probe.Environment;
using SPI.Twamp.Probe.Abstractions;
using SPI.Twamp.Probe.Runners;
using SPI.Twamp.Probe.Server;
using System.Reflection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string fileNamesStr = "appsettings.json";

int mesln = 70;
string OperSystem = $"Operation system {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
OperSystem = OperSystem.PadLeft(OperSystem.Length + ((mesln - OperSystem.Length) / 2)).PadRight(mesln);
string Version = $"Version {Assembly.GetEntryAssembly()?.GetName().Version}";
Version = Version.PadLeft(Version.Length + ((mesln - Version.Length) / 2)).PadRight(mesln);

Logger? logger = null;

try
{

    _ = builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
    _ = builder.Configuration.AddJsonFile(fileNamesStr, optional: true, reloadOnChange: true);


    _ = builder.Configuration.AddEnvironmentVariables();
    _ = builder.Logging.ClearProviders();
    _ = builder.Host.UseSystemd().UseNLog();

    _ = builder.Services.AddMemoryCache(prop =>
    {
        prop.SizeLimit = builder.Configuration["Cashing:PointOfSize"].ConvertTo(256);
        prop.CompactionPercentage = builder.Configuration["Cashing:CompactionPercentage"].ConvertTo(50) / 100.0;
    });

    logger = LogManager.Setup().LoadConfigurationFromSection(builder.Configuration).GetCurrentClassLogger();


    string sd = $"Program start at {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
    sd = sd.PadLeft(sd.Length + ((mesln - sd.Length) / 2)).PadRight(mesln);

    logger.Info($"{sd}");
    logger.Info($"{Version}");
    logger.Info($"{OperSystem}");


    // Выполнение зондов и выдача результатов теперь полностью асинхронны и не
    // удерживают потоки пула, поэтому искусственный верхний лимит потоков убран —
    // он лишь провоцировал голодание пула при большом числе задач.
    // Немного поднимаем минимум потоков, чтобы сгладить пики нагрузки на старте.
    int minThreads = Environment.ProcessorCount * builder.Configuration["MinThreadsCountPerProcessor"].ConvertTo(2);
    _ = ThreadPool.SetMinThreads(minThreads, minThreads);


    _ = builder.Services.AddProblemDetails();
    _ = builder.Services.AddControllers().AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.Converters.Add(new StringEnumConverter());
        options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        options.SerializerSettings.ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy()
        };
    });
    _ = builder.Services.AddCors(options =>
    {
        options.AddPolicy("CorsPolicy",
            builder => builder.AllowAnyOrigin()
                                .AllowAnyMethod()
                                .AllowAnyHeader());
    });
    _ = builder.Services.AddSingleton(logger);
    // Хранилище результатов и исполнитель зондов — синглтоны, общие для всех запросов.
    _ = builder.Services.AddSingleton<IResultStore, ResultStore>();
    _ = builder.Services.AddSingleton<IProbeRunner, ProbeRunner>();

    // Диспетчер зондов: пул воркеров ограниченного размера. Регистрируем его хостед-сервисом
    // ДО Worker, чтобы воркеры уже работали к моменту постановки задач в очередь.
    _ = builder.Services.AddSingleton<ProbeDispatcher>();
    _ = builder.Services.AddSingleton<IProbeDispatcher>(provider => provider.GetRequiredService<ProbeDispatcher>());
    _ = builder.Services.AddHostedService(provider => provider.GetRequiredService<ProbeDispatcher>());

    _ = builder.Services.AddSingleton<Worker>();
    _ = builder.Services.AddHostedService(provider => provider.GetRequiredService<Worker>());

    // Подробнее о настройке Swagger/OpenAPI: https://aka.ms/aspnetcore/swashbuckle
    _ = builder.Services.AddEndpointsApiExplorer();
    _ = builder.Services.AddSwaggerGen(c =>
    {
        // Путь к XML-комментариям для Swagger JSON и UI.
        string xmlFile = "spi.twamp.probe.xml";
        string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        c.IncludeXmlComments(xmlPath);
    });
    _ = builder.Services.AddSwaggerGenNewtonsoftSupport();

    _ = builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
    });


    string pth = builder.Configuration["StaticPathApp"] ?? "wwwroot";
    _ = Directory.CreateDirectory(pth);

    builder.Services.AddSpaStaticFiles(configuration =>
    {
        configuration.RootPath = pth;
    });

    _ = builder.Services.AddRouting(options => options.LowercaseUrls = true);

    WebApplication app = builder.Build();

    app.UseRouting()
        .UseCors("CorsPolicy")
        .UseDefaultFiles()
        .UseStaticFiles()
        .UseExceptionHandler()
        .UseStatusCodePages()
        .UseSwagger()
        .UseSwaggerUI()
        .UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        })
        .UseResponseCompression()
        .UseDeveloperExceptionPage()
        .UseSpaStaticFiles();

    app.UseSpa(spa =>
    {
        spa.Options.SourcePath = $"{builder.Configuration["UrlPathApp"] ?? "/"}";
    });

    _ = app.MapControllers();

    ConfigSettingLayoutRenderer.DefaultConfiguration = builder.Configuration;
    LogManager.ReconfigExistingLoggers();

    logger.Debug("Start is configure as : {StrConfiguration}", builder.Configuration.GetDebugView());
    await app.RunAsync();

}
catch (Exception ex)
{
    logger?.Fatal(ex, "Stopped program because of exception");
    Environment.Exit(1);
}
finally
{
    string se = $"Program end at {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
    se = se.PadLeft(se.Length + ((mesln - se.Length) / 2)).PadRight(mesln);
    logger?.Info($"{se}");
    logger?.Info($"{Version}");
    logger?.Info($"{OperSystem}");
    LogManager.Shutdown();
}

