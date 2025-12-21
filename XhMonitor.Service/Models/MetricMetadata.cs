using XhMonitor.Core.Enums;

namespace XhMonitor.Service.Models;

public class MetricMetadata
{
    public required string MetricId { get; init; }
    public required string DisplayName { get; init; }
    public required string Unit { get; init; }
    public required string Type { get; init; }
    public string? Category { get; init; }
    public string? Color { get; init; }
    public string? Icon { get; init; }
}
