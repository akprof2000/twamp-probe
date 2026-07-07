

// Ignore Spelling: SPI Twamp

using Microsoft.AspNetCore.HttpOverrides;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using NLog;
using NLog.Extensions.Logging;
using NLog.Web;
using spi.twamp.server.Environment;
using SPI.Twamp.Server.Abstractions;
using SPI.Twamp.Server.Application;
using SPI.Twamp.Server.BackgroundServices;
using SPI.Twamp.Server.Infrastructure;
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


    // Опрос проб и работа с БД полностью асинхронны и не удерживают потоки пула,
    // поэтому искусственный верхний лимит потоков убран — он лишь мешал бы под нагрузкой.
    // Немного поднимаем минимум, чтобы сгладить пики на старте.
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
    builder.Services.AddMemoryCache(options =>
    {
        options.SizeLimit = 9086; // общий лимит кэша
    });
    // --- Инфраструктура: БД, репозитории, HTTP-клиент пробы ---
    _ = builder.Services.AddSingleton<LiteDbContext>();
    _ = builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
    _ = builder.Services.AddSingleton<IClientRepository, ClientRepository>();
    _ = builder.Services.AddSingleton<IActionRepository, ActionRepository>();
    _ = builder.Services.AddSingleton<IProbeClient, ProbeClient>();

    // --- Прикладные сервисы ---
    _ = builder.Services.AddSingleton<ITaskService, TaskService>();
    _ = builder.Services.AddSingleton<IClientService, ClientService>();
    _ = builder.Services.AddSingleton<IReportService, ReportService>();

    // --- Фоновый опрос проб: один синглтон, доступный и как IProbePoller, и как хостед-сервис ---
    _ = builder.Services.AddSingleton<ProbePollingService>();
    _ = builder.Services.AddSingleton<IProbePoller>(provider => provider.GetRequiredService<ProbePollingService>());
    _ = builder.Services.AddHostedService(provider => provider.GetRequiredService<ProbePollingService>());

    // Подробнее о настройке Swagger/OpenAPI: https://aka.ms/aspnetcore/swashbuckle
    _ = builder.Services.AddEndpointsApiExplorer();
    _ = builder.Services.AddSwaggerGen(c =>
    {
        // Путь к XML-комментариям для Swagger JSON и UI.
        string xmlFile = "spi.twamp.server.xml";
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
    // Только один из них, в зависимости от среды
    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
    }
    else
    {
        app.UseExceptionHandler();
    }

    app.UseStatusCodePages();

    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseRouting();
    app.UseCors("CorsPolicy");

    app.UseResponseCompression();

    app.UseStaticFiles();
    app.UseSpaStaticFiles();

    app.MapControllers(); // <-- API теперь работает стабильно

    app.UseSpa(spa =>
    {
        spa.Options.SourcePath = builder.Configuration["UrlPathApp"] ?? "/";
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

