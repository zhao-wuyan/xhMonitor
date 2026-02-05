using System.Net.Http;
using FluentAssertions;
using XhMonitor.Desktop.Services;
using XhMonitor.Desktop.ViewModels;
using Xunit;

namespace XhMonitor.Desktop.Tests;

public class SettingsViewModelTests
{
    private sealed class FakeServiceDiscovery : IServiceDiscovery
    {
        public string ApiBaseUrl { get; init; } = "http://localhost:35179";
        public string SignalRUrl { get; init; } = "http://localhost:35179/hubs/metrics";
        public int ApiPort { get; init; } = 35179;
        public int SignalRPort { get; init; } = 35179;
        public int WebPort { get; init; } = 35180;
    }

    [Fact]
    public void LocalIpEndpoint_ShouldAppendPort_ForSingleIp()
    {
        var vm = new SettingsViewModel(new HttpClient(), new FakeServiceDiscovery { WebPort = 35180 });
        vm.LocalIpAddress = "10.0.0.1";

        vm.LocalIpEndpoint.Should().Be("10.0.0.1:35180");
    }

    [Fact]
    public void LocalIpEndpoint_ShouldAppendPort_ForMultipleIps()
    {
        var vm = new SettingsViewModel(new HttpClient(), new FakeServiceDiscovery { WebPort = 35180 });
        vm.LocalIpAddress = "10.0.0.1, 192.168.1.2";

        vm.LocalIpEndpoint.Should().Be("10.0.0.1:35180, 192.168.1.2:35180");
    }

    [Fact]
    public void LocalIpEndpoint_ShouldFallbackToPortHint_WhenNoValidIp()
    {
        var vm = new SettingsViewModel(new HttpClient(), new FakeServiceDiscovery { WebPort = 35180 });
        vm.LocalIpAddress = "未检测到";

        vm.LocalIpEndpoint.Should().Be("未检测到 (端口 35180)");
    }
}

