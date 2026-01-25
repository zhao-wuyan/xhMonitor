using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XhMonitor.Service.Data;
using XhMonitor.Service.Configuration;

namespace XhMonitor.Service.Workers;

public sealed class DatabaseCleanupWorker : BackgroundService
{
    private readonly IDbContextFactory<MonitorDbContext> _contextFactory;
    private readonly ILogger<DatabaseCleanupWorker> _logger;
    private readonly IOptions<DatabaseSettings> _databaseOptions;

    public DatabaseCleanupWorker(
        IDbContextFactory<MonitorDbContext> contextFactory,
        ILogger<DatabaseCleanupWorker> logger,
        IOptions<DatabaseSettings> databaseOptions)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _databaseOptions = databaseOptions;
        ArgumentNullException.ThrowIfNull(databaseOptions);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = _databaseOptions.Value.CleanupIntervalHours;
        var retentionDays = _databaseOptions.Value.RetentionDays;

        _logger.LogInformation("数据库清理服务已启动，清理间隔 {IntervalHours} 小时，保留 {RetentionDays} 天数据",
            intervalHours, retentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken);
                await CleanupOldDataAsync(retentionDays, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("数据库清理服务正在停止");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库清理过程中发生错误");
            }
        }
    }

    private async Task CleanupOldDataAsync(int retentionDays, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

        var deletedProcessMetrics = await context.ProcessMetricRecords
            .Where(r => r.Timestamp < cutoffDate)
            .ExecuteDeleteAsync(cancellationToken);

        var deletedAggregatedMetrics = await context.AggregatedMetricRecords
            .Where(r => r.Timestamp < cutoffDate)
            .ExecuteDeleteAsync(cancellationToken);

        if (deletedProcessMetrics > 0 || deletedAggregatedMetrics > 0)
        {
            _logger.LogInformation("已清理 {ProcessCount} 条进程指标记录和 {AggregatedCount} 条聚合指标记录（{RetentionDays} 天前）",
                deletedProcessMetrics, deletedAggregatedMetrics, retentionDays);

            await context.Database.ExecuteSqlRawAsync("VACUUM;", cancellationToken);
            _logger.LogInformation("已执行 VACUUM 优化数据库");
        }
        else
        {
            _logger.LogDebug("无需清理数据");
        }
    }
}
