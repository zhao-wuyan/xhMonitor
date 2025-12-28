using System.Text;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;
using XhMonitor.Service;
using XhMonitor.Service.Core;
using XhMonitor.Service.Data;
using XhMonitor.Service.Data.Repositories;
using XhMonitor.Service.Workers;
using XhMonitor.Service.Hubs;

// 设置控制台编码为 UTF-8，确保中文日志正常显示
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// 配置 Serilog 日志
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}")
    .WriteTo.Debug(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/xhmonitor-.log",
        rollingInterval: RollingInterval.Day,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}",
        encoding: Encoding.UTF8,
        retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    Log.Information("正在启动 XhMonitor 服务...");

    var builder = WebApplication.CreateBuilder(args);

    // 使用 Serilog 作为日志提供者
    builder.Host.UseSerilog();

var connectionString = builder.Configuration.GetConnectionString("DatabaseConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'DatabaseConnection' not found.");
}

builder.Services.AddDbContextFactory<MonitorDbContext>(options =>
{
    var sqliteOptions = options.UseSqlite(connectionString);
    // Note: SQLite doesn't have built-in retry on failure like SQL Server
    // But we can still configure command timeout
});

builder.Services.AddSingleton<IProcessMetricRepository, MetricRepository>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<AggregationWorker>();

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MetricProviderRegistry>>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var config = sp.GetRequiredService<IConfiguration>();
    var env = sp.GetRequiredService<IHostEnvironment>();

    var pluginDirectory = config["MetricProviders:PluginDirectory"];
    if (string.IsNullOrWhiteSpace(pluginDirectory))
    {
        pluginDirectory = Path.Combine(env.ContentRootPath, "plugins");
    }

    return new MetricProviderRegistry(logger, loggerFactory, pluginDirectory);
});

builder.Services.AddSingleton<SystemMetricProvider>(sp =>
{
    var registry = sp.GetRequiredService<MetricProviderRegistry>();
    return new SystemMetricProvider(
        registry.GetProvider("cpu"),
        registry.GetProvider("gpu"),
        registry.GetProvider("memory"),
        registry.GetProvider("vram")
    );
});

builder.Services.AddSingleton<ProcessScanner>();
builder.Services.AddSingleton<PerformanceMonitor>();

builder.Services.AddControllers();
builder.Services.AddSignalR();

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

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseCors("AllowAll");
app.UseRouting();

app.MapControllers();
var hubPath = builder.Configuration["Server:HubPath"] ?? "/hubs/metrics";
app.MapHub<MetricsHub>(hubPath);

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
