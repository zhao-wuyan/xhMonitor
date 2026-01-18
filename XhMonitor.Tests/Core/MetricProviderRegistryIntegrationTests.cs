using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Services;
using XhMonitor.Service.Core;

namespace XhMonitor.Tests.Core;

/// <summary>
/// MetricProviderRegistry 集成测试
/// Integration tests for MetricProviderRegistry with LibreHardwareMonitor
/// </summary>
public class MetricProviderRegistryIntegrationTests : IDisposable
{
    private readonly Mock<ILogger<MetricProviderRegistry>> _mockLogger;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private readonly Mock<ILibreHardwareManager> _mockHardwareManager;
    private MetricProviderRegistry? _registry;

    public MetricProviderRegistryIntegrationTests()
    {
        _mockLogger = new Mock<ILogger<MetricProviderRegistry>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockHardwareManager = new Mock<ILibreHardwareManager>();

        // 配置 LoggerFactory 返回 Mock Logger
        _mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns((string categoryName) => new Mock<ILogger>().Object);
    }

    [Fact]
    public void DoneWhen_WithAdminPrivileges_RegistersLibreHardwareMonitorProviders()
    {
        // Verify: 在有管理员权限环境下，混合架构提供者被注册

        // Arrange
        _mockHardwareManager.Setup(x => x.Initialize()).Returns(true);
        _mockHardwareManager.Setup(x => x.IsAvailable).Returns(true);

        // Act
        _registry = new MetricProviderRegistry(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _mockHardwareManager.Object,
            preferLibreHardwareMonitor: true);

        // Assert
        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");
        var gpuProvider = _registry.GetProvider("gpu");
        var vramProvider = _registry.GetProvider("vram");

        cpuProvider.Should().NotBeNull("CPU 提供者应被注册");
        memoryProvider.Should().NotBeNull("Memory 提供者应被注册");
        gpuProvider.Should().NotBeNull("GPU 提供者应被注册");
        vramProvider.Should().NotBeNull("VRAM 提供者应被注册");

        // 验证使用了 LibreHardwareMonitor 混合架构提供者
        cpuProvider!.GetType().Name.Should().Be("LibreHardwareMonitorCpuProvider");
        memoryProvider!.GetType().Name.Should().Be("LibreHardwareMonitorMemoryProvider");
        gpuProvider!.GetType().Name.Should().Be("LibreHardwareMonitorGpuProvider");
        vramProvider!.GetType().Name.Should().Be("LibreHardwareMonitorVramProvider");

        // 验证日志记录
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("使用 LibreHardwareMonitor 混合架构提供者")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void DoneWhen_WithoutAdminPrivileges_RegistersLegacyProviders()
    {
        // Verify: 在无管理员权限环境下，现有提供者被注册

        // Arrange
        _mockHardwareManager.Setup(x => x.Initialize()).Returns(false);
        _mockHardwareManager.Setup(x => x.IsAvailable).Returns(false);

        // Act
        _registry = new MetricProviderRegistry(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _mockHardwareManager.Object,
            preferLibreHardwareMonitor: true);

        // Assert
        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");
        var gpuProvider = _registry.GetProvider("gpu");
        var vramProvider = _registry.GetProvider("vram");

        cpuProvider.Should().NotBeNull("CPU 提供者应被注册");
        memoryProvider.Should().NotBeNull("Memory 提供者应被注册");
        gpuProvider.Should().NotBeNull("GPU 提供者应被注册");
        vramProvider.Should().NotBeNull("VRAM 提供者应被注册");

        // 验证使用了传统 PerformanceCounter 提供者
        cpuProvider!.GetType().Name.Should().Be("CpuMetricProvider");
        memoryProvider!.GetType().Name.Should().Be("MemoryMetricProvider");
        gpuProvider!.GetType().Name.Should().Be("GpuMetricProvider");
        vramProvider!.GetType().Name.Should().Be("VramMetricProvider");

        // 验证日志记录
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("LibreHardwareManager 初始化失败")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("已注册传统 PerformanceCounter 提供者")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void DoneWhen_ConfigDisabled_RegistersLegacyProviders()
    {
        // Verify: 配置 PreferLibreHardwareMonitor=false 时强制使用现有提供者

        // Arrange
        _mockHardwareManager.Setup(x => x.Initialize()).Returns(true);
        _mockHardwareManager.Setup(x => x.IsAvailable).Returns(true);

        // Act
        _registry = new MetricProviderRegistry(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _mockHardwareManager.Object,
            preferLibreHardwareMonitor: false);

        // Assert
        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");
        var gpuProvider = _registry.GetProvider("gpu");
        var vramProvider = _registry.GetProvider("vram");

        // 验证使用了传统 PerformanceCounter 提供者
        cpuProvider!.GetType().Name.Should().Be("CpuMetricProvider");
        memoryProvider!.GetType().Name.Should().Be("MemoryMetricProvider");
        gpuProvider!.GetType().Name.Should().Be("GpuMetricProvider");
        vramProvider!.GetType().Name.Should().Be("VramMetricProvider");

        // 验证日志记录
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("配置禁用 LibreHardwareMonitor")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        // 验证 Initialize 未被调用
        _mockHardwareManager.Verify(x => x.Initialize(), Times.Never);
    }

    [Fact]
    public void DoneWhen_SystemMetricProvider_ReceivesRegisteredProviders()
    {
        // Verify: SystemMetricProvider 正确接收到注册的提供者实例

        // Arrange
        _mockHardwareManager.Setup(x => x.Initialize()).Returns(true);
        _mockHardwareManager.Setup(x => x.IsAvailable).Returns(true);

        _registry = new MetricProviderRegistry(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _mockHardwareManager.Object,
            preferLibreHardwareMonitor: true);

        // Act
        var cpuProvider = _registry.GetProvider("cpu");
        var gpuProvider = _registry.GetProvider("gpu");
        var memoryProvider = _registry.GetProvider("memory");
        var vramProvider = _registry.GetProvider("vram");

        // Assert
        cpuProvider.Should().NotBeNull();
        gpuProvider.Should().NotBeNull();
        memoryProvider.Should().NotBeNull();
        vramProvider.Should().NotBeNull();

        // 验证提供者可以被 SystemMetricProvider 使用
        cpuProvider!.MetricId.Should().Be("cpu");
        gpuProvider!.MetricId.Should().Be("gpu");
        memoryProvider!.MetricId.Should().Be("memory");
        vramProvider!.MetricId.Should().Be("vram");
    }

    [Fact]
    public async Task DoneWhen_ProcessLevelMonitoring_WorksInAllScenarios()
    {
        // Verify: 进程级监控功能在所有场景下正常工作，无功能退化

        // Arrange - 使用真实的 LibreHardwareManager 而不是 Mock
        var realHardwareManager = new LibreHardwareManager();
        realHardwareManager.Initialize();

        _registry = new MetricProviderRegistry(
            _mockLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            realHardwareManager,
            preferLibreHardwareMonitor: true);

        var cpuProvider = _registry.GetProvider("cpu");
        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Act
        var result = await cpuProvider!.CollectAsync(currentProcessId);

        // Assert
        result.Should().NotBeNull();

        // 进程级监控应该正常工作（无论是使用 LibreHardwareMonitor 还是 PerformanceCounter）
        // 如果返回错误，应该是合理的错误（如进程不存在），而不是 LibreHardwareMonitor 不可用
        if (result.IsError)
        {
            // 允许的错误：进程不存在、访问被拒绝等
            var allowedErrors = new[] { "Process not found", "Access is denied", "进程", "访问" };
            allowedErrors.Should().Contain(e => result.ErrorMessage!.Contains(e),
                $"错误消息应该是合理的进程级错误，而不是 LibreHardwareMonitor 不可用。实际错误: {result.ErrorMessage}");
        }
        else
        {
            // 如果成功，验证返回值合理
            result.Value.Should().BeGreaterThanOrEqualTo(0, "CPU 使用率应为非负数");
        }

        // Cleanup
        realHardwareManager.Dispose();
    }

    public void Dispose()
    {
        _registry?.Dispose();
    }
}
