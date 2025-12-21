using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Entities;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Service.Data;
using XhMonitor.Service.Data.Repositories;

var connectionString = "Data Source=test_repository.db";

var optionsBuilder = new DbContextOptionsBuilder<MonitorDbContext>();
optionsBuilder.UseSqlite(connectionString);

await using var factory = new PooledDbContextFactory<MonitorDbContext>(optionsBuilder.Options);
await using var context = await factory.CreateDbContextAsync();
await context.Database.EnsureCreatedAsync();

var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MetricRepository>();
var repository = new MetricRepository(factory, logger);

var testMetrics = new List<ProcessMetrics>
{
    new()
    {
        Info = new ProcessInfo { ProcessId = 1234, ProcessName = "test.exe", CommandLine = "test.exe --arg" },
        Metrics = new Dictionary<string, MetricValue>
        {
            ["cpu"] = new MetricValue { Value = 50.0, Unit = "%" },
            ["memory"] = new MetricValue { Value = 100.0, Unit = "MB" }
        },
        Timestamp = DateTime.UtcNow
    }
};

Console.WriteLine("Testing Repository.SaveMetricsAsync...");
await repository.SaveMetricsAsync(testMetrics, DateTime.UtcNow, CancellationToken.None);

var count = await context.ProcessMetricRecords.CountAsync();
Console.WriteLine($"Records saved: {count}");

if (count > 0)
{
    var record = await context.ProcessMetricRecords.FirstAsync();
    Console.WriteLine($"Sample record: PID={record.ProcessId}, Name={record.ProcessName}, JSON={record.MetricsJson}");
}
