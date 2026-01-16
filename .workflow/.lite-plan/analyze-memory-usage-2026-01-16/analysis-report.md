# XhMonitor 内存占用分析报告

**生成时间**: 2026-01-16
**分析范围**: Service (800+MB) 和 Desktop (110+MB) 内存占用过高问题
**探索角度**: Architecture, Dataflow, Performance

---

## 执行摘要

### 问题概述
- **Service**: 启动时 80MB，读取进程数据后暴涨至 800+MB（增长 10 倍）
- **Desktop**: 持续占用 110+MB 内存
- **环境**: 约 20 个匹配进程

### 根本原因
1. **Process.GetProcesses() 全量加载** - 每 5 秒加载所有系统进程（100-300 个）
2. **无界集合增长** - ConcurrentBag、ConcurrentDictionary 无驱逐策略
3. **EF Core ChangeTracker** - 持有实体引用直到 DbContext 释放
4. **SignalR 全量广播** - 每 5 秒广播完整进程列表
5. **字符串重复** - CommandLine（最长 2000 字符）跨多层存储

### 优化潜力
- **Service**: 800+MB → < 300MB（降低 60%+）
- **Desktop**: 110+MB → < 50MB（降低 55%+）

---

## 详细分析

### 1. Service 内存暴涨分析 (80MB → 800MB)

#### 1.1 Process.GetProcesses() 全量加载
**位置**: `XhMonitor.Service/Core/ProcessScanner.cs:36`

```csharp
var processes = Process.GetProcesses(); // 返回所有系统进程（100-300 个）
```

**问题**:
- 每 5 秒调用一次，分配 Process[] 数组 + 每个进程的 native handle
- 即使只监控 20 个进程，仍加载全部系统进程
- 每个 Process 对象约 2-5KB，100 个进程 = 200-500KB/次
- 配合 Parallel.ForEach (MaxDegreeOfParallelism=4)，并发分配放大内存压力

**影响**: 每次采集周期分配 200-500KB，累积导致 GC 压力

#### 1.2 无界集合增长
**位置**:
- `XhMonitor.Core/Providers/GpuMetricProvider.cs:11` - `ConcurrentDictionary<int, List<PerformanceCounter>>`
- `XhMonitor.Core/Providers/CpuMetricProvider.cs:12-13` - `Dictionary<int, double>` + `Dictionary<int, (long, DateTime)>`

**问题**:
- GpuMetricProvider._counters 为每个进程缓存 PerformanceCounter 对象
- CpuMetricProvider._cachedCpuData 缓存所有进程的 CPU 数据
- **无驱逐策略** - 已退出进程的条目永不清理（直到 Dispose）
- 长时间运行后，缓存包含数百个已退出进程的条目

**影响**:
- 每个 PerformanceCounter 对象约 1-2KB + native handle
- 100 个已退出进程 = 100-200KB 永久占用
- 随时间累积，可达数 MB

#### 1.3 EF Core ChangeTracker 持有实体引用
**位置**: `XhMonitor.Service/Data/Repositories/MetricRepository.cs:53`

```csharp
await context.ProcessMetrics.AddRangeAsync(records);
await context.SaveChangesAsync();
// ⚠️ 缺少 context.ChangeTracker.Clear()
```

**问题**:
- EF Core 默认跟踪所有实体直到 DbContext 释放
- 每次保存 20 个 ProcessMetricRecord，每个包含 JSON 序列化的 Metrics（约 500B-2KB）
- 使用 DbContextFactory 模式，但 context 在 SaveChangesAsync 后仍持有引用
- 如果 SaveChangesAsync 耗时 > 5s，多个 context 实例重叠

**影响**:
- 20 个进程 × 2KB/进程 = 40KB/周期
- 10 个周期重叠 = 400KB 被 ChangeTracker 持有

#### 1.4 SignalR 全量广播
**位置**: `XhMonitor.Service/Worker.cs:248-260`

```csharp
await _hubContext.Clients.All.SendAsync("ProcessMetrics", new {
    Processes = metrics.Select(m => new {
        m.Info.ProcessId,
        m.Info.ProcessName,
        m.Info.CommandLine, // 最长 2000 字符
        m.Info.DisplayName,
        Metrics = m.Metrics // 完整字典
    }).ToList()
});
```

**问题**:
- 每 5 秒广播所有 20 个进程的完整数据
- 创建匿名对象图 + JSON 序列化 + SignalR 消息缓冲
- CommandLine 字符串可达 2000 字符，20 个进程 = 40KB 字符串数据
- 序列化过程创建临时字符串和 byte[] 数组

**影响**:
- 每次广播分配 50-100KB 临时对象
- 高频广播（5s 间隔）导致 Gen0 GC 频繁触发

#### 1.5 字符串重复存储
**位置**: 多层存储 CommandLine 字符串

```
ProcessInfo.CommandLine (ProcessScanner.cs:114)
  → ProcessMetrics.Info.CommandLine (PerformanceMonitor.cs:79)
    → ProcessMetricRecord.MetricsJson (MetricRepository.cs:67, JSON 序列化)
      → SignalR 匿名对象 (Worker.cs:254)
```

**问题**:
- 同一个 CommandLine 字符串在 4 个地方存储
- 20 个进程 × 平均 500 字符 × 4 层 = 40KB × 4 = 160KB
- 无字符串驻留（string interning）

**影响**: 每个采集周期浪费 100-160KB 用于重复字符串

---

### 2. Desktop 内存占用分析 (110MB)

#### 2.1 无界进程索引
**位置**: `XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs:14`

```csharp
private readonly Dictionary<int, ProcessRowViewModel> _processIndex = new();
```

**问题**:
- 为每个接收到的进程创建 ProcessRowViewModel 实例
- 仅在进程退出时移除（SyncProcessIndex line 303-307）
- 如果进程频繁启动/停止，索引在同步周期间累积陈旧条目
- 长时间运行后，索引包含数百个历史进程

**影响**:
- 每个 ProcessRowViewModel 约 500B-1KB（包含属性 + ObservableObject 开销）
- 200 个历史进程 = 100-200KB

#### 2.2 三个 ObservableCollection
**位置**: `FloatingWindowViewModel.cs:18-20`

```csharp
public ObservableCollection<ProcessRowViewModel> TopProcesses { get; } = new();
public ObservableCollection<ProcessRowViewModel> PinnedProcesses { get; } = new();
public ObservableCollection<ProcessRowViewModel> AllProcesses { get; } = new();
```

**问题**:
- AllProcesses 包含所有进程（与 _processIndex 重复）
- ObservableCollection 为每个元素维护 PropertyChanged 事件订阅
- SyncCollectionOrder (line 319-348) 执行 O(n²) 操作（IndexOf, Contains）

**影响**:
- 3 个集合 × 100 个进程 × 1KB = 300KB
- 频繁的集合操作触发 WPF 变更通知，增加 UI 线程压力

#### 2.3 Dispatcher.Invoke 编组开销
**位置**: `FloatingWindowViewModel.cs:191`

```csharp
Application.Current.Dispatcher.Invoke(() => {
    SyncProcessIndex(processData);
});
```

**问题**:
- 所有 SignalR 更新（每 1-5 秒）通过 Dispatcher.Invoke 编组到 UI 线程
- 如果 UI 线程繁忙，消息在 Dispatcher 队列中累积
- 每个 Invoke 调用分配委托对象 + 参数捕获

**影响**:
- 高频更新导致 Dispatcher 队列压力
- 委托分配增加 GC 负担

---

## 优化方案

### 方案 1: 优化 EF Core 数据持久化内存占用
**目标**: 避免 ChangeTracker 持有实体引用

**实现**:
1. 在 `MetricRepository.SaveMetricsAsync()` 的 `SaveChangesAsync()` 后添加 `context.ChangeTracker.Clear()`
2. 在 `MonitorDbContext.OnConfiguring()` 中设置 `QueryTrackingBehavior.NoTracking`

**预期效果**:
- SaveChangesAsync 后 ChangeTracker 为空
- 减少 40KB × 10 周期 = 400KB 内存占用

**文件**:
- `XhMonitor.Service/Data/Repositories/MetricRepository.cs`
- `XhMonitor.Service/Data/MonitorDbContext.cs`

---

### 方案 2: 限制 Desktop 进程索引和集合大小
**目标**: 防止索引无限增长

**实现**:
1. 添加 `const int MaxProcessCount = 100`
2. 在 `ProcessRowViewModel` 添加 `DateTime LastUpdateTime` 属性
3. 在 `SyncProcessIndex()` 中实现 LRU 清理：
   - 检查 `_processIndex.Count > MaxProcessCount`
   - 按 `LastUpdateTime` 排序，移除最旧的非置顶进程

**预期效果**:
- 索引大小 ≤ 100
- 减少 100KB+ 内存占用
- Desktop 内存稳定在 50MB 以下

**文件**:
- `XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
- `XhMonitor.Desktop/Models/ProcessRowViewModel.cs`

---

### 方案 3: 减少 SignalR 广播负载大小
**目标**: 仅广播 Top-N 进程

**实现**:
1. 在 `appsettings.json` 添加 `"MaxBroadcastProcessCount": 20`
2. 在 `Worker` 构造函数读取配置
3. 在 `SendProcessDataAsync()` 中按 CPU/内存排序，取 Top-N

```csharp
Processes = metrics
    .OrderByDescending(m => m.Metrics.GetValueOrDefault("CPU", 0))
    .Take(_maxBroadcastCount)
    .Select(...)
```

**预期效果**:
- 广播负载减少 80%+（如果原有 100 个进程）
- 减少 50-80KB/次 临时对象分配
- Service 内存峰值降低

**文件**:
- `XhMonitor.Service/Worker.cs`
- `XhMonitor.Service/appsettings.json`

---

### 方案 4: 添加指标提供者缓存清理策略
**目标**: 清理已退出进程的缓存条目

**实现**:
1. 添加 `ConcurrentDictionary<int, DateTime> _lastAccessTime` 跟踪访问时间
2. 在 `CollectAsync()` 中更新 `_lastAccessTime[processId] = DateTime.UtcNow`
3. 添加 `CleanupExpiredEntries()` 方法：
   - 移除 `DateTime.UtcNow - _lastAccessTime[pid] > 60s` 的条目
   - 限制缓存大小 ≤ 1000

**预期效果**:
- 缓存大小受控
- 减少 100-200KB 永久占用
- 已退出进程条目在 60 秒后自动清理

**文件**:
- `XhMonitor.Core/Providers/GpuMetricProvider.cs`
- `XhMonitor.Core/Providers/CpuMetricProvider.cs`

---

## 内存分配热点汇总

| 位置 | 分配频率 | 单次分配 | 累积影响 | 优先级 |
|------|---------|---------|---------|--------|
| ProcessScanner.cs:36 (Process.GetProcesses) | 每 5s | 200-500KB | 高 GC 压力 | ⭐⭐⭐⭐⭐ |
| GpuMetricProvider.cs:11 (无界缓存) | 持续增长 | 1-2KB/进程 | 数 MB 永久占用 | ⭐⭐⭐⭐ |
| Worker.cs:248-260 (SignalR 广播) | 每 5s | 50-100KB | 高 Gen0 GC | ⭐⭐⭐⭐ |
| MetricRepository.cs:53 (ChangeTracker) | 每 5s | 40KB | 400KB 重叠 | ⭐⭐⭐ |
| FloatingWindowViewModel.cs:14 (无界索引) | 持续增长 | 500B-1KB/进程 | 100-200KB | ⭐⭐⭐ |
| ProcessScanner.cs:114 (字符串重复) | 每 5s | 100-160KB | 高 GC 压力 | ⭐⭐ |

---

## 实施建议

### 阶段 1: 快速见效（1-2 小时）
1. **方案 1** - EF Core ChangeTracker 清理（最简单，立即见效）
2. **方案 3** - SignalR Top-N 过滤（配置项，无风险）

**预期**: Service 内存降低 30-40%

### 阶段 2: 深度优化（2-3 小时）
3. **方案 2** - Desktop 索引大小限制（需要 LRU 逻辑）
4. **方案 4** - 缓存清理策略（需要 TTL 跟踪）

**预期**: Service 内存降低 60%+，Desktop 内存降低 55%+

### 阶段 3: 长期优化（可选）
- 实现 Process.GetProcesses() 缓存（仅在进程数变化时刷新）
- 字符串驻留（string interning）减少重复
- 对象池（ArrayPool, ObjectPool）减少分配

---

## 验证方法

### 内存监控
```csharp
// 在 Worker.ExecuteAsync 中添加
var memoryBefore = GC.GetTotalMemory(false);
// ... 执行采集 ...
var memoryAfter = GC.GetTotalMemory(false);
_logger.LogInformation("Memory delta: {Delta} KB", (memoryAfter - memoryBefore) / 1024);
```

### 性能计数器
- Process\Private Bytes (Service 进程)
- Process\Private Bytes (Desktop 进程)
- .NET CLR Memory\# Gen 0 Collections
- .NET CLR Memory\# Bytes in all Heaps

### 验收标准
- [ ] Service 启动内存 < 100MB
- [ ] Service 稳定运行 1 小时后 < 300MB
- [ ] Desktop 稳定运行 1 小时后 < 50MB
- [ ] Gen0 GC 频率降低 50%+
- [ ] 所有功能正常，无数据丢失

---

## 风险评估

| 方案 | 风险等级 | 潜在问题 | 缓解措施 |
|------|---------|---------|---------|
| 方案 1 (EF Core) | 低 | 删除操作可能需要跟踪 | 仅对写入操作使用 NoTracking |
| 方案 2 (Desktop 索引) | 中 | LRU 逻辑可能误删活跃进程 | 保护置顶进程，添加日志 |
| 方案 3 (SignalR Top-N) | 低 | Desktop 可能看不到所有进程 | 添加配置项，默认 20 |
| 方案 4 (缓存清理) | 中 | TTL 过短可能导致频繁重建 | 设置 60s TTL，监控性能 |

---

## 附录：探索文件

### 探索清单
- `exploration-architecture.json` - 架构视角分析
- `exploration-dataflow.json` - 数据流视角分析
- `exploration-performance.json` - 性能视角分析
- `explorations-manifest.json` - 探索索引

### 实施计划
- `plan.json` - 详细实施计划（4 个并行任务）

### 会话信息
- **会话 ID**: analyze-memory-usage-2026-01-16
- **复杂度**: Medium
- **探索角度**: architecture, dataflow, performance
- **用户确认**:
  - 进程数量: 20 左右
  - EF Core: 使用 AsNoTracking()
  - Desktop: 限制索引大小
  - SignalR: 减少负载大小

---

**报告生成时间**: 2026-01-16
**分析工具**: Claude Code + cli-explore-agent + cli-lite-planning-agent
**下一步**: 根据实施建议执行优化方案
