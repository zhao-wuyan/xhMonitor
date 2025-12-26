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

    [JsonPropertyName("systemStats")]
    public SystemStatsDto? SystemStats { get; set; }
}

public class SystemStatsDto
{
    [JsonPropertyName("totalCpu")]
    public double TotalCpu { get; set; }

    [JsonPropertyName("totalMemory")]
    public double TotalMemory { get; set; }

    [JsonPropertyName("totalGpu")]
    public double TotalGpu { get; set; }

    [JsonPropertyName("totalVram")]
    public double TotalVram { get; set; }

    [JsonPropertyName("maxMemory")]
    public double MaxMemory { get; set; }

    [JsonPropertyName("maxVram")]
    public double MaxVram { get; set; }
}
