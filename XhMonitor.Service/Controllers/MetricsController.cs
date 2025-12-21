using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Enums;
using XhMonitor.Service.Data;

namespace XhMonitor.Service.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IDbContextFactory<MonitorDbContext> _contextFactory;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        IDbContextFactory<MonitorDbContext> contextFactory,
        ILogger<MetricsController> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(
        [FromQuery] int? processId,
        [FromQuery] string? processName,
        [FromQuery] string? keyword)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.ProcessMetricRecords.AsQueryable();

        if (processId.HasValue)
        {
            query = query.Where(r => r.ProcessId == processId.Value);
        }

        if (!string.IsNullOrWhiteSpace(processName))
        {
            query = query.Where(r => r.ProcessName.Contains(processName));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(r => r.ProcessName.Contains(keyword) ||
                                    (r.CommandLine != null && r.CommandLine.Contains(keyword)));
        }

        var latestTimestamp = await query.MaxAsync(r => (DateTime?)r.Timestamp);

        if (latestTimestamp == null)
        {
            return Ok(Array.Empty<object>());
        }

        var records = await query
            .Where(r => r.Timestamp == latestTimestamp.Value)
            .OrderBy(r => r.ProcessName)
            .ToListAsync();

        return Ok(records);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int processId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string aggregation = "raw")
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        if (aggregation.Equals("raw", StringComparison.OrdinalIgnoreCase))
        {
            var query = context.ProcessMetricRecords
                .Where(r => r.ProcessId == processId);

            if (from.HasValue)
            {
                query = query.Where(r => r.Timestamp >= from.Value);
            }

            if (to.HasValue)
            {
                query = query.Where(r => r.Timestamp <= to.Value);
            }

            var records = await query
                .OrderBy(r => r.Timestamp)
                .ToListAsync();

            return Ok(records);
        }
        else
        {
            var level = aggregation.ToLower() switch
            {
                "minute" => AggregationLevel.Minute,
                "hour" => AggregationLevel.Hour,
                "day" => AggregationLevel.Day,
                _ => AggregationLevel.Minute
            };

            var query = context.AggregatedMetricRecords
                .Where(r => r.ProcessId == processId && r.AggregationLevel == level);

            if (from.HasValue)
            {
                query = query.Where(r => r.Timestamp >= from.Value);
            }

            if (to.HasValue)
            {
                query = query.Where(r => r.Timestamp <= to.Value);
            }

            var records = await query
                .OrderBy(r => r.Timestamp)
                .ToListAsync();

            return Ok(records);
        }
    }

    [HttpGet("processes")]
    public async Task<IActionResult> GetProcesses(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? keyword)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.ProcessMetricRecords.AsQueryable();

        if (from.HasValue)
        {
            query = query.Where(r => r.Timestamp >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(r => r.Timestamp <= to.Value);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(r => r.ProcessName.Contains(keyword) ||
                                    (r.CommandLine != null && r.CommandLine.Contains(keyword)));
        }

        var processes = await query
            .GroupBy(r => new { r.ProcessId, r.ProcessName })
            .Select(g => new
            {
                g.Key.ProcessId,
                g.Key.ProcessName,
                LastSeen = g.Max(r => r.Timestamp),
                RecordCount = g.Count()
            })
            .OrderByDescending(p => p.LastSeen)
            .ToListAsync();

        return Ok(processes);
    }

    [HttpGet("aggregations")]
    public async Task<IActionResult> GetAggregations(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] string aggregation = "minute")
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var level = aggregation.ToLower() switch
        {
            "minute" => AggregationLevel.Minute,
            "hour" => AggregationLevel.Hour,
            "day" => AggregationLevel.Day,
            _ => AggregationLevel.Minute
        };

        var records = await context.AggregatedMetricRecords
            .Where(r => r.AggregationLevel == level &&
                       r.Timestamp >= from &&
                       r.Timestamp <= to)
            .OrderBy(r => r.Timestamp)
            .ThenBy(r => r.ProcessName)
            .ToListAsync();

        return Ok(records);
    }
}
