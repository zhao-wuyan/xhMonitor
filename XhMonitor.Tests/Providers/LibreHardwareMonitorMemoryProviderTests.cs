using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;
using LibreHardwareMonitor.Hardware;

namespace XhMonitor.Tests.Providers;

/// <summary>
/// LibreHardwareMonitorMemoryProvider 单元测试
/// Unit tests for LibreHardwareMonitorMemoryProvider
/// </summary>
public class LibreHardwareMonitorMemoryProviderTests : IDisposable
{
    private readonly Mock<ILibreHardwareManager> _mockHardwareManager;
    private readonly Mock<ILogger<LibreHardwareMonitorMemoryProvider>> _mockLogger;
    private readonly MemoryMetricProvider _memoryMetricProvider;
    private LibreHardwareMonitorMemoryProvider? _provider;

    public LibreHardwareMonitorMemoryProviderTests()
    {
        _mockHardwareManager = new Mock<ILibreHardwareManager>();
        _mockLogger = new Mock<ILogger<LibreHardwareMonitorMemoryProvider>>();
        _memoryMetricProvider = new MemoryMetricProvider();
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenHardwareManagerIsNull()
    {
        // Act
        Action act = () => new LibreHardwareMonitorMemoryProvider(null!, _memoryMetricProvider, _mockLogger.Object);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("hardwareManager");
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenMemoryMetricProviderIsNull()
    {
        // Act
        Action act = () => new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, null!, _mockLogger.Object);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("memoryMetricProvider");
    }

    [Fact]
    public void MetricId_ShouldReturnMemory()
    {
        // Arrange
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act & Assert
        _provider.MetricId.Should().Be("memory");
    }

    [Fact]
    public void DisplayName_ShouldReturnCorrectName()
    {
        // Arrange
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act & Assert
        _provider.DisplayName.Should().Be("内存使用率 (LibreHardwareMonitor)");
    }

    [Fact]
    public void Unit_ShouldReturnPercentage()
    {
        // Arrange
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act & Assert
        _provider.Unit.Should().Be("%");
    }

    [Fact]
    public void IsSupported_ShouldReturnTrue_WhenHardwareManagerIsAvailable()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act
        var result = _provider.IsSupported();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsSupported_ShouldReturnFalse_WhenHardwareManagerIsNotAvailable()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(false);
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

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
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnMemoryLoad_WhenSensorValueExists()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager.Setup(m => m.GetSensorValue(HardwareType.Memory, SensorType.Load))
            .Returns(75.5f);
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(75.5);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnZero_WhenSensorValueIsNull()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager.Setup(m => m.GetSensorValue(HardwareType.Memory, SensorType.Load))
            .Returns((float?)null);
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

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
        _mockHardwareManager.Setup(m => m.GetSensorValue(HardwareType.Memory, SensorType.Load))
            .Returns(75.567f);
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(75.6);
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnZero_WhenExceptionOccurs()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager.Setup(m => m.GetSensorValue(HardwareType.Memory, SensorType.Load))
            .Throws(new InvalidOperationException("Test exception"));
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0);
    }

    [Fact]
    public async Task CollectAsync_ShouldDelegateToMemoryMetricProvider()
    {
        // Arrange
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);
        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Act
        var result = await _provider.CollectAsync(currentProcessId);

        // Assert
        result.Should().NotBeNull();
        result.IsError.Should().BeFalse();
        result.Value.Should().BeGreaterThan(0);
        result.Unit.Should().Be("MB");
    }

    [Fact]
    public async Task CollectAsync_ShouldReturnError_WhenProcessNotFound()
    {
        // Arrange
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);
        var invalidProcessId = 999999;

        // Act
        var result = await _provider.CollectAsync(invalidProcessId);

        // Assert
        result.Should().NotBeNull();
        result.IsError.Should().BeTrue();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Dispose_ShouldDisposeMemoryMetricProvider()
    {
        // Arrange
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act
        Action act = () => _provider.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public async Task GetSystemTotalAsync_ShouldReturnValueInRange_0To100()
    {
        // Arrange
        _mockHardwareManager.Setup(m => m.IsAvailable).Returns(true);
        _mockHardwareManager.Setup(m => m.GetSensorValue(HardwareType.Memory, SensorType.Load))
            .Returns(85.3f);
        _provider = new LibreHardwareMonitorMemoryProvider(_mockHardwareManager.Object, _memoryMetricProvider, _mockLogger.Object);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().BeInRange(0.0, 100.0);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _memoryMetricProvider?.Dispose();
    }
}
