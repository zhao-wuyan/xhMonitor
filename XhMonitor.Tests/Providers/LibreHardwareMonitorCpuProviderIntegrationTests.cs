using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Providers;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.Providers;

/// <summary>
/// LibreHardwareMonitorCpuProvider 集成测试
/// Integration tests for LibreHardwareMonitorCpuProvider with real LibreHardwareManager
/// </summary>
public class LibreHardwareMonitorCpuProviderIntegrationTests : IDisposable
{
    private readonly LibreHardwareManager _hardwareManager;
    private readonly Mock<ILogger<LibreHardwareMonitorCpuProvider>> _mockLogger;
    private readonly CpuMetricProvider _cpuMetricProvider;
    private LibreHardwareMonitorCpuProvider? _provider;

    public LibreHardwareMonitorCpuProviderIntegrationTests()
    {
        var mockHardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(mockHardwareLogger.Object);
        _mockLogger = new Mock<ILogger<LibreHardwareMonitorCpuProvider>>();
        _cpuMetricProvider = new CpuMetricProvider();
    }

    [Fact]
    public void MetricId_ShouldReturnCpu()
    {
        // Arrange
        _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act & Assert
        _provider.MetricId.Should().Be("cpu");
    }

    [Fact]
    public void Type_ShouldReturnPercentage()
    {
        // Arrange
        _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act & Assert
        _provider.Type.Should().Be(MetricType.Percentage);
    }

    [Fact]
    public void IsSupported_ShouldMatchHardwareManagerAvailability()
    {
        // Arrange
        var initialized = _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = _provider.IsSupported();

        // Assert
        if (OperatingSystem.IsWindows() && initialized)
        {
            result.Should().Be(_hardwareManager.IsAvailable);
        }
        else
        {
            result.Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnValidCpuUsage_WhenSupported()
    {
        // Arrange
        var initialized = _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        if (!initialized || !_provider.IsSupported())
        {
            // 跳过测试，因为环境不支持
            return;
        }

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().BeGreaterOrEqualTo(0.0);
        result.Should().BeLessOrEqualTo(100.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnZero_WhenNotInitialized()
    {
        // Arrange - 不初始化 HardwareManager
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldBeConsistent_WhenCalledMultipleTimes()
    {
        // Arrange
        var initialized = _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        if (!initialized || !_provider.IsSupported())
        {
            return;
        }

        // Act - 连续调用 3 次
        var result1 = await _provider.GetSystemTotalAsync();
        await Task.Delay(100);
        var result2 = await _provider.GetSystemTotalAsync();
        await Task.Delay(100);
        var result3 = await _provider.GetSystemTotalAsync();

        // Assert - 所有结果都应该在有效范围内
        result1.Should().BeInRange(0.0, 100.0);
        result2.Should().BeInRange(0.0, 100.0);
        result3.Should().BeInRange(0.0, 100.0);
    }

    [Fact]
    public async Task CollectAsync_ShouldReturnValidMetricValue_ForCurrentProcess()
    {
        // Arrange
        var initialized = _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        if (!initialized || !_provider.IsSupported())
        {
            return;
        }

        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Warmup CpuMetricProvider
        await _cpuMetricProvider.WarmupAsync();
        await Task.Delay(1000); // 等待第一次采样

        // Act
        var result = await _provider.CollectAsync(currentProcessId);

        // Assert
        result.Should().NotBeNull();
        result.Unit.Should().Be("%");
        result.DisplayName.Should().Be("CPU Usage");

        // 如果不是错误，值应该在有效范围内
        if (!result.IsError)
        {
            result.Value.Should().BeGreaterOrEqualTo(0.0);
            result.Value.Should().BeLessOrEqualTo(100.0);
        }
    }

    [Fact]
    public async Task CollectAsync_ShouldReturnError_ForInvalidProcessId()
    {
        // Arrange
        var initialized = _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        if (!initialized || !_provider.IsSupported())
        {
            return;
        }

        var invalidProcessId = -1;

        // Act
        var result = await _provider.CollectAsync(invalidProcessId);

        // Assert
        result.Should().NotBeNull();
        // 无效进程 ID 应该返回错误或找不到进程
        (result.IsError || result.ErrorMessage == "Process not found").Should().BeTrue();
    }

    [Fact]
    public async Task HybridArchitecture_ShouldWork_SystemAndProcessMetrics()
    {
        // Arrange
        var initialized = _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        if (!initialized || !_provider.IsSupported())
        {
            return;
        }

        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Warmup
        await _cpuMetricProvider.WarmupAsync();
        await Task.Delay(1000);

        // Act - 同时获取系统级和进程级指标
        var systemCpu = await _provider.GetSystemTotalAsync();
        var processCpu = await _provider.CollectAsync(currentProcessId);

        // Assert
        systemCpu.Should().BeInRange(0.0, 100.0, "系统 CPU 使用率应该在 0-100 范围内");

        if (!processCpu.IsError)
        {
            processCpu.Value.Should().BeInRange(0.0, 100.0, "进程 CPU 使用率应该在 0-100 范围内");
            processCpu.Value.Should().BeLessOrEqualTo(systemCpu, "进程 CPU 使用率不应超过系统总使用率");
        }
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldBeThreadSafe()
    {
        // Arrange
        var initialized = _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        if (!initialized || !_provider.IsSupported())
        {
            return;
        }

        var exceptions = new List<Exception>();
        var results = new List<double>();
        var tasks = new List<Task>();

        // Act - 50 次并发调用
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await _provider.GetSystemTotalAsync();
                    lock (results)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        exceptions.Should().BeEmpty("不应有任何线程安全异常");
        results.Should().HaveCount(50, "所有调用都应完成");
        results.Should().OnlyContain(r => r >= 0.0 && r <= 100.0, "所有结果都应在有效范围内");
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        // Arrange
        _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        Action act = () => _provider.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        // Arrange
        _hardwareManager.Initialize();
        _provider = new LibreHardwareMonitorCpuProvider(
            _hardwareManager,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        _provider.Dispose();
        Action act = () => _provider.Dispose();

        // Assert
        act.Should().NotThrow("多次调用 Dispose 不应抛出异常");
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _cpuMetricProvider?.Dispose();
        _hardwareManager?.Dispose();
    }
}
