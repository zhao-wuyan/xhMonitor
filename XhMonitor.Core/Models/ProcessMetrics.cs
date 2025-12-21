namespace XhMonitor.Core.Models;

public class ProcessMetrics
{
    public required ProcessInfo Info { get; init; }
    public Dictionary<string, MetricValue> Metrics { get; init; } = new();
    public DateTime Timestamp { get; init; }
}
