using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Interfaces;
using XhMonitor.Service;
using XhMonitor.Service.Core;
using XhMonitor.Service.Data;
using XhMonitor.Service.Data.Repositories;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DatabaseConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'DatabaseConnection' not found.");
}

builder.Services.AddDbContextFactory<MonitorDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddSingleton<IProcessMetricRepository, MetricRepository>();

builder.Services.AddHostedService<Worker>();

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

    return new MetricProviderRegistry(logger, pluginDirectory);
});

builder.Services.AddSingleton<ProcessScanner>();
builder.Services.AddSingleton<PerformanceMonitor>();

var host = builder.Build();
host.Run();
