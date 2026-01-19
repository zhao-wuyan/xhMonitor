namespace XhMonitor.Core.Models;

/// <summary>
/// VRAM 完整指标数据
/// </summary>
public class VramMetrics
{
    /// <summary>
    /// 已使用显存（MB）
    /// </summary>
    public double Used { get; set; }

    /// <summary>
    /// 显存总容量（MB）
    /// </summary>
    public double Total { get; set; }

    /// <summary>
    /// 使用率（%）
    /// </summary>
    public double UsagePercent => Total > 0 ? (Used / Total) * 100.0 : 0.0;

    /// <summary>
    /// 是否有效
    /// </summary>
    public bool IsValid => Total > 0;

    /// <summary>
    /// 采集时间
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 创建空的 VRAM 指标
    /// </summary>
    public static VramMetrics Empty => new() { Used = 0, Total = 0 };
}
