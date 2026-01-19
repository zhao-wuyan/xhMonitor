namespace XhMonitor.Core.Models;

public class WidgetSettings
{
    /// <summary>
    /// 全局开关：是否启用指标点击功能
    /// </summary>
    public bool EnableMetricClick { get; set; } = false;

    /// <summary>
    /// 每个指标的点击配置
    /// </summary>
    public Dictionary<string, MetricClickConfig> MetricClickActions { get; set; } = new();
}

public class MetricClickConfig
{
    /// <summary>
    /// 是否启用该指标的点击功能
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 点击时执行的动作类型
    /// </summary>
    public string Action { get; set; } = "none";

    /// <summary>
    /// 动作参数（可选）
    /// </summary>
    public Dictionary<string, string>? Parameters { get; set; }
}
