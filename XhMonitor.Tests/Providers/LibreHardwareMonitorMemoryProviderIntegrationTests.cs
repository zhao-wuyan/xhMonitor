using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Providers;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.Providers;

/// <summary>
/// LibreHardwareMonitorMemoryProvider 集成测试
/// Integration tests for LibreHardwareMonitorMemoryProvider
/// </summary>
public class LibreHardwareMonitorMemoryProviderIntegrationTests : IDisposable
{
    private readonly Mock<ILogger<LibreHardwareManager>> _mockHardwareLogger;
    private readonly Mock<ILogger<LibreHardwareMonitorMemoryProvider>> _mockProviderLogger;
    private LibreHardwareManager? _hardwareManager;
    private MemoryMetricProvider? _memoryMetricProvider;
    private LibreHardwareMonitorMemoryProvider? _provider;

    public LibreHardwareMonitorMemoryProviderIntegrationTests()
    {
        _mockHardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _mockProviderLogger = new Mock<ILogger<LibreHardwareMonitorMemoryProvider>>();
    }

    [Fact]
    public async Task DoneWhen_GetSystemTotalAsync_ReturnsMemoryUsageInRange0To100()
    {
        // Verify: GetSystemTotalAsync() 返回 0-100 范围内的内存使用率（来自 LibreHardwareMonitor）

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        var initialized = _hardwareManager.Initialize();

        if (!initialized)
        {
            // 环境不支持 LibreHardwareMonitor，跳过测试
            return;
        }

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().BeInRange(0.0, 100.0, "内存使用率应在 0-100% 范围内");
        result.Should().BeGreaterThanOrEqualTo(0.0, "内存使用率不应为负数");
    }

    [Fact]
    public async Task DoneWhen_CollectAsync_ReturnsProcessMemoryUsage()
    {
        // Verify: CollectAsync(processId) 正确返回进程级内存使用量（委托给 MemoryMetricProvider）

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        _hardwareManager.Initialize();

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);
        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Act
        var result = await _provider.CollectAsync(currentProcessId);

        // Assert
        result.Should().NotBeNull("应返回有效的 MetricValue");
        result.IsError.Should().BeFalse("不应返回错误状态");
        result.Value.Should().BeGreaterThan(0, "当前进程应有内存使用");
        result.Unit.Should().Be("MB", "单位应为 MB（来自 MemoryMetricProvider）");
        result.DisplayName.Should().Be("Memory Usage", "显示名称应来自 MemoryMetricProvider");
    }

    [Fact]
    public async Task DoneWhen_ProcessLevelMonitoring_MatchesExistingImplementation()
    {
        // Verify: 进程级监控功能与现有实现完全一致，无功能退化

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        _hardwareManager.Initialize();

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);
        var originalProvider = new MemoryMetricProvider();
        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Act
        var hybridResult = await _provider.CollectAsync(currentProcessId);
        var originalResult = await originalProvider.CollectAsync(currentProcessId);

        // Assert
        hybridResult.IsError.Should().Be(originalResult.IsError, "错误状态应一致");
        hybridResult.Unit.Should().Be(originalResult.Unit, "单位应一致");
        hybridResult.DisplayName.Should().Be(originalResult.DisplayName, "显示名称应一致");

        if (!hybridResult.IsError && !originalResult.IsError)
        {
            // 允许小幅差异（进程内存可能在两次调用间变化）
            Math.Abs(hybridResult.Value - originalResult.Value).Should().BeLessThan(50, "内存值应基本一致（允许 50MB 差异）");
        }

        originalProvider.Dispose();
    }

    [Fact]
    public void DoneWhen_IsSupported_ReturnsFalse_WhenLibreHardwareManagerNotAvailable()
    {
        // Verify: 在 LibreHardwareManager 不可用时 IsSupported() 返回 false

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        // 不调用 Initialize()，保持 IsAvailable=false

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);

        // Act
        var result = _provider.IsSupported();

        // Assert
        result.Should().BeFalse("LibreHardwareManager 未初始化时应返回 false");
    }

    [Fact]
    public async Task DoneWhen_GetSystemTotalAsync_ReturnsZeroAndLogsError_WhenSensorReadFails()
    {
        // Verify: 传感器读取失败时返回 0.0 且记录日志

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        // 不初始化，模拟传感器不可用

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);

        // Act
        var result = await _provider.GetSystemTotalAsync();

        // Assert
        result.Should().Be(0.0, "传感器读取失败时应返回 0.0");

        // 验证日志记录（至少应有警告日志）
        _mockProviderLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("LibreHardwareManager is not available")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce,
            "应记录警告日志");
    }

    [Fact]
    public async Task DoneWhen_CollectAsync_HandlesInvalidProcessId()
    {
        // Verify: CollectAsync 正确处理无效进程 ID（委托给 MemoryMetricProvider）

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        _hardwareManager.Initialize();

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);
        var invalidProcessId = 999999;

        // Act
        var result = await _provider.CollectAsync(invalidProcessId);

        // Assert
        result.Should().NotBeNull("应返回有效的 MetricValue");
        result.IsError.Should().BeTrue("无效进程 ID 应返回错误状态");
        result.ErrorMessage.Should().NotBeNullOrEmpty("应包含错误消息");
    }

    [Fact]
    public async Task DoneWhen_HybridArchitecture_SystemLevelUsesLibreHardware_ProcessLevelUsesPerformanceCounter()
    {
        // Verify: 混合架构验证 - 系统级使用 LibreHardwareMonitor，进程级使用 PerformanceCounter

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        var initialized = _hardwareManager.Initialize();

        if (!initialized)
        {
            // 环境不支持，跳过测试
            return;
        }

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);
        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Act
        var systemTotal = await _provider.GetSystemTotalAsync();
        var processMetric = await _provider.CollectAsync(currentProcessId);

        // Assert - 系统级指标
        systemTotal.Should().BeInRange(0.0, 100.0, "系统级内存使用率应在 0-100% 范围内");
        _provider.Unit.Should().Be("%", "系统级单位应为百分比");
        _provider.DisplayName.Should().Contain("LibreHardwareMonitor", "显示名称应标识使用 LibreHardwareMonitor");

        // Assert - 进程级指标
        processMetric.Should().NotBeNull("进程级指标应有效");
        processMetric.IsError.Should().BeFalse("进程级指标不应出错");
        processMetric.Unit.Should().Be("MB", "进程级单位应为 MB（来自 MemoryMetricProvider）");
        processMetric.Value.Should().BeGreaterThan(0, "进程应有内存使用");
    }

    [Fact]
    public void DoneWhen_Dispose_ReleasesResources()
    {
        // Verify: Dispose() 正确释放资源

        // Arrange
        _hardwareManager = new LibreHardwareManager(_mockHardwareLogger.Object);
        _memoryMetricProvider = new MemoryMetricProvider();
        _hardwareManager.Initialize();

        _provider = new LibreHardwareMonitorMemoryProvider(_hardwareManager, _memoryMetricProvider, _mockProviderLogger.Object);

        // Act
        Action act = () => _provider.Dispose();

        // Assert
        act.Should().NotThrow("Dispose 不应抛出异常");

        // 验证多次 Dispose 不会出错
        Action secondDispose = () => _provider.Dispose();
        secondDispose.Should().NotThrow("多次 Dispose 不应抛出异常");
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _memoryMetricProvider?.Dispose();
        _hardwareManager?.Dispose();
    }
}
