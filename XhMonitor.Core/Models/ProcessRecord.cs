namespace XhMonitor.Core.Models;

/// <summary>
/// 进程记录
/// </summary>
public class ProcessRecord
{
    /// <summary>
    /// 进程ID
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// 进程名称
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// 启动命令行
    /// </summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>
    /// 指标数据（动态维度）
    /// </summary>
    public Dictionary<string, MetricValue> Metrics { get; set; } = new();

    /// <summary>
    /// 记录时间
    /// </summary>
    public DateTime Timestamp { get; set; }
}
