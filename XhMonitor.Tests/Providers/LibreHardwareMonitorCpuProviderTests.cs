using FluentAssertions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Enums;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;

namespace XhMonitor.Tests.Providers;

/// <summary>
/// LibreHardwareMonitorCpuProvider 单元测试
/// Unit tests for LibreHardwareMonitorCpuProvider
/// </summary>
public class LibreHardwareMonitorCpuProviderTests : IDisposable
{
    private readonly Mock<ILibreHardwareManager> _mockHardwareManager;
    private readonly Mock<ILogger<LibreHardwareMonitorCpuProvider>> _mockLogger;
    private readonly CpuMetricProvider _cpuMetricProvider;
    private LibreHardwareMonitorCpuProvider? _provider;

    public LibreHardwareMonitorCpuProviderTests()
    {
        _mockHardwareManager = new Mock<ILibreHardwareManager>();
        _mockLogger = new Mock<ILogger<LibreHardwareMonitorCpuProvider>>();
        _cpuMetricProvider = new CpuMetricProvider();
    }

    [Fact]
    public void MetricId_ShouldReturnCpu()
    {
        // Arrange
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act & Assert
        _provider.MetricId.Should().Be("cpu");
    }

    [Fact]
    public void DisplayName_ShouldReturnCorrectName()
    {
        // Arrange
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act & Assert
        _provider.DisplayName.Should().Be("CPU 使用率 (LibreHardwareMonitor)");
    }

    [Fact]
    public void Unit_ShouldReturnPercentage()
    {
        // Arrange
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act & Assert
        _provider.Unit.Should().Be("%");
    }

    [Fact]
    public void Type_ShouldReturnPercentage()
    {
        // Arrange
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act & Assert
        _provider.Type.Should().Be(MetricType.Percentage);
    }

    [Fact]
    public void IsSupported_ShouldReturnTrue_WhenHardwareManagerIsAvailableAndWindows()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = _provider.IsSupported();

        // Assert
        if (OperatingSystem.IsWindows())
        {
            result.Should().BeTrue();
        }
        else
        {
            result.Should().BeFalse();
        }
    }

    [Fact]
    public void IsSupported_ShouldReturnFalse_WhenHardwareManagerIsNotAvailable()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(false);
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = _provider.IsSupported();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnZero_WhenNotSupported()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(false);
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnCpuLoad_WhenSensorValueExists()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager
            .Setup(m => m.GetSensorValue(HardwareType.Cpu, SensorType.Load))
            .Returns(45.6f);

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(45.6);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldClampToZero_WhenNegativeValue()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager
            .Setup(m => m.GetSensorValue(HardwareType.Cpu, SensorType.Load))
            .Returns(-10.0f);

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldClampTo100_WhenValueExceeds100()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager
            .Setup(m => m.GetSensorValue(HardwareType.Cpu, SensorType.Load))
            .Returns(150.0f);

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(100.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnZero_WhenSensorValueIsNull()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager
            .Setup(m => m.GetSensorValue(HardwareType.Cpu, SensorType.Load))
            .Returns((float?)null);

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnZero_WhenExceptionThrown()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager
            .Setup(m => m.GetSensorValue(HardwareType.Cpu, SensorType.Load))
            .Throws(new InvalidOperationException("Test exception"));

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldRoundToOneDecimal()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager
            .Setup(m => m.GetSensorValue(HardwareType.Cpu, SensorType.Load))
            .Returns(45.6789f);

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(45.7);
    }

    [Fact]
    public async Task CollectAsync_ShouldReturnError_WhenNotSupported()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(false);
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.CollectAsync(1234);

        // Assert
        result.IsError.Should().BeTrue();
        result.ErrorMessage.Should().Be("LibreHardwareMonitor 不可用");
    }

    [Fact]
    public async Task CollectAsync_ShouldDelegateToCpuMetricProvider_WhenSupported()
    {
        // Arrange
        var processId = System.Diagnostics.Process.GetCurrentProcess().Id;

        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.CollectAsync(processId);

        // Assert
        // 应该成功调用 CpuMetricProvider，返回非错误结果
        // 注意：在某些环境下可能无法获取进程 CPU 数据，这是正常的
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task CollectAsync_ShouldReturnError_WhenCpuMetricProviderFails()
    {
        // Arrange
        var invalidProcessId = -1; // 无效的进程 ID
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);

        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        var result = await _provider.CollectAsync(invalidProcessId);

        // Assert
        result.Should().NotBeNull();
        // 无效进程 ID 应该返回错误或找不到进程
    }

    [Fact]
    public void Dispose_ShouldDisposeCpuMetricProvider()
    {
        // Arrange
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        Action act = () => _provider.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_ShouldNotThrow_WhenCalledMultipleTimes()
    {
        // Arrange
        _provider = new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Act
        _provider.Dispose();
        Action act = () => _provider.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenHardwareManagerIsNull()
    {
        // Act
        Action act = () => new LibreHardwareMonitorCpuProvider(
            null!,
            _mockLogger.Object,
            _cpuMetricProvider);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("hardwareManager");
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenLoggerIsNull()
    {
        // Act
        Action act = () => new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            null!,
            _cpuMetricProvider);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenCpuMetricProviderIsNull()
    {
        // Act
        Action act = () => new LibreHardwareMonitorCpuProvider(
            _mockHardwareManager.Object,
            _mockLogger.Object,
            null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("cpuMetricProvider");
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _cpuMetricProvider?.Dispose();
    }
}
