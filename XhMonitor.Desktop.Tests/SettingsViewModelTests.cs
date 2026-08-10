using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

    private sealed class FakeWebServerService : IWebServerService
    {
        public bool IsRunning { get; set; }
        public WebServerBindingMode CurrentBindingMode { get; set; } = WebServerBindingMode.Unknown;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SettingsViewModel CreateViewModel(
        FakeServiceDiscovery? serviceDiscovery = null,
        FakeWebServerService? webServerService = null)
    {
        return new SettingsViewModel(
            new HttpClient(),
            serviceDiscovery ?? new FakeServiceDiscovery(),
            webServerService ?? new FakeWebServerService(),
            NullLogger<SettingsViewModel>.Instance);
    }

    [Fact]
    public void LocalIpEndpoint_ShouldAppendPort_ForSingleIp()
    {
        var vm = CreateViewModel(new FakeServiceDiscovery { WebPort = 35180 });
        vm.LocalIpAddress = "10.0.0.1";

        vm.LocalIpEndpoint.Should().Be("10.0.0.1:35180");
    }

    [Fact]
    public void LocalIpEndpoint_ShouldAppendPort_ForMultipleIps()
    {
        var vm = CreateViewModel(new FakeServiceDiscovery { WebPort = 35180 });
        vm.LocalIpAddress = "10.0.0.1, 192.168.1.2";

        vm.LocalIpEndpoint.Should().Be("10.0.0.1:35180, 192.168.1.2:35180");
    }

    [Fact]
    public void LocalIpEndpoint_ShouldFallbackToPortHint_WhenNoValidIp()
    {
        var vm = CreateViewModel(new FakeServiceDiscovery { WebPort = 35180 });
        vm.LocalIpAddress = "未检测到";

        vm.LocalIpEndpoint.Should().Be("未检测到 (端口 35180)");
    }

    [Fact]
    public void DockVisualStyle_ShouldNormalizeToBar_WhenInputInvalid()
    {
        var vm = CreateViewModel();

        vm.DockVisualStyle = "unknown-style";

        vm.DockVisualStyle.Should().Be("Bar");
    }

    [Fact]
    public void DockVisualStyle_ShouldKeepText_WhenInputIsText()
    {
        var vm = CreateViewModel();

        vm.DockVisualStyle = "Text";

        vm.DockVisualStyle.Should().Be("Text");
    }

    [Theory]
    [InlineData(WebServerBindingMode.Lan, "当前实际监听：0.0.0.0:35180（局域网可访问）")]
    [InlineData(WebServerBindingMode.Localhost, "当前实际监听：localhost:35180（仅本机可访问）")]
    [InlineData(WebServerBindingMode.Unknown, "当前实际监听：未知（Web 服务尚未启动或状态未刷新）")]
    public void RefreshWebServerBindingStatus_ShouldMapRuntimeBindingModeToMessage(
        WebServerBindingMode bindingMode,
        string expectedMessage)
    {
        var webServerService = new FakeWebServerService { CurrentBindingMode = bindingMode };
        var vm = CreateViewModel(new FakeServiceDiscovery { WebPort = 35180 }, webServerService);

        vm.RefreshWebServerBindingStatus();

        vm.WebServerBindingMode.Should().Be(bindingMode);
        vm.WebServerBindingMessage.Should().Be(expectedMessage);
    }
}
