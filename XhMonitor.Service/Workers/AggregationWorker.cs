using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Entities;
using XhMonitor.Core.Enums;
using XhMonitor.Service.Data;
using System.Text.Json;

namespace XhMonitor.Service.Workers;

public sealed class AggregationWorker : BackgroundService
{
    private readonly IDbContextFactory<MonitorDbContext> _contextFactory;
    private readonly ILogger<AggregationWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AggregationWorker(
        IDbContextFactory<MonitorDbContext> contextFactory,
        ILogger<AggregationWorker> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AggregationWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAggregationCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aggregation cycle failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("AggregationWorker stopped");
    }

    private async Task RunAggregationCycleAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting aggregation cycle");

        await AggregateRawToMinuteAsync(cancellationToken);
        await AggregateMinuteToHourAsync(cancellationToken);
        await AggregateHourToDayAsync(cancellationToken);

        _logger.LogDebug("Aggregation cycle completed");
    }

    private async Task AggregateRawToMinuteAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var lastWatermark = await context.AggregatedMetricRecords
            .AsNoTracking()
            .Where(r => r.AggregationLevel == AggregationLevel.Minute)
            .OrderByDescending(r => r.Timestamp)
            .Select(r => r.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        var windowEnd = DateTime.UtcNow.TruncateToMinute();

        if (lastWatermark >= windowEnd)
        {
            _logger.LogDebug("No new minute windows to aggregate (watermark: {Watermark}, end: {End})",
                lastWatermark, windowEnd);
            return;
        }

        var windowStart = lastWatermark == default ?
            await context.ProcessMetricRecords
                .AsNoTracking()
                .OrderBy(r => r.Timestamp)
                .Select(r => r.Timestamp)
                .FirstOrDefaultAsync(cancellationToken) :
            lastWatermark;

        if (windowStart == default)
        {
            _logger.LogDebug("No raw data available for aggregation");
            return;
        }

        _logger.LogInformation("Aggregating Raw → Minute: {Start} to {End}", windowStart, windowEnd);

        var rawRecords = await context.ProcessMetricRecords
            .AsNoTracking()
            .Where(r => r.Timestamp > windowStart && r.Timestamp < windowEnd)
            .OrderBy(r => r.Timestamp)
            .ToListAsync(cancellationToken);

        if (rawRecords.Count == 0)
        {
            _logger.LogDebug("No raw records in window");
            return;
        }

        var aggregatedRecords = AggregateToMinute(rawRecords);

        await context.AggregatedMetricRecords.AddRangeAsync(aggregatedRecords, cancellationToken);
        var savedCount = await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Saved {Count} minute aggregations", savedCount);
    }

    private List<AggregatedMetricRecord> AggregateToMinute(List<ProcessMetricRecord> rawRecords)
    {
        var grouped = rawRecords
            .GroupBy(r => new
            {
                r.ProcessId,
                r.ProcessName,
                MinuteTimestamp = r.Timestamp.TruncateToMinute()
            });

        var results = new List<AggregatedMetricRecord>();

        foreach (var group in grouped)
        {
            var metrics = new Dictionary<string, MetricAggregation>();

            foreach (var record in group)
            {
                var recordMetrics = JsonSerializer.Deserialize<Dictionary<string, MetricValue>>(
                    record.MetricsJson, JsonOptions);

                if (recordMetrics == null) continue;

                foreach (var (metricId, metricValue) in recordMetrics)
                {
                    if (!metrics.ContainsKey(metricId))
                    {
                        metrics[metricId] = new MetricAggregation
                        {
                            Unit = metricValue.Unit ?? string.Empty
                        };
                    }

                    var agg = metrics[metricId];
                    var value = metricValue.Value;

                    agg.Min = Math.Min(agg.Min, value);
                    agg.Max = Math.Max(agg.Max, value);
                    agg.Sum += value;
                    agg.Count++;
                }
            }

            foreach (var agg in metrics.Values)
            {
                agg.Avg = agg.Count > 0 ? agg.Sum / agg.Count : 0;
            }

            var metricsJson = JsonSerializer.Serialize(metrics, JsonOptions);

            results.Add(new AggregatedMetricRecord
            {
                ProcessId = group.Key.ProcessId,
                ProcessName = group.Key.ProcessName,
                AggregationLevel = AggregationLevel.Minute,
                Timestamp = DateTime.SpecifyKind(group.Key.MinuteTimestamp, DateTimeKind.Utc),
                MetricsJson = metricsJson
            });
        }

        return results;
    }

    private async Task AggregateMinuteToHourAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var lastWatermark = await context.AggregatedMetricRecords
            .AsNoTracking()
            .Where(r => r.AggregationLevel == AggregationLevel.Hour)
            .OrderByDescending(r => r.Timestamp)
            .Select(r => r.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        var windowEnd = DateTime.UtcNow.TruncateToHour();

        if (lastWatermark >= windowEnd)
        {
            _logger.LogDebug("No new hour windows to aggregate");
            return;
        }

        var windowStart = lastWatermark == default ?
            await context.AggregatedMetricRecords
                .AsNoTracking()
                .Where(r => r.AggregationLevel == AggregationLevel.Minute)
                .OrderBy(r => r.Timestamp)
                .Select(r => r.Timestamp)
                .FirstOrDefaultAsync(cancellationToken) :
            lastWatermark;

        if (windowStart == default)
        {
            _logger.LogDebug("No minute data available for hour aggregation");
            return;
        }

        _logger.LogInformation("Aggregating Minute → Hour: {Start} to {End}", windowStart, windowEnd);

        var minuteRecords = await context.AggregatedMetricRecords
            .AsNoTracking()
            .Where(r => r.AggregationLevel == AggregationLevel.Minute &&
                       r.Timestamp > windowStart && r.Timestamp < windowEnd)
            .OrderBy(r => r.Timestamp)
            .ToListAsync(cancellationToken);

        if (minuteRecords.Count == 0)
        {
            _logger.LogDebug("No minute records in window");
            return;
        }

        var aggregatedRecords = AggregateToHigherLevel(minuteRecords, AggregationLevel.Hour,
            r => r.TruncateToHour());

        await context.AggregatedMetricRecords.AddRangeAsync(aggregatedRecords, cancellationToken);
        var savedCount = await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Saved {Count} hour aggregations", savedCount);
    }

    private async Task AggregateHourToDayAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var lastWatermark = await context.AggregatedMetricRecords
            .AsNoTracking()
            .Where(r => r.AggregationLevel == AggregationLevel.Day)
            .OrderByDescending(r => r.Timestamp)
            .Select(r => r.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        var windowEnd = DateTime.UtcNow.TruncateToDay();

        if (lastWatermark >= windowEnd)
        {
            _logger.LogDebug("No new day windows to aggregate");
            return;
        }

        var windowStart = lastWatermark == default ?
            await context.AggregatedMetricRecords
                .AsNoTracking()
                .Where(r => r.AggregationLevel == AggregationLevel.Hour)
                .OrderBy(r => r.Timestamp)
                .Select(r => r.Timestamp)
                .FirstOrDefaultAsync(cancellationToken) :
            lastWatermark;

        if (windowStart == default)
        {
            _logger.LogDebug("No hour data available for day aggregation");
            return;
        }

        _logger.LogInformation("Aggregating Hour → Day: {Start} to {End}", windowStart, windowEnd);

        var hourRecords = await context.AggregatedMetricRecords
            .AsNoTracking()
            .Where(r => r.AggregationLevel == AggregationLevel.Hour &&
                       r.Timestamp > windowStart && r.Timestamp < windowEnd)
            .OrderBy(r => r.Timestamp)
            .ToListAsync(cancellationToken);

        if (hourRecords.Count == 0)
        {
            _logger.LogDebug("No hour records in window");
            return;
        }

        var aggregatedRecords = AggregateToHigherLevel(hourRecords, AggregationLevel.Day,
            r => r.TruncateToDay());

        await context.AggregatedMetricRecords.AddRangeAsync(aggregatedRecords, cancellationToken);
        var savedCount = await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Saved {Count} day aggregations", savedCount);
    }

    private List<AggregatedMetricRecord> AggregateToHigherLevel(
        List<AggregatedMetricRecord> sourceRecords,
        AggregationLevel targetLevel,
        Func<DateTime, DateTime> truncateFunc)
    {
        var grouped = sourceRecords
            .GroupBy(r => new
            {
                r.ProcessId,
                r.ProcessName,
                TruncatedTimestamp = truncateFunc(r.Timestamp)
            });

        var results = new List<AggregatedMetricRecord>();

        foreach (var group in grouped)
        {
            var metrics = new Dictionary<string, MetricAggregation>();

            foreach (var record in group)
            {
                var recordMetrics = JsonSerializer.Deserialize<Dictionary<string, MetricAggregation>>(
                    record.MetricsJson, JsonOptions);

                if (recordMetrics == null) continue;

                foreach (var (metricId, metricAgg) in recordMetrics)
                {
                    if (!metrics.ContainsKey(metricId))
                    {
                        metrics[metricId] = new MetricAggregation
                        {
                            Min = double.MaxValue,
                            Max = double.MinValue,
                            Unit = metricAgg.Unit
                        };
                    }

                    var agg = metrics[metricId];
                    agg.Min = Math.Min(agg.Min, metricAgg.Min);
                    agg.Max = Math.Max(agg.Max, metricAgg.Max);
                    agg.Sum += metricAgg.Sum;
                    agg.Count += metricAgg.Count;
                }
            }

            foreach (var agg in metrics.Values)
            {
                agg.Avg = agg.Count > 0 ? agg.Sum / agg.Count : 0;
            }

            var metricsJson = JsonSerializer.Serialize(metrics, JsonOptions);

            results.Add(new AggregatedMetricRecord
            {
                ProcessId = group.Key.ProcessId,
                ProcessName = group.Key.ProcessName,
                AggregationLevel = targetLevel,
                Timestamp = DateTime.SpecifyKind(group.Key.TruncatedTimestamp, DateTimeKind.Utc),
                MetricsJson = metricsJson
            });
        }

        return results;
    }

    private class MetricValue
    {
        public double Value { get; set; }
        public string? Unit { get; set; }
    }

    private class MetricAggregation
    {
        public double Min { get; set; } = double.MaxValue;
        public double Max { get; set; } = double.MinValue;
        public double Avg { get; set; }
        public double Sum { get; set; }
        public int Count { get; set; }
        public string Unit { get; set; } = string.Empty;
    }
}

public static class DateTimeExtensions
{
    public static DateTime TruncateToMinute(this DateTime dt)
    {
        return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
    }

    public static DateTime TruncateToHour(this DateTime dt)
    {
        return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, 0, 0, dt.Kind);
    }

    public static DateTime TruncateToDay(this DateTime dt)
    {
        return new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, dt.Kind);
    }
}
