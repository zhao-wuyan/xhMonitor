using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Moq;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;
using XhMonitor.Core.Providers;

namespace XhMonitor.Tests.Providers;

public class SystemMetricProviderTests : IDisposable
{
    private readonly SystemMetricProvider _provider;

    public SystemMetricProviderTests()
    {
        _provider = new SystemMetricProvider(
            cpuProvider: null,
            gpuProvider: null,
            memoryProvider: null,
            vramProvider: null,
            logger: null,
            initializeDxgi: false);
    }

    [Fact]
    public void DoneWhen_GetMaxMemory_IsSynchronous()
    {
        var method = typeof(SystemMetricProvider).GetMethod("GetMaxMemory", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(double), "GetMaxMemory should be a synchronous method that returns double");
    }

    [Fact]
    public void DoneWhen_GetMemoryUsage_IsSynchronous()
    {
        var method = typeof(SystemMetricProvider).GetMethod("GetMemoryUsage", BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(double), "GetMemoryUsage should be a synchronous method that returns double");
    }

    [Fact]
    public void DoneWhen_GetMaxMemory_ReturnsZero_WhenNotWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        InvokeGetMaxMemory().Should().Be(0.0);
    }

    [Fact]
    public void DoneWhen_GetMemoryUsage_ReturnsZero_WhenNotWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        InvokeGetMemoryUsage().Should().Be(0.0);
    }

    [Fact]
    public void DoneWhen_GetMaxMemory_ReturnsPositiveTotalMemory_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        InvokeGetMaxMemory().Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void DoneWhen_GetMemoryUsage_ReturnsNonNegativeAndNotExceedTotal_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var total = InvokeGetMaxMemory();
        var used = InvokeGetMemoryUsage();

        used.Should().BeGreaterThanOrEqualTo(0.0);
        used.Should().BeLessThanOrEqualTo(total + 1.0, "used memory should not exceed total memory");
    }

    [Fact]
    public void DoneWhen_GetMaxMemory_ExecutesInUnder1ms_OnAverage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var getMaxMemory = CreateGetMaxMemoryDelegate();

        _ = getMaxMemory(_provider);

        const int iterations = 100;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            _ = getMaxMemory(_provider);
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        avgMs.Should().BeLessThan(1.0, "GlobalMemoryStatusEx should be fast and not incur thread pool overhead");
    }

    [Fact]
    public void DoneWhen_GetMemoryUsage_ExecutesInUnder1ms_OnAverage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var getMemoryUsage = CreateGetMemoryUsageDelegate();

        _ = getMemoryUsage(_provider);

        const int iterations = 100;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            _ = getMemoryUsage(_provider);
        }
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        avgMs.Should().BeLessThan(1.0, "GlobalMemoryStatusEx should be fast and not incur thread pool overhead");
    }

    [Fact]
    public async Task DoneWhen_GetHardwareLimitsAsync_ReturnsMaxMemoryAndMaxVram()
    {
        var vramProvider = CreateMetricProvider(
            metricId: "vram",
            systemTotal: 0.0,
            vramMetrics: new VramMetrics { Used = 100, Total = 512 });

        using var provider = new SystemMetricProvider(
            cpuProvider: null,
            gpuProvider: null,
            memoryProvider: null,
            vramProvider: vramProvider.Object,
            logger: null,
            initializeDxgi: false);

        var result = await provider.GetHardwareLimitsAsync();

        result.MaxVram.Should().Be(512);

        if (OperatingSystem.IsWindows())
        {
            result.MaxMemory.Should().BeGreaterThan(0.0);
        }
        else
        {
            result.MaxMemory.Should().Be(0.0);
        }
    }

    [Fact]
    public async Task DoneWhen_GetSystemUsageAsync_ReturnsExpectedCpuGpuVramAndValidTotalMemory()
    {
        var cpuProvider = CreateMetricProvider(metricId: "cpu", systemTotal: 12.3);
        var gpuProvider = CreateMetricProvider(metricId: "gpu", systemTotal: 45.6);
        var vramProvider = CreateMetricProvider(
            metricId: "vram",
            systemTotal: 0.0,
            vramMetrics: new VramMetrics { Used = 128, Total = 1024 });

        using var provider = new SystemMetricProvider(
            cpuProvider: cpuProvider.Object,
            gpuProvider: gpuProvider.Object,
            memoryProvider: null,
            vramProvider: vramProvider.Object,
            logger: null,
            initializeDxgi: false);

        var limits = await provider.GetHardwareLimitsAsync();
        var usage = await provider.GetSystemUsageAsync();

        usage.TotalCpu.Should().Be(12.3);
        usage.TotalGpu.Should().Be(45.6);
        usage.TotalVram.Should().Be(128);

        usage.TotalMemory.Should().BeGreaterThanOrEqualTo(0.0);
        usage.TotalMemory.Should().BeLessThanOrEqualTo(limits.MaxMemory + 1.0);
    }

    private static Mock<IMetricProvider> CreateMetricProvider(string metricId, double systemTotal, VramMetrics? vramMetrics = null)
    {
        var mock = new Mock<IMetricProvider>();
        mock.SetupGet(p => p.MetricId).Returns(metricId);
        mock.SetupGet(p => p.DisplayName).Returns(metricId);
        mock.SetupGet(p => p.Unit).Returns("%");
        mock.SetupGet(p => p.Type).Returns(MetricType.Percentage);
        mock.Setup(p => p.IsSupported()).Returns(true);
        mock.Setup(p => p.CollectAsync(It.IsAny<int>()))
            .ReturnsAsync(new MetricValue { Value = 0.0, Unit = "%", DisplayName = metricId, Timestamp = DateTime.UtcNow });
        mock.Setup(p => p.GetSystemTotalAsync()).ReturnsAsync(systemTotal);
        if (vramMetrics != null)
        {
            mock.Setup(p => p.GetVramMetricsAsync()).ReturnsAsync(vramMetrics);
        }
        return mock;
    }

    private double InvokeGetMaxMemory()
    {
        var getMaxMemory = CreateGetMaxMemoryDelegate();
        return getMaxMemory(_provider);
    }

    private double InvokeGetMemoryUsage()
    {
        var getMemoryUsage = CreateGetMemoryUsageDelegate();
        return getMemoryUsage(_provider);
    }

    private static Func<SystemMetricProvider, double> CreateGetMaxMemoryDelegate()
    {
        var method = typeof(SystemMetricProvider).GetMethod("GetMaxMemory", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return method!.CreateDelegate<Func<SystemMetricProvider, double>>();
    }

    private static Func<SystemMetricProvider, double> CreateGetMemoryUsageDelegate()
    {
        var method = typeof(SystemMetricProvider).GetMethod("GetMemoryUsage", BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        return method!.CreateDelegate<Func<SystemMetricProvider, double>>();
    }

    public void Dispose()
    {
        _provider.Dispose();
    }
}

