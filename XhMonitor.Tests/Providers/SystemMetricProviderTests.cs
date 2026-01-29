using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using LibreHardwareMonitor.Hardware;
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
            Array.Empty<IMetricProvider>(),
            logger: null);
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
            new[] { vramProvider.Object },
            logger: null);

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
            new[] { cpuProvider.Object, gpuProvider.Object, vramProvider.Object },
            logger: null);

        var limits = await provider.GetHardwareLimitsAsync();
        var usage = await provider.GetSystemUsageAsync();

        usage.TotalCpu.Should().Be(12.3);
        usage.TotalGpu.Should().Be(45.6);
        usage.TotalVram.Should().Be(128);
        usage.UploadSpeed.Should().Be(0.0);
        usage.DownloadSpeed.Should().Be(0.0);

        usage.TotalMemory.Should().BeGreaterThanOrEqualTo(0.0);
        usage.TotalMemory.Should().BeLessThanOrEqualTo(limits.MaxMemory + 1.0);
    }

    [Fact]
    public async Task DoneWhen_GetSystemUsageAsync_IncludesNetworkThroughput_WhenHardwareManagerAvailable()
    {
        var hardwareManager = new Mock<ILibreHardwareManager>();
        hardwareManager.SetupGet(m => m.IsAvailable).Returns(true);
        hardwareManager.Setup(m => m.GetSensorValues(It.IsAny<IReadOnlyCollection<HardwareType>>(), SensorType.Throughput))
            .Returns(new List<SensorReading>
            {
                // LibreHardwareMonitor Throughput 原始单位为 Bytes/s（B/s）
                new(HardwareType.Network, "Intel(R) Ethernet", SensorType.Throughput, "Download Speed", 2 * 1024 * 1024f),
                new(HardwareType.Network, "Intel(R) Ethernet", SensorType.Throughput, "Upload Speed", 1024 * 1024f)
            });

        using var provider = new SystemMetricProvider(
            Array.Empty<IMetricProvider>(),
            logger: null,
            hardwareManager: hardwareManager.Object);

        var usage = await provider.GetSystemUsageAsync();

        usage.DownloadSpeed.Should().Be(2.0);
        usage.UploadSpeed.Should().Be(1.0);
    }

    [Fact]
    public async Task DoneWhen_GetSystemUsageAsync_ExcludesVirtualAdapters()
    {
        var hardwareManager = new Mock<ILibreHardwareManager>();
        hardwareManager.SetupGet(m => m.IsAvailable).Returns(true);
        hardwareManager.Setup(m => m.GetSensorValues(It.IsAny<IReadOnlyCollection<HardwareType>>(), SensorType.Throughput))
            .Returns(new List<SensorReading>
            {
                new(HardwareType.Network, "Hyper-V Virtual Ethernet Adapter", SensorType.Throughput, "Download Speed", 2 * 1024 * 1024f),
                new(HardwareType.Network, "VPN Adapter", SensorType.Throughput, "Upload Speed", 1024 * 1024f)
            });

        using var provider = new SystemMetricProvider(
            Array.Empty<IMetricProvider>(),
            logger: null,
            hardwareManager: hardwareManager.Object);

        var usage = await provider.GetSystemUsageAsync();

        usage.DownloadSpeed.Should().Be(0.0);
        usage.UploadSpeed.Should().Be(0.0);
    }

    [Fact]
    public async Task DoneWhen_GetSystemUsageAsync_SkipsPowerProvider_WhenNotSupported()
    {
        var powerProvider = new Mock<IPowerProvider>();
        powerProvider.Setup(p => p.IsSupported()).Returns(false);

        using var provider = new SystemMetricProvider(
            Array.Empty<IMetricProvider>(),
            logger: null,
            hardwareManager: null,
            powerProvider: powerProvider.Object);

        var usage = await provider.GetSystemUsageAsync();

        usage.PowerAvailable.Should().BeFalse();
        usage.TotalPower.Should().Be(0.0);
        usage.MaxPower.Should().Be(0.0);

        powerProvider.Verify(p => p.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DoneWhen_GetSystemUsageAsync_IncludesPower_WhenSupported()
    {
        var powerProvider = new Mock<IPowerProvider>();
        powerProvider.Setup(p => p.IsSupported()).Returns(true);
        powerProvider.Setup(p => p.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PowerStatus(
                CurrentWatts: 42.0,
                LimitWatts: 55.0,
                SchemeIndex: 0,
                Limits: new PowerScheme(55, 100, 55)));

        using var provider = new SystemMetricProvider(
            Array.Empty<IMetricProvider>(),
            logger: null,
            hardwareManager: null,
            powerProvider: powerProvider.Object);

        var usage = await provider.GetSystemUsageAsync();

        usage.PowerAvailable.Should().BeTrue();
        usage.TotalPower.Should().Be(42.0);
        usage.MaxPower.Should().Be(55.0);
        usage.PowerSchemeIndex.Should().Be(0);
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

