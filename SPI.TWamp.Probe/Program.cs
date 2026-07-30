

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

string OperSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
string Version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "неизвестна";

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


    logger.Info("Запуск пробы: версия {Version}, ОС {OperSystem}", Version, OperSystem);


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
    // Хранилище результатов, реестр статусов и исполнитель зондов — синглтоны.
    _ = builder.Services.AddSingleton<IResultStore, ResultStore>();
    _ = builder.Services.AddSingleton<ITaskRunRegistry, TaskRunRegistry>();
    // Реестр активных запусков: через него удаление задачи обрывает работающий зонд.
    _ = builder.Services.AddSingleton<RunCancelRegistry>();
    _ = builder.Services.AddSingleton<IProbeRunner, ProbeRunner>();

    // Диспетчер зондов: пул воркеров ограниченного размера. Регистрируем его хостед-сервисом
    // ДО Worker, чтобы воркеры уже работали к моменту постановки задач в очередь.
    _ = builder.Services.AddSingleton<ProbeDispatcher>();
    _ = builder.Services.AddSingleton<IProbeDispatcher>(provider => provider.GetRequiredService<ProbeDispatcher>());
    _ = builder.Services.AddHostedService(provider => provider.GetRequiredService<ProbeDispatcher>());

    _ = builder.Services.AddSingleton<Worker>();
    _ = builder.Services.AddHostedService(provider => provider.GetRequiredService<Worker>());

    // Сторож связи с сервером: молчание дольше «Probe:ServerTimeoutHours» означает,
    // что пробу удалили, — задачи останавливаются, реестр и кэш результатов чистятся.
    _ = builder.Services.AddSingleton<ServerContactTracker>();
    _ = builder.Services.AddHostedService<ServerWatchdogService>();

    // Подробнее о настройке Swagger/OpenAPI: https://aka.ms/aspnetcore/swashbuckle
    _ = builder.Services.AddEndpointsApiExplorer();
    _ = builder.Services.AddSwaggerGen(c =>
    {
        // Путь к XML-комментариям для Swagger JSON и UI.
        string xmlFile = "spi.twamp.probe.xml";
        string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        c.IncludeXmlComments(xmlPath);

        // Ключ API в Swagger UI (кнопка Authorize), если аутентификация включена.
        c.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
            In = Microsoft.OpenApi.ParameterLocation.Header,
            Name = "X-Api-Key",
            Description = "Ключ API (заголовок X-Api-Key)"
        });
        c.AddSecurityRequirement(doc => new Microsoft.OpenApi.OpenApiSecurityRequirement
        {
            { new Microsoft.OpenApi.OpenApiSecuritySchemeReference("ApiKey", doc), new List<string>() }
        });
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

    // Отметка «сервер выходил на связь» — для сторожа ServerWatchdogService.
    ServerContactTracker contactTracker = app.Services.GetRequiredService<ServerContactTracker>();
    _ = app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            contactTracker.MarkContact();
        }
        await next();
    });

    // Аутентификация по общему ключу: включается, когда задан «Auth:ApiKey».
    // Проба исполняет команды по сети, поэтому в бою ключ обязателен.
    string? apiKey = builder.Configuration["Auth:ApiKey"];
    if (!string.IsNullOrWhiteSpace(apiKey))
    {
        logger.Info("Включена аутентификация API по ключу (заголовок X-Api-Key)");
        _ = app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") &&
                (!context.Request.Headers.TryGetValue("X-Api-Key", out Microsoft.Extensions.Primitives.StringValues key) || key != apiKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Неверный или отсутствующий ключ API");
                return;
            }
            await next();
        });
    }

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

    // Полный дамп конфигурации (GetDebugView) не пишем: он вываливает все переменные
    // окружения процесса, включая секреты, и раздувает журнал на старте.
    await app.RunAsync();

}
catch (Exception ex)
{
    logger?.Fatal(ex, "Аварийная остановка: необработанное исключение");
    Environment.Exit(1);
}
finally
{
    logger?.Info("Проба остановлена");
    LogManager.Shutdown();
}

