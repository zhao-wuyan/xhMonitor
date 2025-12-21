namespace XhMonitor.Core.Models;

/// <summary>
/// 系统总体资源占用摘要
/// </summary>
public class SystemSummary
{
    /// <summary>
    /// 总CPU使用率（%）
    /// </summary>
    public double TotalCpu { get; set; }

    /// <summary>
    /// 总内存占用（MB）
    /// </summary>
    public double TotalMemoryMB { get; set; }

    /// <summary>
    /// 总GPU使用率（%）
    /// </summary>
    public double TotalGpu { get; set; }

    /// <summary>
    /// 总显存占用（MB）
    /// </summary>
    public double TotalVramMB { get; set; }

    /// <summary>
    /// 监控的进程数量
    /// </summary>
    public int ProcessCount { get; set; }

    /// <summary>
    /// 统计时间
    /// </summary>
    public DateTime Timestamp { get; set; }
}
