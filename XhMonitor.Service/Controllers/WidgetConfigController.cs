using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using XhMonitor.Core.Models;

namespace XhMonitor.Service.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class WidgetConfigController : ControllerBase
{
    private readonly string _configFilePath;
    private readonly ILogger<WidgetConfigController> _logger;

    public WidgetConfigController(ILogger<WidgetConfigController> logger, IConfiguration configuration)
    {
        _logger = logger;
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _configFilePath = Path.Combine(dataDir, "widget-settings.json");
    }

    /// <summary>
    /// 获取悬浮窗配置
    /// </summary>
    [HttpGet]
    public ActionResult<WidgetSettings> GetSettings()
    {
        try
        {
            if (!System.IO.File.Exists(_configFilePath))
            {
                return Ok(GetDefaultSettings());
            }

            var json = System.IO.File.ReadAllText(_configFilePath);
            var settings = JsonSerializer.Deserialize<WidgetSettings>(json);
            return Ok(settings ?? GetDefaultSettings());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load widget settings");
            return Ok(GetDefaultSettings());
        }
    }

    /// <summary>
    /// 更新悬浮窗配置
    /// </summary>
    [HttpPost]
    public ActionResult UpdateSettings([FromBody] WidgetSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            System.IO.File.WriteAllText(_configFilePath, json);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save widget settings");
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// 更新单个指标的点击配置
    /// </summary>
    [HttpPost("{metricId}")]
    public ActionResult UpdateMetricConfig(string metricId, [FromBody] MetricClickConfig config)
    {
        try
        {
            var settings = GetSettings().Value ?? GetDefaultSettings();
            settings.MetricClickActions[metricId] = config;

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            System.IO.File.WriteAllText(_configFilePath, json);

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update metric config for {MetricId}", metricId);
            return StatusCode(500, new { success = false, error = ex.Message });
        }
    }

    private WidgetSettings GetDefaultSettings()
    {
        return new WidgetSettings
        {
            EnableMetricClick = false,
            MetricClickActions = new Dictionary<string, MetricClickConfig>
            {
                ["cpu"] = new MetricClickConfig { Enabled = false, Action = "none" },
                ["memory"] = new MetricClickConfig { Enabled = false, Action = "none" },
                ["gpu"] = new MetricClickConfig { Enabled = false, Action = "none" },
                ["vram"] = new MetricClickConfig { Enabled = false, Action = "none" },
                ["power"] = new MetricClickConfig
                {
                    Enabled = false,
                    Action = "togglePowerMode",
                    Parameters = new Dictionary<string, string>
                    {
                        ["modes"] = "balanced,performance,powersaver"
                    }
                }
            }
        };
    }
}
