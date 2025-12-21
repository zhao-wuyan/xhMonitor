using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Entities;
using XhMonitor.Service.Core;
using XhMonitor.Service.Data;
using XhMonitor.Service.Models;

namespace XhMonitor.Service.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly IDbContextFactory<MonitorDbContext> _contextFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigController> _logger;
    private readonly MetricProviderRegistry _providerRegistry;

    public ConfigController(
        IDbContextFactory<MonitorDbContext> contextFactory,
        IConfiguration configuration,
        ILogger<ConfigController> logger,
        MetricProviderRegistry providerRegistry)
    {
        _contextFactory = contextFactory;
        _configuration = configuration;
        _logger = logger;
        _providerRegistry = providerRegistry;
    }

    [HttpGet]
    public IActionResult GetConfig()
    {
        var config = new
        {
            Monitor = new
            {
                IntervalSeconds = _configuration.GetValue<int>("Monitor:IntervalSeconds", 5),
                Keywords = _configuration.GetSection("Monitor:Keywords").Get<string[]>() ?? Array.Empty<string>()
            },
            MetricProviders = new
            {
                PluginDirectory = _configuration["MetricProviders:PluginDirectory"] ?? string.Empty
            }
        };

        return Ok(config);
    }

    [HttpGet("alerts")]
    public async Task<IActionResult> GetAlerts()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var alerts = await context.AlertConfigurations
            .OrderBy(a => a.MetricId)
            .ToListAsync();

        return Ok(alerts);
    }

    [HttpPost("alerts")]
    public async Task<IActionResult> UpdateAlert([FromBody] AlertConfiguration alert)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var existing = await context.AlertConfigurations
            .FirstOrDefaultAsync(a => a.Id == alert.Id);

        if (existing == null)
        {
            alert.CreatedAt = DateTime.UtcNow;
            alert.UpdatedAt = DateTime.UtcNow;
            context.AlertConfigurations.Add(alert);
        }
        else
        {
            existing.Threshold = alert.Threshold;
            existing.IsEnabled = alert.IsEnabled;
            existing.UpdatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();

        return Ok(alert);
    }

    [HttpDelete("alerts/{id}")]
    public async Task<IActionResult> DeleteAlert(int id)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var alert = await context.AlertConfigurations.FindAsync(id);

        if (alert == null)
        {
            return NotFound();
        }

        context.AlertConfigurations.Remove(alert);
        await context.SaveChangesAsync();

        return NoContent();
    }

    [HttpGet("metrics")]
    public IActionResult GetMetrics()
    {
        var providers = _providerRegistry.GetAllProviders();

        var metricColorMap = new Dictionary<string, string>
        {
            ["cpu"] = "#3b82f6",
            ["memory"] = "#10b981",
            ["gpu"] = "#8b5cf6",
            ["vram"] = "#f59e0b"
        };

        var metricIconMap = new Dictionary<string, string>
        {
            ["cpu"] = "Cpu",
            ["memory"] = "MemoryStick",
            ["gpu"] = "Gpu",
            ["vram"] = "HardDrive"
        };

        var metrics = providers.Select(p => new MetricMetadata
        {
            MetricId = p.MetricId,
            DisplayName = p.DisplayName,
            Unit = p.Unit,
            Type = p.Type.ToString(),
            Category = p.Type.ToString(),
            Color = metricColorMap.GetValueOrDefault(p.MetricId.ToLower()),
            Icon = metricIconMap.GetValueOrDefault(p.MetricId.ToLower())
        }).ToList();

        return Ok(metrics);
    }

    [HttpGet("health")]
    public async Task<IActionResult> GetHealth()
    {
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync();
            await context.Database.CanConnectAsync();

            return Ok(new
            {
                Status = "Healthy",
                Timestamp = DateTime.UtcNow,
                Database = "Connected"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");

            return StatusCode(503, new
            {
                Status = "Unhealthy",
                Timestamp = DateTime.UtcNow,
                Error = ex.Message
            });
        }
    }
}
