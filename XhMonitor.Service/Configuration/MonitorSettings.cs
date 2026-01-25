using System.ComponentModel.DataAnnotations;

namespace XhMonitor.Service.Configuration;

/// <summary>
/// 后端监控配置（appsettings.json: Monitor）。
/// </summary>
public sealed class MonitorSettings
{
    /// <summary>
    /// 进程指标采集间隔（秒）。
    /// </summary>
    [Range(1, 3600)]
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>
    /// 系统使用率采集间隔（秒）。
    /// </summary>
    [Range(1, 3600)]
    public int SystemUsageIntervalSeconds { get; set; } = 1;
}
