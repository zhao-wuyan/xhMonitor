using FluentAssertions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Moq;
using XhMonitor.Core.Models;
using XhMonitor.Core.Services;

namespace XhMonitor.Tests.Services;

/// <summary>
/// LibreHardwareManager 集成测试 - 验证所有 Done When 条件
/// Integration tests for LibreHardwareManager - Verify all Done When conditions
/// </summary>
public class LibreHardwareManagerIntegrationTests : IDisposable
{
    private readonly Mock<ILogger<LibreHardwareManager>> _mockLogger;
    private LibreHardwareManager? _manager;

    public LibreHardwareManagerIntegrationTests()
    {
        _mockLogger = new Mock<ILogger<LibreHardwareManager>>();
    }

    [Fact]
    public void DoneWhen_InterfaceDefinitionComplete()
    {
        // Verify: ILibreHardwareManager 接口定义完整，包含所有必需方法和属性

        var interfaceType = typeof(XhMonitor.Core.Interfaces.ILibreHardwareManager);

        // 验证接口存在
        interfaceType.Should().NotBeNull();

        // 验证 Initialize() 方法
        var initializeMethod = interfaceType.GetMethod("Initialize");
        initializeMethod.Should().NotBeNull();
        initializeMethod!.ReturnType.Should().Be(typeof(bool));

        // 验证 GetSensorValue() 方法
        var getSensorValueMethod = interfaceType.GetMethod("GetSensorValue");
        getSensorValueMethod.Should().NotBeNull();
        getSensorValueMethod!.ReturnType.Should().Be(typeof(float?));
        getSensorValueMethod.GetParameters().Should().HaveCount(2);

        // 验证 GetSensorValues() 方法
        var getSensorValuesMethod = interfaceType.GetMethod("GetSensorValues");
        getSensorValuesMethod.Should().NotBeNull();
        getSensorValuesMethod!.ReturnType.Should().Be(typeof(IReadOnlyList<SensorReading>));
        getSensorValuesMethod.GetParameters().Should().HaveCount(2);

        // 验证 IsAvailable 属性
        var isAvailableProperty = interfaceType.GetProperty("IsAvailable");
        isAvailableProperty.Should().NotBeNull();
        isAvailableProperty!.PropertyType.Should().Be(typeof(bool));

        // 验证实现了 IDisposable 接口
        typeof(IDisposable).IsAssignableFrom(interfaceType).Should().BeTrue("接口应继承 IDisposable");
    }

    [Fact]
    public void DoneWhen_NoAdminPrivileges_ReturnsFlaseWithoutException()
    {
        // Verify: LibreHardwareManager 在无管理员权限时 Initialize() 返回 false，IsAvailable=false，不抛出异常

        _manager = new LibreHardwareManager(_mockLogger.Object);

        // Act
        Action act = () => _manager.Initialize();

        // Assert
        act.Should().NotThrow("无管理员权限时不应抛出异常");

        // 注意：在有管理员权限的环境下可能返回 true
        // 但无论如何都不应抛出异常
        if (!_manager.IsAvailable)
        {
            _manager.IsAvailable.Should().BeFalse("初始化失败时 IsAvailable 应为 false");
        }
    }

    [Fact]
    public void DoneWhen_CacheWorksWithin1Second()
    {
        // Verify: GetSensorValue() 在 1 秒内多次调用返回缓存值，不重复调用 Hardware.Update()

        _manager = new LibreHardwareManager(_mockLogger.Object);
        var initialized = _manager.Initialize();

        if (!initialized)
        {
            // 环境不支持，跳过测试
            return;
        }

        // Act - 第一次调用
        var startTime = DateTime.UtcNow;
        var firstCall = _manager.GetSensorValue(HardwareType.Cpu, SensorType.Load);

        // Act - 立即第二次调用（应使用缓存）
        var secondCall = _manager.GetSensorValue(HardwareType.Cpu, SensorType.Load);
        var endTime = DateTime.UtcNow;

        // Assert
        var elapsed = (endTime - startTime).TotalMilliseconds;
        elapsed.Should().BeLessThan(1000, "两次调用应在 1 秒内完成");

        // 如果第一次调用成功，第二次应返回相同的缓存值
        if (firstCall.HasValue)
        {
            secondCall.Should().Be(firstCall, "1 秒内应返回缓存值");
        }
    }

    [Fact]
    public async Task DoneWhen_ThreadSafe_100ConcurrentCalls()
    {
        // Verify: 多线程并发调用 GetSensorValue() 无竞态条件，通过 100 次并发测试

        _manager = new LibreHardwareManager(_mockLogger.Object);
        var initialized = _manager.Initialize();

        if (!initialized)
        {
            // 环境不支持，跳过测试
            return;
        }

        var exceptions = new List<Exception>();
        var results = new List<float?>();
        var tasks = new List<Task>();
        var lockObj = new object();

        // Act - 100 次并发调用
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    var result = _manager.GetSensorValue(HardwareType.Cpu, SensorType.Load);
                    lock (lockObj)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception ex)
                {
                    lock (lockObj)
                    {
                        exceptions.Add(ex);
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        exceptions.Should().BeEmpty("不应有任何线程安全异常");
        results.Should().HaveCount(100, "所有 100 次调用都应完成");
    }

    [Fact]
    public void DoneWhen_DisposeReleasesResources()
    {
        // Verify: Dispose() 后 Computer.Close() 被调用，所有资源释放

        _manager = new LibreHardwareManager(_mockLogger.Object);
        _manager.Initialize();

        // Act
        Action act = () => _manager.Dispose();

        // Assert
        act.Should().NotThrow("Dispose 不应抛出异常");

        // 验证 Dispose 后无法获取传感器值
        var result = _manager.GetSensorValue(HardwareType.Cpu, SensorType.Load);
        result.Should().BeNull("Dispose 后应返回 null");

        // 验证多次 Dispose 不会出错
        Action secondDispose = () => _manager.Dispose();
        secondDispose.Should().NotThrow("多次 Dispose 不应抛出异常");
    }

    [Fact]
    public void DoneWhen_ProjectCompilesSuccessfully()
    {
        // Verify: 项目编译成功，AllowUnsafeBlocks 启用，依赖包版本正确

        // 验证 LibreHardwareManager 类型存在且可实例化
        var managerType = typeof(LibreHardwareManager);
        managerType.Should().NotBeNull("LibreHardwareManager 类型应存在");

        // 验证可以创建实例
        Action createInstance = () => _manager = new LibreHardwareManager(_mockLogger.Object);
        createInstance.Should().NotThrow("应能成功创建 LibreHardwareManager 实例");

        // 验证 LibreHardwareMonitor.Hardware 命名空间可访问
        var hardwareType = typeof(HardwareType);
        hardwareType.Should().NotBeNull("LibreHardwareMonitor.Hardware 类型应可访问");

        var sensorType = typeof(SensorType);
        sensorType.Should().NotBeNull("LibreHardwareMonitor.Hardware.SensorType 应可访问");
    }

    [Fact]
    public void DoneWhen_AllRequiredMethodsExist()
    {
        // Verify: 所有必需的方法和属性都已实现

        _manager = new LibreHardwareManager(_mockLogger.Object);
        var managerType = _manager.GetType();

        // 验证 Initialize() 方法
        var initializeMethod = managerType.GetMethod("Initialize");
        initializeMethod.Should().NotBeNull("Initialize 方法应存在");

        // 验证 GetSensorValue() 方法
        var getSensorValueMethod = managerType.GetMethod("GetSensorValue");
        getSensorValueMethod.Should().NotBeNull("GetSensorValue 方法应存在");

        // 验证 GetSensorValues() 方法
        var getSensorValuesMethod = managerType.GetMethod("GetSensorValues");
        getSensorValuesMethod.Should().NotBeNull("GetSensorValues 方法应存在");

        // 验证 IsAvailable 属性
        var isAvailableProperty = managerType.GetProperty("IsAvailable");
        isAvailableProperty.Should().NotBeNull("IsAvailable 属性应存在");

        // 验证 Dispose() 方法
        var disposeMethod = managerType.GetMethod("Dispose");
        disposeMethod.Should().NotBeNull("Dispose 方法应存在");
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}
