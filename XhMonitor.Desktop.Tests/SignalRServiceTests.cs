using System.Text.Json;
using FluentAssertions;
using XhMonitor.Desktop.Models;
using XhMonitor.Desktop.Services;

namespace XhMonitor.Desktop.Tests;

public class SignalRServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void DoneWhen_DeserializePayload_ReceivesValidProcessMetrics()
    {
        const string json = """
                            {
                              "timestamp": "2026-02-09T08:00:00Z",
                              "processCount": 1,
                              "processes": [
                                {
                                  "processId": 100,
                                  "processName": "llama-server",
                                  "metrics": {
                                    "cpu": 22.5,
                                    "memory": 1234.5
                                  }
                                }
                              ]
                            }
                            """;

        using var document = JsonDocument.Parse(json);
        var dto = SignalRService.DeserializePayload<ProcessDataDto>(document.RootElement, JsonOptions);

        dto.Should().NotBeNull();
        dto!.ProcessCount.Should().Be(1);
        dto.Processes.Should().HaveCount(1);
        dto.Processes[0].ProcessId.Should().Be(100);
        dto.Processes[0].Metrics["memory"].Should().Be(1234.5);
    }

    [Fact]
    public void DoneWhen_DeserializePayload_GetsNullLiteral_ShouldReturnNull()
    {
        using var document = JsonDocument.Parse("null");

        var dto = SignalRService.DeserializePayload<ProcessDataDto>(document.RootElement, JsonOptions);

        dto.Should().BeNull();
    }
}
