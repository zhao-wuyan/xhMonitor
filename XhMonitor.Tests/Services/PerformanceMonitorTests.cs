using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Moq;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Service.Core;
using XhMonitor.Service.Data;

namespace XhMonitor.Tests.Services;

public class PerformanceMonitorTests
{
    [Fact]
    public async Task DoneWhen_ProviderTimesOut_SubsequentCallsUseCooldown()
    {
        var provider = new Mock<IMetricProvider>();
        provider.SetupGet(p => p.MetricId).Returns("vram");
        provider.SetupGet(p => p.DisplayName).Returns("VRAM");
        provider.SetupGet(p => p.Unit).Returns("MB");
        provider.SetupGet(p => p.Type).Returns(MetricType.Size);
        provider.Setup(p => p.IsSupported()).Returns(true);
        provider.Setup(p => p.GetSystemTotalAsync()).ReturnsAsync(0d);
        provider.Setup(p => p.CollectAsync(It.IsAny<int>()))
            .Returns(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                return new MetricValue { Value = 1, Timestamp = DateTime.UtcNow };
            });

        using var registry = CreateRegistry(provider.Object);
        var monitor = new PerformanceMonitor(
            Mock.Of<ILogger<PerformanceMonitor>>(),
            CreateProcessScanner(),
            registry);

        var method = typeof(PerformanceMonitor).GetMethod("CollectMetricSafeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var first = await InvokeCollectMetricSafeAsync(method!, monitor, provider.Object, 100);
        var second = await InvokeCollectMetricSafeAsync(method!, monitor, provider.Object, 200);

        first.MetricId.Should().Be("vram");
        first.Value.IsError.Should().BeTrue();
        first.Value.ErrorMessage.Should().Be("Timeout");
        second.Value.IsError.Should().BeTrue();
        second.Value.ErrorMessage.Should().Be("Cooling down after timeout");
        provider.Verify(p => p.CollectAsync(It.IsAny<int>()), Times.Once);
        monitor.DebugIsProviderCoolingDown("vram").Should().BeTrue();
    }

    [Fact]
    public async Task DoneWhen_ProviderSucceeds_CooldownIsCleared()
    {
        var provider = new Mock<IMetricProvider>();
        provider.SetupGet(p => p.MetricId).Returns("vram");
        provider.SetupGet(p => p.DisplayName).Returns("VRAM");
        provider.SetupGet(p => p.Unit).Returns("MB");
        provider.SetupGet(p => p.Type).Returns(MetricType.Size);
        provider.Setup(p => p.IsSupported()).Returns(true);
        provider.Setup(p => p.GetSystemTotalAsync()).ReturnsAsync(0d);
        provider.Setup(p => p.CollectAsync(It.IsAny<int>()))
            .ReturnsAsync(new MetricValue { Value = 42, Timestamp = DateTime.UtcNow });

        using var registry = CreateRegistry(provider.Object);
        var monitor = new PerformanceMonitor(
            Mock.Of<ILogger<PerformanceMonitor>>(),
            CreateProcessScanner(),
            registry);
        monitor.DebugSetProviderCooldown("vram", DateTime.UtcNow.AddSeconds(10));

        var method = typeof(PerformanceMonitor).GetMethod("CollectMetricSafeAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        var result = await InvokeCollectMetricSafeAsync(method!, monitor, provider.Object, 100);

        result.Value.IsError.Should().BeTrue("active cooldown should short-circuit collection");
        provider.Verify(p => p.CollectAsync(It.IsAny<int>()), Times.Never);

        monitor.DebugSetProviderCooldown("vram", DateTime.UtcNow.AddMilliseconds(-1));
        var success = await InvokeCollectMetricSafeAsync(method!, monitor, provider.Object, 101);

        success.Value.IsError.Should().BeFalse();
        success.Value.Value.Should().Be(42);
        monitor.DebugIsProviderCoolingDown("vram").Should().BeFalse();
        provider.Verify(p => p.CollectAsync(It.IsAny<int>()), Times.Once);
    }

    private static async Task<(string MetricId, MetricValue Value)> InvokeCollectMetricSafeAsync(
        MethodInfo method,
        PerformanceMonitor monitor,
        IMetricProvider provider,
        int processId)
    {
        var task = (Task<(string MetricId, MetricValue Value)>)method.Invoke(monitor, new object[] { provider, processId })!;
        return await task;
    }

    private static MetricProviderRegistry CreateRegistry(IMetricProvider provider)
    {
        var factory = new Mock<IMetricProviderFactory>();
        factory.Setup(f => f.GetSupportedMetricIds()).Returns(Array.Empty<string>());

        var registry = new MetricProviderRegistry(
            Mock.Of<ILogger<MetricProviderRegistry>>(),
            pluginDirectory: string.Empty,
            factory.Object);
        registry.RegisterProvider(provider);
        return registry;
    }

    private static ProcessScanner CreateProcessScanner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Monitor:Keywords:0"] = "llama-server"
            })
            .Build();

        var dbFactory = new Mock<IDbContextFactory<MonitorDbContext>>();
        var resolver = new Mock<IProcessNameResolver>();
        resolver.Setup(r => r.Resolve(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string processName, string? _) => processName);

        return new ProcessScanner(
            Mock.Of<ILogger<ProcessScanner>>(),
            configuration,
            resolver.Object,
            dbFactory.Object);
    }
}
