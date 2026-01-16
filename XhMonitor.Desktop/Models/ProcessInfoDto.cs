using System.Text.Json.Serialization;

namespace XhMonitor.Desktop.Models;

public class ProcessInfoDto
{
    [JsonPropertyName("processId")]
    public int ProcessId { get; set; }

    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = string.Empty;

    [JsonPropertyName("commandLine")]
    public string CommandLine { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("metrics")]
    public Dictionary<string, double> Metrics { get; set; } = new();
}
