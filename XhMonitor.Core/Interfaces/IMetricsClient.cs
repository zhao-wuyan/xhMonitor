namespace XhMonitor.Core.Interfaces;

/// <summary>
/// MetricsHub 客户端契约（强类型 SignalR Hub）
/// </summary>
public interface IMetricsClient
{
    /// <summary>
    /// 接收硬件限制数据（最大内存/显存等）
    /// </summary>
    /// <param name="data">
    /// 数据结构：
    /// <list type="bullet">
    /// <item><description><c>Timestamp</c>: 时间戳</description></item>
    /// <item><description><c>MaxMemory</c>: 最大内存（MB）</description></item>
    /// <item><description><c>MaxVram</c>: 最大显存（MB）</description></item>
    /// </list>
    /// </param>
    Task ReceiveHardwareLimits(object data);

    /// <summary>
    /// 接收系统使用率数据（CPU/GPU/内存/显存等）
    /// </summary>
    /// <param name="data">
    /// 数据结构：
    /// <list type="bullet">
    /// <item><description><c>Timestamp</c>: 时间戳</description></item>
    /// <item><description><c>TotalCpu</c>: CPU 使用率（%）</description></item>
    /// <item><description><c>TotalGpu</c>: GPU 使用率（%）</description></item>
    /// <item><description><c>TotalMemory</c>: 内存使用量（MB）</description></item>
    /// <item><description><c>TotalVram</c>: 显存使用量（MB）</description></item>
    /// <item><description><c>UploadSpeed</c>: 上传速度（MB/s）</description></item>
    /// <item><description><c>DownloadSpeed</c>: 下载速度（MB/s）</description></item>
    /// <item><description><c>MaxMemory</c>: 最大内存（MB）</description></item>
    /// <item><description><c>MaxVram</c>: 最大显存（MB）</description></item>
    /// <item><description><c>Disks</c>: 物理硬盘列表（包含 <c>Name</c> / <c>TotalBytes</c> / <c>UsedBytes</c> / <c>ReadSpeed</c> / <c>WriteSpeed</c>，其中容量单位为 Bytes，速度单位为 MB/s，字段可能为 null）</description></item>
    /// </list>
    /// </param>
    Task ReceiveSystemUsage(object data);

    /// <summary>
    /// 接收进程实时指标数据（按进程聚合的指标快照）
    /// </summary>
    /// <param name="data">
    /// 数据结构：
    /// <list type="bullet">
    /// <item><description><c>Timestamp</c>: 时间戳</description></item>
    /// <item><description><c>ProcessCount</c>: 进程数量</description></item>
    /// <item><description><c>Processes</c>: 进程列表（包含 <c>ProcessId</c> / <c>ProcessName</c> / <c>Metrics</c> 字典）</description></item>
    /// </list>
    /// </param>
    Task ReceiveProcessMetrics(object data);

    /// <summary>
    /// 接收进程元数据（命令行/显示名等）
    /// </summary>
    /// <param name="data">
    /// 数据结构：
    /// <list type="bullet">
    /// <item><description><c>Timestamp</c>: 时间戳</description></item>
    /// <item><description><c>ProcessCount</c>: 进程数量</description></item>
    /// <item><description><c>Processes</c>: 进程列表（包含 <c>ProcessId</c> / <c>ProcessName</c> / <c>CommandLine</c> / <c>DisplayName</c>）</description></item>
    /// </list>
    /// </param>
    Task ReceiveProcessMetadata(object data);
}
