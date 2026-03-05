using XhMonitor.Core.Models;

namespace XhMonitor.Service.Core;

/// <summary>
/// 对采集到的 <see cref="ProcessMetrics"/> 做二次增强（例如：基于进程命令行、HTTP 指标端点等补充更多指标）。
/// 设计为可插拔，便于未来支持多种进程类型的不同监控方式。
/// </summary>
public interface IProcessMetricsEnricher
{
    Task EnrichAsync(IReadOnlyList<ProcessMetrics> metrics, CancellationToken cancellationToken = default);
}

