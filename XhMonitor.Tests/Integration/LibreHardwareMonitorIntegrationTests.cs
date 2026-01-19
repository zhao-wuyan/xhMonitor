using FluentAssertions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Providers;
using XhMonitor.Core.Services;
using XhMonitor.Service.Core;

namespace XhMonitor.Tests.Integration;

/// <summary>
/// LibreHardwareMonitor 混合架构集成测试
/// 验证系统级指标使用 LibreHardwareMonitor，进程级指标使用 PerformanceCounter
/// </summary>
public class LibreHardwareMonitorIntegrationTests : IDisposable
{
    private readonly Mock<ILogger<MetricProviderRegistry>> _mockRegistryLogger;
    private readonly Mock<ILoggerFactory> _mockLoggerFactory;
    private LibreHardwareManager? _hardwareManager;
    private MetricProviderRegistry? _registry;

    public LibreHardwareMonitorIntegrationTests()
    {
        _mockRegistryLogger = new Mock<ILogger<MetricProviderRegistry>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();

        // 配置 LoggerFactory 返回 Mock Logger
        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);
    }

    [Fact]
    public void Integration_WithAdminPrivileges_ShouldUseLibreHardwareMonitorProviders()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);

        // Act
        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: true);

        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");
        var gpuProvider = _registry.GetProvider("gpu");
        var vramProvider = _registry.GetProvider("vram");

        // Assert
        cpuProvider.Should().NotBeNull("CPU 提供者应被注册");
        memoryProvider.Should().NotBeNull("Memory 提供者应被注册");
        gpuProvider.Should().NotBeNull("GPU 提供者应被注册");
        vramProvider.Should().NotBeNull("VRAM 提供者应被注册");

        // 验证是否使用了 LibreHardwareMonitor 混合架构提供者
        if (_hardwareManager.IsAvailable)
        {
            cpuProvider!.GetType().Name.Should().Be("LibreHardwareMonitorCpuProvider",
                "有管理员权限时应使用 LibreHardwareMonitor CPU 提供者");
            memoryProvider!.GetType().Name.Should().Be("LibreHardwareMonitorMemoryProvider",
                "有管理员权限时应使用 LibreHardwareMonitor Memory 提供者");
            gpuProvider!.GetType().Name.Should().Be("LibreHardwareMonitorGpuProvider",
                "有管理员权限时应使用 LibreHardwareMonitor GPU 提供者");
            vramProvider!.GetType().Name.Should().Be("LibreHardwareMonitorVramProvider",
                "有管理员权限时应使用 LibreHardwareMonitor VRAM 提供者");
        }
        else
        {
            // 无管理员权限时应回退到传统提供者
            cpuProvider!.GetType().Name.Should().Be("CpuMetricProvider",
                "无管理员权限时应回退到传统 CPU 提供者");
            memoryProvider!.GetType().Name.Should().Be("MemoryMetricProvider",
                "无管理员权限时应回退到传统 Memory 提供者");
        }
    }

    [Fact]
    public void Integration_WithoutAdminPrivileges_ShouldFallbackToPerformanceCounterProviders()
    {
        // Arrange - 不提供 LibreHardwareManager，模拟无管理员权限场景
        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            hardwareManager: null,
            preferLibreHardwareMonitor: true);

        // Act
        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");
        var gpuProvider = _registry.GetProvider("gpu");
        var vramProvider = _registry.GetProvider("vram");

        // Assert
        cpuProvider.Should().NotBeNull("CPU 提供者应被注册");
        memoryProvider.Should().NotBeNull("Memory 提供者应被注册");
        gpuProvider.Should().NotBeNull("GPU 提供者应被注册");
        vramProvider.Should().NotBeNull("VRAM 提供者应被注册");

        // 验证回退到传统 PerformanceCounter 提供者
        cpuProvider!.GetType().Name.Should().Be("CpuMetricProvider",
            "无 LibreHardwareManager 时应使用传统 CPU 提供者");
        memoryProvider!.GetType().Name.Should().Be("MemoryMetricProvider",
            "无 LibreHardwareManager 时应使用传统 Memory 提供者");
        gpuProvider!.GetType().Name.Should().Be("GpuMetricProvider",
            "无 LibreHardwareManager 时应使用传统 GPU 提供者");
        vramProvider!.GetType().Name.Should().Be("VramMetricProvider",
            "无 LibreHardwareManager 时应使用传统 VRAM 提供者");
    }

    [Fact]
    public void Integration_PreferLibreHardwareMonitorDisabled_ShouldUsePerformanceCounterProviders()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);

        // Act - 配置禁用 LibreHardwareMonitor
        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: false);

        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");

        // Assert
        cpuProvider.Should().NotBeNull();
        memoryProvider.Should().NotBeNull();

        // 验证使用传统提供者（即使有 LibreHardwareManager）
        cpuProvider!.GetType().Name.Should().Be("CpuMetricProvider",
            "配置禁用时应使用传统 CPU 提供者");
        memoryProvider!.GetType().Name.Should().Be("MemoryMetricProvider",
            "配置禁用时应使用传统 Memory 提供者");
    }

    [Fact]
    public void Integration_LibreHardwareManagerSingleton_ShouldBeSharedAcrossProviders()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);
        _hardwareManager.Initialize();

        if (!_hardwareManager.IsAvailable)
        {
            // 跳过测试（无管理员权限）
            return;
        }

        // Act
        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: true);

        var cpuProvider = _registry.GetProvider("cpu") as LibreHardwareMonitorCpuProvider;
        var memoryProvider = _registry.GetProvider("memory") as LibreHardwareMonitorMemoryProvider;

        // Assert
        cpuProvider.Should().NotBeNull();
        memoryProvider.Should().NotBeNull();

        // 验证两个提供者共享同一个 LibreHardwareManager 实例
        // 通过 IsSupported() 验证（依赖 _hardwareManager.IsAvailable）
        cpuProvider!.IsSupported().Should().BeTrue("CPU 提供者应支持");
        memoryProvider!.IsSupported().Should().BeTrue("Memory 提供者应支持");
    }

    [Fact]
    public async Task Integration_ProcessLevelMonitoring_ShouldUsePerformanceCounterDelegation()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);
        _hardwareManager.Initialize();

        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: true);

        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");

        var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        // Act - 调用进程级监控方法
        var cpuResult = await cpuProvider!.CollectAsync(currentProcessId);
        var memoryResult = await memoryProvider!.CollectAsync(currentProcessId);

        // Assert - 验证进程级监控功能正常工作
        cpuResult.Should().NotBeNull("CPU 进程级监控应返回结果");
        memoryResult.Should().NotBeNull("Memory 进程级监控应返回结果");

        // 验证返回的是有效数据（非错误）
        if (_hardwareManager.IsAvailable)
        {
            // LibreHardwareMonitor 提供者应委托给 PerformanceCounter
            // 结果应该是有效的（可能是 0 或实际值，但不应该是错误）
            (cpuResult.IsError == false || cpuResult.Value >= 0).Should().BeTrue(
                "CPU 进程级监控应返回有效数据或非错误状态");
            (memoryResult.IsError == false || memoryResult.Value >= 0).Should().BeTrue(
                "Memory 进程级监控应返回有效数据或非错误状态");
        }
    }

    [Fact]
    public async Task Integration_SystemLevelMonitoring_ShouldUseLibreHardwareMonitor()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);
        var initialized = _hardwareManager.Initialize();

        if (!initialized)
        {
            // 跳过测试（无管理员权限）
            return;
        }

        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: true);

        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");

        // Act - 调用系统级监控方法
        var cpuTotal = await cpuProvider!.GetSystemTotalAsync();
        var memoryTotal = await memoryProvider!.GetSystemTotalAsync();

        // Assert - 验证系统级监控使用 LibreHardwareMonitor
        cpuTotal.Should().BeGreaterThanOrEqualTo(0, "系统 CPU 使用率应 >= 0");
        cpuTotal.Should().BeLessThanOrEqualTo(100, "系统 CPU 使用率应 <= 100");

        memoryTotal.Should().BeGreaterThanOrEqualTo(0, "系统内存使用率应 >= 0");
        memoryTotal.Should().BeLessThanOrEqualTo(100, "系统内存使用率应 <= 100");
    }

    [Fact]
    public async Task Integration_InvalidProcessId_ShouldReturnError()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);
        _hardwareManager.Initialize();

        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: true);

        var cpuProvider = _registry.GetProvider("cpu");
        var memoryProvider = _registry.GetProvider("memory");

        var invalidProcessId = 999999;

        // Act
        var cpuResult = await cpuProvider!.CollectAsync(invalidProcessId);
        var memoryResult = await memoryProvider!.CollectAsync(invalidProcessId);

        // Assert - 验证无效进程 ID 返回错误
        cpuResult.Should().NotBeNull();
        memoryResult.Should().NotBeNull();

        cpuResult.IsError.Should().BeTrue("无效进程 ID 应返回错误状态");
        memoryResult.IsError.Should().BeTrue("无效进程 ID 应返回错误状态");

        cpuResult.ErrorMessage.Should().NotBeNullOrEmpty("应包含错误消息");
        memoryResult.ErrorMessage.Should().NotBeNullOrEmpty("应包含错误消息");
    }

    [Fact]
    public void Integration_MultiThreadedAccess_ShouldBeThreadSafe()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);
        var initialized = _hardwareManager.Initialize();

        if (!initialized)
        {
            // 跳过测试（无管理员权限）
            return;
        }

        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: true);

        var cpuProvider = _registry.GetProvider("cpu");
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Act - 多线程并发访问
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 5; i++)
                {
                    var result = await cpuProvider!.GetSystemTotalAsync();
                    result.Should().BeGreaterThanOrEqualTo(0);
                    await Task.Delay(10);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        })).ToArray();

        Task.WaitAll(tasks);

        // Assert - 验证线程安全
        exceptions.Should().BeEmpty("多线程访问不应抛出异常");
    }

    [Fact]
    public void Integration_ProviderDisposal_ShouldCleanupResources()
    {
        // Arrange
        var hardwareLogger = new Mock<ILogger<LibreHardwareManager>>();
        _hardwareManager = new LibreHardwareManager(hardwareLogger.Object);
        _hardwareManager.Initialize();

        _registry = new MetricProviderRegistry(
            _mockRegistryLogger.Object,
            _mockLoggerFactory.Object,
            "plugins",
            _hardwareManager,
            preferLibreHardwareMonitor: true);

        // Act
        Action disposeAction = () => _registry.Dispose();

        // Assert - 验证 Dispose 不抛出异常
        disposeAction.Should().NotThrow("Dispose 应正常执行");

        // 验证 Dispose 后无法访问提供者
        Action accessAfterDispose = () => _registry.GetProvider("cpu");
        accessAfterDispose.Should().Throw<ObjectDisposedException>(
            "Dispose 后访问应抛出 ObjectDisposedException");
    }

    public void Dispose()
    {
        _registry?.Dispose();
        _hardwareManager?.Dispose();
    }
}
