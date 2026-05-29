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

    /// <summary>
    /// llama-server (/metrics) 采样间隔（秒）。0 表示禁用。
    /// </summary>
    [Range(0, 3600)]
    public int LlamaMetricsIntervalSeconds { get; set; } = 1;

    /// <summary>
    /// llama-server (/metrics) 连续失败达到该次数后进入退避。0 表示不退避。
    /// </summary>
    [Range(0, 100)]
    public int LlamaMetricsFailureBackoffThreshold { get; set; }

    /// <summary>
    /// llama-server (/metrics) 失败退避时长（秒）。
    /// </summary>
    [Range(1, 3600)]
    public int LlamaMetricsFailureBackoffSeconds { get; set; } = 60;
}
