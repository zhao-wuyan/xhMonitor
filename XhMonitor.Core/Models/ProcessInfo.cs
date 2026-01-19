namespace XhMonitor.Core.Models;

public class ProcessInfo
{
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public string? CommandLine { get; init; }
    public string? DisplayName { get; init; }
    public List<string> MatchedKeywords { get; init; } = new();
}
