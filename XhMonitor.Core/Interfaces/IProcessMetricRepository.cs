using XhMonitor.Core.Models;

namespace XhMonitor.Core.Interfaces;

public interface IProcessMetricRepository
{
    Task SaveMetricsAsync(
        IReadOnlyCollection<ProcessMetrics> metrics,
        DateTime cycleTimestamp,
        CancellationToken cancellationToken = default);
}
