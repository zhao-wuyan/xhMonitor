using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Entities;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Service.Data.Repositories;

public sealed class MetricRepository : IProcessMetricRepository
{
    private readonly IDbContextFactory<MonitorDbContext> _contextFactory;
    private readonly ILogger<MetricRepository> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public MetricRepository(
        IDbContextFactory<MonitorDbContext> contextFactory,
        ILogger<MetricRepository> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task SaveMetricsAsync(
        IReadOnlyCollection<ProcessMetrics> metrics,
        DateTime cycleTimestamp,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SaveMetricsAsync called with {Count} metrics", metrics.Count);

        if (metrics.Count == 0)
        {
            _logger.LogDebug("No metrics to save, skipping persistence");
            return;
        }

        try
        {
            _logger.LogDebug("Creating DbContext...");
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            _logger.LogDebug("DbContext created successfully");

            _logger.LogDebug("Mapping {Count} metrics to entities...", metrics.Count);
            var records = metrics.Select(m => MapToEntity(m, cycleTimestamp)).ToList();
            _logger.LogDebug("Mapped {Count} records", records.Count);

            _logger.LogDebug("Adding records to context...");
            await context.ProcessMetricRecords.AddRangeAsync(records, cancellationToken);
            _logger.LogDebug("Calling SaveChangesAsync...");
            var savedCount = await context.SaveChangesAsync(cancellationToken);

            // 清理 ChangeTracker 避免内存累积
            context.ChangeTracker.Clear();
            _logger.LogDebug("ChangeTracker cleared after SaveChangesAsync");

            _logger.LogInformation("Saved {Count} metric records to database", savedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save metrics to database. {Count} records lost", metrics.Count);
        }
    }

    private static ProcessMetricRecord MapToEntity(ProcessMetrics source, DateTime cycleTimestamp)
    {
        var metricsJson = JsonSerializer.Serialize(source.Metrics, JsonOptions);

        return new ProcessMetricRecord
        {
            ProcessId = source.Info.ProcessId,
            ProcessName = source.Info.ProcessName,
            CommandLine = source.Info.CommandLine,
            DisplayName = source.Info.DisplayName,
            Timestamp = DateTime.SpecifyKind(cycleTimestamp, DateTimeKind.Utc),
            MetricsJson = metricsJson
        };
    }
}
