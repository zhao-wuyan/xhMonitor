using System.Text.Json.Serialization;

namespace XhMonitor.Desktop.Models;

public class MetricsDataDto
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("processCount")]
    public int ProcessCount { get; set; }

    [JsonPropertyName("processes")]
    public List<ProcessInfoDto> Processes { get; set; } = new();
}
