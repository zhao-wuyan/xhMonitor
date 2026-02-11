using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Settings.Configuration;
using XhMonitor.Core.Common;
using XhMonitor.Core.Configuration;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;
using XhMonitor.Core.Services;
using XhMonitor.Service;
using XhMonitor.Service.Core;
using XhMonitor.Service.Data;
using XhMonitor.Service.Data.Repositories;
using XhMonitor.Service.Configuration;
using XhMonitor.Service.Workers;
using XhMonitor.Service.Hubs;

// 单实例检查
const string MutexName = "XhMonitor_Service_SingleInstance";
using var mutex = new Mutex(true, MutexName, out bool createdNew);
if (!createdNew)
{
    Console.WriteLine("XhMonitor Service 已在运行中。");
    return;
}

// 设置控制台编码为 UTF-8，确保中文日志正常显示（仅在有控制台时）
try
{
    if (Environment.UserInteractive)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
    }
}
catch
{
    // WinExe 模式下可能没有控制台，忽略错误
}

// 获取应用程序目录（兼容单文件发布模式）
// 单文件发布时 AppContext.BaseDirectory 指向临时解压目录，需要使用 exe 实际路径
var appDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName)
    ?? AppContext.BaseDirectory;

// dotnet run / dotnet-ef 等场景下，MainModule 可能是 dotnet.exe，导致 appDirectory 指向 dotnet 安装目录。
// 这里在找不到配置文件时回退到项目目录（或工作目录），保证开发/迁移工具可用。
if (!File.Exists(Path.Combine(appDirectory, "appsettings.json")))
{
    var currentDirectory = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(currentDirectory, "appsettings.json")))
    {
        appDirectory = currentDirectory;
    }
    else if (File.Exists(Path.Combine(currentDirectory, "XhMonitor.Service", "appsettings.json")))
    {
        appDirectory = Path.Combine(currentDirectory, "XhMonitor.Service");
    }
}

// 创建临时配置以读取日志设置（使用应用程序目录而非工作目录）
var tempConfig = new ConfigurationBuilder()
    .SetBasePath(appDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true, reloadOnChange: true)
    .Build();

// 确定日志目录（使用应用所在目录）
var logDirectory = Path.Combine(appDirectory, "logs");
Directory.CreateDirectory(logDirectory);

// 配置 Serilog 日志 - 从配置文件读取
var serilogOptions = new ConfigurationReaderOptions(
    Assembly.Load("Serilog.Sinks.Console"),
    Assembly.Load("Serilog.Sinks.Debug"),
    Assembly.Load("Serilog.Sinks.File"));

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(tempConfig, serilogOptions)
    .CreateLogger();

Result<string, string> ValidateConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("DatabaseConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Result<string, string>.Failure("Connection string 'DatabaseConnection' not found.");
    }

    return Result<string, string>.Success(connectionString);
}

try
{
    Log.Information("正在启动 XhMonitor 服务...");
    Log.Information("应用程序目录: {AppDirectory}", appDirectory);

    // 创建 WebApplication 时设置 ContentRootPath 为应用程序目录
    // 确保相对路径（数据库、日志等）解析到正确位置
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = appDirectory
    });

    // 使用 Serilog 作为日志提供者
    builder.Host.UseSerilog();

    builder.Services.Configure<MonitorSettings>(builder.Configuration.GetSection("Monitor"));
    builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection("Database"));
    builder.Services.Configure<AggregationSettings>(builder.Configuration.GetSection("Aggregation"));
    builder.Services.AddOptions<MonitorSettings>().ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddOptions<DatabaseSettings>().ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddOptions<AggregationSettings>().ValidateDataAnnotations().ValidateOnStart();

    var connectionResult = ValidateConnectionString(builder.Configuration);
    if (connectionResult.IsFailure)
    {
        Log.Fatal("配置错误: {Error}", connectionResult.Error);
        Environment.Exit(1);
        return;
    }

    var connectionString = connectionResult.Value;

builder.Services.AddDbContextFactory<MonitorDbContext>(options =>
{
    var sqliteOptions = options.UseSqlite(connectionString);
    // Note: SQLite doesn't have built-in retry on failure like SQL Server
    // But we can still configure command timeout
});

builder.Services.AddSingleton<IProcessMetricRepository, MetricRepository>();

builder.Services.AddSingleton<IProcessNameResolver, ProcessNameResolver>();

// 注册 LibreHardwareManager 为单例（在 MetricProviderRegistry 之前）
// Host 会在应用关闭时自动调用 IAsyncDisposable.DisposeAsync。
builder.Services.AddSingleton<ILibreHardwareManager>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<LibreHardwareManager>>();
    var monitorOptions = sp.GetRequiredService<IOptions<MonitorSettings>>();
    // 保留防御性保护，避免外部依赖收到无效间隔导致异常。
    var systemIntervalSeconds = Math.Max(1, monitorOptions.Value.SystemUsageIntervalSeconds);
    return new LibreHardwareManager(logger, TimeSpan.FromSeconds(systemIntervalSeconds));
});

builder.Services.AddSingleton<BuiltInMetricProviderFactory>();
builder.Services.AddSingleton<IMetricProviderFactory>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var preferLibreHardwareMonitor = config.GetValue<bool>("MetricProviders:PreferLibreHardwareMonitor", true);

    if (!preferLibreHardwareMonitor)
    {
        var logger = sp.GetRequiredService<ILogger<MetricProviderRegistry>>();
        logger.LogInformation("配置禁用 LibreHardwareMonitor，使用传统 PerformanceCounter 提供者");
        return sp.GetRequiredService<BuiltInMetricProviderFactory>();
    }

    var hardwareManager = sp.GetRequiredService<ILibreHardwareManager>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var factoryLogger = sp.GetRequiredService<ILogger<LibreHardwareMonitorProviderFactory>>();
    return new LibreHardwareMonitorProviderFactory(
        hardwareManager,
        loggerFactory,
        factoryLogger,
        sp.GetRequiredService<BuiltInMetricProviderFactory>());
});

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<AggregationWorker>();
builder.Services.AddHostedService<DatabaseCleanupWorker>();

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MetricProviderRegistry>>();
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    var pluginDirectory = config["MetricProviders:PluginDirectory"];
    if (string.IsNullOrWhiteSpace(pluginDirectory))
    {
        pluginDirectory = Path.Combine(env.ContentRootPath, "plugins");
    }
    var providerFactory = sp.GetRequiredService<IMetricProviderFactory>();

    return new MetricProviderRegistry(logger, pluginDirectory, providerFactory);
});

builder.Services.AddSingleton<IGpuVendorDetector>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<WmiGpuVendorDetector>>();
    return new WmiGpuVendorDetector(logger);
});

builder.Services.AddSingleton<IRyzenAdjCli>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();
    var logger = sp.GetRequiredService<ILogger<RyzenAdjCli>>();
    return new RyzenAdjCli(config["Power:RyzenAdjPath"], env.ContentRootPath, logger);
});

// 设备验证服务
builder.Services.Configure<DeviceVerificationOptions>(
    builder.Configuration.GetSection("Power:DeviceVerification"));
builder.Services.AddHttpClient<IDeviceVerifier, DeviceVerifier>();
builder.Services.AddSingleton<IDeviceVerifier>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient(nameof(DeviceVerifier));
    var options = sp.GetRequiredService<IOptions<DeviceVerificationOptions>>();
    var logger = sp.GetRequiredService<ILogger<DeviceVerifier>>();
    return new DeviceVerifier(httpClient, options, logger);
});

builder.Services.AddSingleton<IPowerProvider>(sp =>
{
    var vendor = sp.GetRequiredService<IGpuVendorDetector>().DetectVendor();
    if (vendor != GpuVendor.Amd)
    {
        return new NullPowerProvider();
    }

    var cli = sp.GetRequiredService<IRyzenAdjCli>();
    if (!cli.IsAvailable)
    {
        return new NullPowerProvider();
    }

    var config = sp.GetRequiredService<IConfiguration>();
    var pollSeconds = config.GetValue<int>("Power:PollingIntervalSeconds", 3);
    var pollingInterval = pollSeconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(pollSeconds);

    // 获取设备验证服务，使用验证后的设备方案
    var deviceVerifier = sp.GetRequiredService<IDeviceVerifier>();
    var deviceName = deviceVerifier.GetVerifiedDeviceName();
    var schemes = deviceName != null ? deviceVerifier.GetSchemesForDevice(deviceName) : null;

    var logger = sp.GetRequiredService<ILogger<RyzenAdjPowerProvider>>();
    return new RyzenAdjPowerProvider(cli, pollingInterval, schemes, logger);
});

builder.Services.AddSingleton<ISystemMetricProvider, SystemMetricProvider>(sp =>
{
    var registry = sp.GetRequiredService<MetricProviderRegistry>();
    var logger = sp.GetRequiredService<ILogger<SystemMetricProvider>>();
    var hardwareManager = sp.GetRequiredService<ILibreHardwareManager>();
    var powerProvider = sp.GetRequiredService<IPowerProvider>();
    return new SystemMetricProvider(registry.GetAllProviders(), logger, hardwareManager, powerProvider);
});

builder.Services.AddSingleton<ProcessScanner>();
builder.Services.AddSingleton<PerformanceMonitor>();
builder.Services.AddSingleton<IProcessMetadataStore, ProcessMetadataStore>();

builder.Services.AddControllers();
var signalRSection = builder.Configuration.GetSection("SignalR");
var signalRMaximumReceiveMessageSize = signalRSection.GetValue<long?>("MaximumReceiveMessageSize");
var signalRApplicationMaxBufferSize = signalRSection.GetValue<long?>("ApplicationMaxBufferSize");
var signalRTransportMaxBufferSize = signalRSection.GetValue<long?>("TransportMaxBufferSize");

builder.Services.AddSignalR(options =>
{
    if (signalRMaximumReceiveMessageSize.HasValue && signalRMaximumReceiveMessageSize.Value > 0)
    {
        options.MaximumReceiveMessageSize = signalRMaximumReceiveMessageSize.Value;
    }
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.WithOrigins("http://localhost:3000", "http://localhost:5173", "http://localhost:35180", "app://.")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var host = context.Configuration["Server:Host"] ?? "localhost";
    var port = context.Configuration.GetValue<int>("Server:Port", 35179);
    options.ListenLocalhost(port);
});

var serverHost = builder.Configuration["Server:Host"] ?? "localhost";
var serverPort = builder.Configuration.GetValue<int>("Server:Port", 35179);
var hubPath = builder.Configuration["Server:HubPath"] ?? "/hubs/metrics";

var app = builder.Build();

// 初始化设备验证服务（在服务解析前完成 HTTP 验证）
using (var scope = app.Services.CreateScope())
{
    var deviceVerifier = scope.ServiceProvider.GetRequiredService<IDeviceVerifier>();
    try
    {
        Log.Information("正在初始化设备验证服务...");
        await deviceVerifier.GetDeviceInfoAsync().ConfigureAwait(false);
        var deviceName = deviceVerifier.GetVerifiedDeviceName();
        if (deviceName != null)
        {
            Log.Information("设备验证成功: {DeviceName}", deviceName);
        }
        else
        {
            Log.Warning("设备验证未通过: {Reason}", deviceVerifier.GetDisabledReason());
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "设备验证服务初始化失败，功耗切换功能将被禁用");
    }
}

// 自动应用数据库迁移
 using (var scope = app.Services.CreateScope())
 {
     var dbContext = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
     try
     {
         Log.Information("正在检查并应用数据库迁移...");
         dbContext.Database.Migrate();
         Log.Information("数据库迁移完成");
     }
     catch (Exception ex)
     {
         Log.Warning(ex, "数据库迁移失败，将尝试继续运行");
     }
 }

 // 启动配置校验（appsettings.json + 数据库）
 ConfigurationValidator.ValidateConfiguration(app.Services);
 
 if (app.Environment.IsDevelopment())
 {
     app.UseDeveloperExceptionPage();
 }

app.UseCors("AllowAll");
app.UseRouting();

app.MapControllers();
app.MapHub<MetricsHub>(hubPath, options =>
{
    if (signalRApplicationMaxBufferSize.HasValue && signalRApplicationMaxBufferSize.Value > 0)
    {
        options.ApplicationMaxBufferSize = signalRApplicationMaxBufferSize.Value;
    }

    if (signalRTransportMaxBufferSize.HasValue && signalRTransportMaxBufferSize.Value > 0)
    {
        options.TransportMaxBufferSize = signalRTransportMaxBufferSize.Value;
    }
});

Log.Information(
    "SignalR Hub: http://{Host}:{Port}{HubPath}",
    serverHost,
    serverPort,
    hubPath);

    Log.Information("XhMonitor 服务配置完成，开始运行");
    app.Run();
    Log.Information("XhMonitor 服务正常停止");
}
catch (Exception ex)
{
    Log.Fatal(ex, "XhMonitor 服务启动失败");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
