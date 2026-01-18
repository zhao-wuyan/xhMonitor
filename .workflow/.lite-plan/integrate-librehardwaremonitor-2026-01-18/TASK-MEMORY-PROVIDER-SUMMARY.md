# Task: 实现 LibreHardwareMonitor Memory 提供者

## 实现总结

### 文件修改
- `XhMonitor.Core/Providers/LibreHardwareMonitorMemoryProvider.cs`: 创建混合架构内存提供者
- `XhMonitor.Tests/Providers/LibreHardwareMonitorMemoryProviderTests.cs`: 创建单元测试（16 个测试）
- `XhMonitor.Tests/Providers/LibreHardwareMonitorMemoryProviderIntegrationTests.cs`: 创建集成测试（8 个测试）

### 内容添加

#### LibreHardwareMonitorMemoryProvider (`XhMonitor.Core/Providers/LibreHardwareMonitorMemoryProvider.cs`)
- **目的**: 混合架构内存监控提供者，系统级使用 LibreHardwareMonitor，进程级委托给 MemoryMetricProvider
- **关键属性**:
  - `MetricId`: "memory"
  - `DisplayName`: "内存使用率 (LibreHardwareMonitor)"
  - `Unit`: "%"
  - `Type`: MetricType.Percentage
- **关键方法**:
  - `IsSupported()`: 检查 LibreHardwareManager 是否可用
  - `GetSystemTotalAsync()`: 读取 HardwareType.Memory 的 SensorType.Load 传感器，返回 0-100% 范围内的内存使用率
  - `CollectAsync(processId)`: 委托给 MemoryMetricProvider.CollectAsync() 处理进程级内存监控
  - `Dispose()`: 释放 MemoryMetricProvider 资源

#### 单元测试 (`LibreHardwareMonitorMemoryProviderTests.cs`)
- **测试覆盖**:
  - 构造函数参数验证（null 检查）
  - 元数据属性验证（MetricId, DisplayName, Unit）
  - IsSupported() 在不同场景下的行为
  - GetSystemTotalAsync() 的各种场景（正常值、null、异常、边界值）
  - CollectAsync() 委托行为验证
  - Dispose() 资源释放验证
- **测试数量**: 16 个测试，全部通过

#### 集成测试 (`LibreHardwareMonitorMemoryProviderIntegrationTests.cs`)
- **Done When 验证**:
  - ✅ GetSystemTotalAsync() 返回 0-100 范围内的内存使用率
  - ✅ CollectAsync(processId) 正确返回进程级内存使用量（委托给 MemoryMetricProvider）
  - ✅ 进程级监控功能与现有实现完全一致，无功能退化
  - ✅ LibreHardwareManager 不可用时 IsSupported() 返回 false
  - ✅ 传感器读取失败时返回 0.0 且记录日志
  - ✅ 混合架构验证：系统级使用 LibreHardwareMonitor（%），进程级使用 PerformanceCounter（MB）
  - ✅ Dispose() 正确释放资源
  - ✅ 处理无效进程 ID
- **测试数量**: 8 个测试，全部通过

## 输出供依赖任务使用

### 可用组件
```csharp
// 导入混合架构内存提供者
using XhMonitor.Core.Providers;

// 创建实例（需要依赖注入）
var provider = new LibreHardwareMonitorMemoryProvider(
    hardwareManager,      // ILibreHardwareManager
    memoryMetricProvider, // MemoryMetricProvider
    logger                // ILogger<LibreHardwareMonitorMemoryProvider>
);
```

### 集成点
- **DI 注册**: 需要在 Program.cs 中注册为单例，依赖 ILibreHardwareManager 和 MemoryMetricProvider
- **MetricProviderRegistry**: 在 RegisterBuiltInProviders() 中根据 IsAvailable 状态选择注册混合架构提供者或原始提供者
- **配置项**: 可通过配置控制是否启用 LibreHardwareMonitor 内存监控

### 使用示例
```csharp
// 获取系统内存使用率（LibreHardwareMonitor）
var systemMemoryUsage = await provider.GetSystemTotalAsync(); // 返回 0-100%

// 获取进程内存使用量（MemoryMetricProvider）
var processMemory = await provider.CollectAsync(processId); // 返回 MB
```

## 架构特点

### 混合架构设计
1. **系统级指标**: 使用 LibreHardwareMonitor 读取 Memory Load 传感器
   - 优点: 更准确的系统级内存使用率
   - 单位: 百分比（%）
   - 范围: 0-100

2. **进程级指标**: 委托给现有 MemoryMetricProvider
   - 优点: 保持现有实现的稳定性和兼容性
   - 单位: MB
   - 方法: Process.WorkingSet64

### 错误处理
- LibreHardwareManager 不可用时返回 0.0
- 传感器读取失败时返回 0.0 并记录日志
- 进程不存在时返回 MetricValue.Error()

### 资源管理
- 实现 IDisposable 接口
- Dispose() 时释放委托的 MemoryMetricProvider 资源
- 支持多次 Dispose() 调用

## 测试结果

### 编译状态
- ✅ XhMonitor.Core 编译成功（0 错误，仅警告）
- ✅ XhMonitor.Tests 编译成功（0 错误，仅警告）

### 测试执行
- ✅ 单元测试: 16/16 通过
- ✅ 集成测试: 8/8 通过
- ✅ 总计: 24/24 通过

### Done When 清单验证
- [x] GetSystemTotalAsync() 返回 0-100 范围内的内存使用率（来自 LibreHardwareMonitor）
- [x] CollectAsync(processId) 正确返回进程级内存使用量（委托给 MemoryMetricProvider）
- [x] 进程级监控功能与现有实现完全一致，无功能退化
- [x] 在 LibreHardwareManager 不可用时 IsSupported() 返回 false
- [x] 传感器读取失败时返回 0.0 且记录日志

## 状态: ✅ 完成

所有 Done When 条件已满足，实现已通过全部测试验证。
