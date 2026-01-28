using System.Security.Principal;
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
    private readonly ProcessScanner _processScanner;

    public ConfigController(
        IDbContextFactory<MonitorDbContext> contextFactory,
        IConfiguration configuration,
        ILogger<ConfigController> logger,
        MetricProviderRegistry providerRegistry,
        ProcessScanner processScanner)
    {
        _contextFactory = contextFactory;
        _configuration = configuration;
        _logger = logger;
        _providerRegistry = providerRegistry;
        _processScanner = processScanner;
    }

    [HttpGet]
    public IActionResult GetConfig()
    {
        var processIntervalSeconds = _configuration.GetValue<int>("Monitor:IntervalSeconds", 5);
        var systemIntervalSeconds = _configuration.GetValue<int>("Monitor:SystemUsageIntervalSeconds", 1);

        var config = new
        {
            Monitor = new
            {
                IntervalSeconds = processIntervalSeconds,
                SystemUsageIntervalSeconds = systemIntervalSeconds,
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

        var metrics = providers.Select(p =>
        {
            var metricId = p.MetricId;
            var displayName = p.DisplayName;
            var unit = p.Unit;
            var type = p.Type.ToString();
            var category = p.Type.ToString();

            if (string.Equals(metricId, "cpu", StringComparison.OrdinalIgnoreCase))
            {
                displayName = "CPU Usage";
            }
            else if (string.Equals(metricId, "gpu", StringComparison.OrdinalIgnoreCase))
            {
                displayName = "GPU Usage";
            }
            else if (string.Equals(metricId, "memory", StringComparison.OrdinalIgnoreCase))
            {
                displayName = "Memory Usage";
                unit = "MB";
                type = "Size";
                category = "Size";
            }
            else if (string.Equals(metricId, "vram", StringComparison.OrdinalIgnoreCase))
            {
                displayName = "VRAM Usage";
                unit = "MB";
                type = "Size";
                category = "Size";
            }

            return new MetricMetadata
            {
                MetricId = metricId,
                DisplayName = displayName,
                Unit = unit,
                Type = type,
                Category = category,
                Color = metricColorMap.GetValueOrDefault(metricId.ToLower()),
                Icon = metricIconMap.GetValueOrDefault(metricId.ToLower())
            };
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

    /// <summary>
    /// 获取所有应用设置 (按分类分组)
    /// </summary>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(Dictionary<string, Dictionary<string, string>>), 200)]
    public async Task<IActionResult> GetSettings()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var settings = await context.ApplicationSettings
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Key)
            .ToListAsync();

        // 按分类分组
        var grouped = settings
            .GroupBy(s => s.Category)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(x => x.Key, x => x.Value)
            );

        return Ok(grouped);
    }

    /// <summary>
    /// 更新单个配置项
    /// </summary>
    /// <param name="category">配置分类</param>
    /// <param name="key">配置键</param>
    /// <param name="request">配置值</param>
    [HttpPut("settings/{category}/{key}")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateSetting(
        string category,
        string key,
        [FromBody] UpdateSettingRequest request)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var setting = await context.ApplicationSettings
            .FirstOrDefaultAsync(s => s.Category == category && s.Key == key);

        if (setting == null)
        {
            return NotFound(new { Message = $"配置项 {category}.{key} 不存在" });
        }

        setting.Value = request.Value;
        setting.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();

        _logger.LogInformation("配置已更新: {Category}.{Key} = {Value}", category, key, request.Value);

        return Ok(new { Message = "配置已更新", Category = category, Key = key, Value = request.Value });
    }

    /// <summary>
    /// 批量更新配置 (用于保存整个设置页)
    /// </summary>
    [HttpPut("settings")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> UpdateSettings([FromBody] Dictionary<string, Dictionary<string, string>> settings)
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var updatedCount = 0;
        var timestamp = DateTime.UtcNow;
        var processKeywordsUpdated = false;

        foreach (var categoryGroup in settings)
        {
            var category = categoryGroup.Key;
            foreach (var item in categoryGroup.Value)
            {
                var key = item.Key;
                var value = item.Value;

                var setting = await context.ApplicationSettings
                    .FirstOrDefaultAsync(s => s.Category == category && s.Key == key);

                if (setting != null)
                {
                    setting.Value = value;
                    setting.UpdatedAt = timestamp;
                    updatedCount++;

                    // 检测是否更新了进程关键字
                    if (category == "DataCollection" && key == "ProcessKeywords")
                    {
                        processKeywordsUpdated = true;
                    }
                }
            }
        }

        await context.SaveChangesAsync();

        _logger.LogInformation("批量更新 {Count} 个配置项", updatedCount);

        // 如果进程关键字被更新,触发 ProcessScanner 重新加载
        if (processKeywordsUpdated)
        {
            _logger.LogInformation("检测到进程关键字更新,触发 ProcessScanner 重新加载");
            await _processScanner.ReloadKeywordsAsync();
        }

        return Ok(new { Message = $"成功更新 {updatedCount} 个配置项", UpdatedCount = updatedCount });
    }

    /// <summary>
    /// 获取 Service 当前的管理员权限状态
    /// </summary>
    [HttpGet("admin-status")]
    [ProducesResponseType(typeof(object), 200)]
    public IActionResult GetAdminStatus()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

            return Ok(new
            {
                IsAdmin = isAdmin,
                Message = isAdmin ? "Service 正在以管理员权限运行" : "Service 未以管理员权限运行"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查管理员权限状态失败");
            return Ok(new
            {
                IsAdmin = false,
                Message = $"无法检测管理员权限状态: {ex.Message}"
            });
        }
    }
}
