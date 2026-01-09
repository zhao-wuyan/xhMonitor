# XhMonitor.Service/Core

本模块包含后端服务的核心业务逻辑，负责协调进程扫描、指标采集和数据存储。

## 职责

- **PerformanceMonitor**: 协调所有监控流程的核心引擎
- **ProcessScanner**: 扫描和过滤系统进程
- **MetricProviderRegistry**: 管理所有指标提供者

## 结构

```
XhMonitor.Service/Core/
├── PerformanceMonitor.cs      # 监控核心协调器
├── ProcessScanner.cs         # 进程扫描器
└── MetricProviderRegistry.cs # 指标提供者注册表
```

## 关键文件

| 文件 | 目的 |
|------|------|
| `PerformanceMonitor.cs` | 核心监控引擎，协调进程扫描和指标采集 |
| `ProcessScanner.cs` | 扫描系统进程，根据关键词过滤 |
| `MetricProviderRegistry.cs` | 管理所有 IMetricProvider 实例 |

## 依赖

**本模块依赖**:
- `../XhMonitor.Core/Interfaces/` - 核心接口定义
- `../XhMonitor.Core/Models/` - 数据模型
- `../XhMonitor.Core/Providers/` - 指标提供者实现
- `Microsoft.Extensions.Logging` - 日志

**依赖本模块的**:
- `../Worker.cs` - 主监控循环
- `../Hubs/MetricsHub.cs` - SignalR 推送

## 代码模式

### PerformanceMonitor 主循环

```csharp
public async Task<List<ProcessMetrics>> CollectAllAsync()
{
    // 1. 扫描进程
    var processes = _scanner.ScanProcesses();
    
    // 2. 获取提供者
    var providers = _registry.GetAllProviders();
    
    // 3. 并行采集（MaxDegreeOfParallelism=1）
    var results = new ConcurrentBag<ProcessMetrics>();
    await Parallel.ForEachAsync(processes, parallelOptions, async (process, ct) =>
    {
        var metrics = new Dictionary<string, MetricValue>();
        foreach (var provider in providers)
        {
            var value = await CollectMetricSafeAsync(provider, process.ProcessId);
            metrics[provider.MetricId] = value;
        }
        results.Add(new ProcessMetrics { Info = process, Metrics = metrics });
    });
    
    return results.ToList();
}
```

### 进程过滤

```csharp
public List<ProcessInfo> ScanProcesses()
{
    var keywords = _configuration.GetSection("Monitor:Keywords").Get<string[]>() ?? Array.Empty<string>();
    
    return Process.GetProcesses()
        .Where(p => keywords.Any(k => p.ProcessName.Contains(k, StringComparison.OrdinalIgnoreCase)))
        .Select(p => new ProcessInfo
        {
            ProcessId = p.Id,
            ProcessName = p.ProcessName,
            CommandLine = ProcessCommandLineReader.GetCommandLine(p.Id)
        })
        .ToList();
}
```

## 错误处理

- 单个指标采集失败不会影响其他指标
- 使用 `MetricValue.Error` 记录错误信息
- 进程不存在时跳过（`ArgumentException`）
- Provider 超时（2 秒）返回错误值

## 测试

本模块使用 Moq 进行依赖注入和单元测试：

```csharp
[Fact]
public async Task CollectAllAsync_ShouldReturnProcessMetrics()
{
    // Arrange
    var mockScanner = new Mock<IProcessScanner>();
    var mockRegistry = new Mock<IMetricProviderRegistry>();
    
    // Act
    var results = await _monitor.CollectAllAsync();
    
    // Assert
    Assert.NotEmpty(results);
}
```

## 添加新功能

### 添加新的扫描过滤器

1. 修改 `ProcessScanner.cs`
2. 添加新的过滤逻辑
3. 更新配置模式
4. 添加单元测试

### 修改并发策略

**当前状态**: `MaxDegreeOfParallelism = 1`（串行）

**优化方向**:
- 提高并发度到 4-8
- 需要确保 PerformanceCounter 线程安全
- 可能需要使用 WMI 替代

## 已知问题

1. **串行采集**: 由于 PerformanceCounter 的线程安全问题，目前使用串行采集
2. **超时固定**: 2 秒超时可能对慢进程过严
3. **无重试**: 采集失败后不会重试

## 未来改进

- [ ] 替换 PerformanceCounter 为 WMI 异步 API
- [ ] 实现数据重试队列
- [ ] 配置化并发度和超时
- [ ] 添加性能指标（采集耗时统计）
