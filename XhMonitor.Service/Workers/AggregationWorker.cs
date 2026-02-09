using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XhMonitor.Core.Entities;
using XhMonitor.Core.Enums;
using XhMonitor.Service.Configuration;
using XhMonitor.Service.Data;
using System.Text.Json;

namespace XhMonitor.Service.Workers;

public sealed class AggregationWorker : BackgroundService
{
    private readonly IDbContextFactory<MonitorDbContext> _contextFactory;
    private readonly ILogger<AggregationWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);
    private readonly int _aggregationBatchSize;
    private const int DefaultAggregationBatchSize = 2000;
    private const int MinAggregationBatchSize = 100;
    private const int MaxAggregationBatchSize = 50000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public AggregationWorker(
        IDbContextFactory<MonitorDbContext> contextFactory,
        ILogger<AggregationWorker> logger,
        IOptions<AggregationSettings>? aggregationSettings = null)
    {
        _contextFactory = contextFactory;
        _logger = logger;

        var configuredBatchSize = aggregationSettings?.Value.BatchSize ?? DefaultAggregationBatchSize;
        _aggregationBatchSize = NormalizeBatchSize(configuredBatchSize);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AggregationWorker started, batch size: {BatchSize}", _aggregationBatchSize);

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

        var aggregatedRecords = await AggregateRawToMinuteInBatchesAsync(context, windowStart, windowEnd, cancellationToken);

        if (aggregatedRecords.Count == 0)
        {
            _logger.LogDebug("No raw records in window");
            return;
        }

        await context.AggregatedMetricRecords.AddRangeAsync(aggregatedRecords, cancellationToken);
        var savedCount = await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Saved {Count} minute aggregations", savedCount);
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

        var aggregatedRecords = await AggregateToHigherLevelInBatchesAsync(
            context,
            AggregationLevel.Minute,
            AggregationLevel.Hour,
            windowStart,
            windowEnd,
            timestamp => timestamp.TruncateToHour(),
            cancellationToken);

        if (aggregatedRecords.Count == 0)
        {
            _logger.LogDebug("No minute records in window");
            return;
        }

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

        var aggregatedRecords = await AggregateToHigherLevelInBatchesAsync(
            context,
            AggregationLevel.Hour,
            AggregationLevel.Day,
            windowStart,
            windowEnd,
            timestamp => timestamp.TruncateToDay(),
            cancellationToken);

        if (aggregatedRecords.Count == 0)
        {
            _logger.LogDebug("No hour records in window");
            return;
        }

        await context.AggregatedMetricRecords.AddRangeAsync(aggregatedRecords, cancellationToken);
        var savedCount = await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Saved {Count} day aggregations", savedCount);
    }

    private async Task<List<AggregatedMetricRecord>> AggregateRawToMinuteInBatchesAsync(
        MonitorDbContext context,
        DateTime windowStart,
        DateTime windowEnd,
        CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<AggregationBucketKey, Dictionary<string, MetricAggregation>>();
        long lastSeenId = 0;

        while (true)
        {
            var batch = await context.ProcessMetricRecords
                .AsNoTracking()
                .Where(r => r.Timestamp > windowStart && r.Timestamp < windowEnd && r.Id > lastSeenId)
                .OrderBy(r => r.Id)
                .Select(r => new RawMetricRow
                {
                    Id = r.Id,
                    ProcessId = r.ProcessId,
                    ProcessName = r.ProcessName,
                    Timestamp = r.Timestamp,
                    MetricsJson = r.MetricsJson
                })
                .Take(_aggregationBatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                var recordMetrics = JsonSerializer.Deserialize<Dictionary<string, MetricValue>>(row.MetricsJson, JsonOptions);
                if (recordMetrics == null || recordMetrics.Count == 0)
                {
                    continue;
                }

                var minuteTimestamp = DateTime.SpecifyKind(row.Timestamp.TruncateToMinute(), DateTimeKind.Utc);
                var bucketKey = new AggregationBucketKey(row.ProcessId, row.ProcessName, minuteTimestamp);
                if (!buckets.TryGetValue(bucketKey, out var metrics))
                {
                    metrics = new Dictionary<string, MetricAggregation>();
                    buckets[bucketKey] = metrics;
                }

                MergeMetricValues(metrics, recordMetrics);
            }

            lastSeenId = batch[^1].Id;
        }

        return BuildAggregatedRecords(buckets, AggregationLevel.Minute);
    }

    private async Task<List<AggregatedMetricRecord>> AggregateToHigherLevelInBatchesAsync(
        MonitorDbContext context,
        AggregationLevel sourceLevel,
        AggregationLevel targetLevel,
        DateTime windowStart,
        DateTime windowEnd,
        Func<DateTime, DateTime> truncateFunc,
        CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<AggregationBucketKey, Dictionary<string, MetricAggregation>>();
        long lastSeenId = 0;

        while (true)
        {
            var batch = await context.AggregatedMetricRecords
                .AsNoTracking()
                .Where(r => r.AggregationLevel == sourceLevel &&
                            r.Timestamp > windowStart &&
                            r.Timestamp < windowEnd &&
                            r.Id > lastSeenId)
                .OrderBy(r => r.Id)
                .Select(r => new AggregatedMetricRow
                {
                    Id = r.Id,
                    ProcessId = r.ProcessId,
                    ProcessName = r.ProcessName,
                    Timestamp = r.Timestamp,
                    MetricsJson = r.MetricsJson
                })
                .Take(_aggregationBatchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                var recordMetrics = JsonSerializer.Deserialize<Dictionary<string, MetricAggregation>>(row.MetricsJson, JsonOptions);
                if (recordMetrics == null || recordMetrics.Count == 0)
                {
                    continue;
                }

                var bucketTimestamp = DateTime.SpecifyKind(truncateFunc(row.Timestamp), DateTimeKind.Utc);
                var bucketKey = new AggregationBucketKey(row.ProcessId, row.ProcessName, bucketTimestamp);
                if (!buckets.TryGetValue(bucketKey, out var metrics))
                {
                    metrics = new Dictionary<string, MetricAggregation>();
                    buckets[bucketKey] = metrics;
                }

                MergeMetricAggregations(metrics, recordMetrics);
            }

            lastSeenId = batch[^1].Id;
        }

        return BuildAggregatedRecords(buckets, targetLevel);
    }

    private static void MergeMetricValues(
        Dictionary<string, MetricAggregation> target,
        IReadOnlyDictionary<string, MetricValue> source)
    {
        foreach (var (metricId, metricValue) in source)
        {
            if (!target.TryGetValue(metricId, out var aggregate))
            {
                aggregate = new MetricAggregation
                {
                    Unit = metricValue.Unit ?? string.Empty
                };
                target[metricId] = aggregate;
            }

            var value = metricValue.Value;
            aggregate.Min = Math.Min(aggregate.Min, value);
            aggregate.Max = Math.Max(aggregate.Max, value);
            aggregate.Sum += value;
            aggregate.Count++;
        }
    }

    private static void MergeMetricAggregations(
        Dictionary<string, MetricAggregation> target,
        IReadOnlyDictionary<string, MetricAggregation> source)
    {
        foreach (var (metricId, metricAggregation) in source)
        {
            if (!target.TryGetValue(metricId, out var aggregate))
            {
                aggregate = new MetricAggregation
                {
                    Min = double.MaxValue,
                    Max = double.MinValue,
                    Unit = metricAggregation.Unit
                };
                target[metricId] = aggregate;
            }

            aggregate.Min = Math.Min(aggregate.Min, metricAggregation.Min);
            aggregate.Max = Math.Max(aggregate.Max, metricAggregation.Max);
            aggregate.Sum += metricAggregation.Sum;
            aggregate.Count += metricAggregation.Count;
        }
    }

    private static List<AggregatedMetricRecord> BuildAggregatedRecords(
        Dictionary<AggregationBucketKey, Dictionary<string, MetricAggregation>> buckets,
        AggregationLevel targetLevel)
    {
        if (buckets.Count == 0)
        {
            return [];
        }

        var results = new List<AggregatedMetricRecord>(buckets.Count);
        foreach (var (bucketKey, metrics) in buckets
                     .OrderBy(entry => entry.Key.Timestamp)
                     .ThenBy(entry => entry.Key.ProcessId))
        {
            foreach (var aggregate in metrics.Values)
            {
                aggregate.Avg = aggregate.Count > 0 ? aggregate.Sum / aggregate.Count : 0;
            }

            var metricsJson = JsonSerializer.Serialize(metrics, JsonOptions);
            results.Add(new AggregatedMetricRecord
            {
                ProcessId = bucketKey.ProcessId,
                ProcessName = bucketKey.ProcessName,
                AggregationLevel = targetLevel,
                Timestamp = bucketKey.Timestamp,
                MetricsJson = metricsJson
            });
        }

        return results;
    }

    private readonly record struct AggregationBucketKey(int ProcessId, string ProcessName, DateTime Timestamp);

    private sealed class RawMetricRow
    {
        public long Id { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public string MetricsJson { get; init; } = string.Empty;
    }

    private sealed class AggregatedMetricRow
    {
        public long Id { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public string MetricsJson { get; init; } = string.Empty;
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

    internal static int NormalizeBatchSize(int configuredBatchSize)
    {
        if (configuredBatchSize <= 0)
        {
            return DefaultAggregationBatchSize;
        }

        return Math.Clamp(configuredBatchSize, MinAggregationBatchSize, MaxAggregationBatchSize);
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
