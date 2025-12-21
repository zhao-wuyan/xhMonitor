namespace XhMonitor.Core.Models;

/// <summary>
/// 指标值
/// </summary>
public class MetricValue
{
    /// <summary>
    /// 数值
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// 单位（如：%、MB、GB、°C等）
    /// </summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 采集时间
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 是否为错误状态
    /// </summary>
    public bool IsError { get; set; }

    /// <summary>
    /// 错误消息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 创建错误状态的指标值
    /// </summary>
    public static MetricValue Error(string message) => new()
    {
        IsError = true,
        ErrorMessage = message,
        Timestamp = DateTime.Now
    };
}
