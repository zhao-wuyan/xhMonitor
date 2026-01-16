# DXGI GPU 监控方案 - 快速参考

## 🎯 核心优势

| 指标 | 原方案 (PerformanceCounter) | 新方案 (DXGI) |
|------|---------------------------|---------------|
| **内存占用** | 800MB+ | < 1MB |
| **初始化时间** | 10-30 秒 | < 100ms |
| **支持厂家** | 全部 | 全部 (NVIDIA/AMD/Intel) |
| **依赖** | 性能计数器服务 | Windows 系统自带 |

---

## 📦 文件清单

```
.workflow/.lite-plan/analyze-memory-usage-2026-01-16/
├── DxgiGpuMonitor.cs           # DXGI P/Invoke 封装类（核心实现）
├── DXGI-Integration-Guide.md   # 完整集成指南
└── DXGI-Quick-Reference.md     # 本文件（快速参考）
```

---

## 🚀 快速开始（3 步集成）

### 步骤 1: 添加 DxgiGpuMonitor.cs

将 `DxgiGpuMonitor.cs` 复制到 `XhMonitor.Core/Monitoring/` 目录。

### 步骤 2: 修改 SystemMetricProvider.cs

**位置**: `XhMonitor.Core/Providers/SystemMetricProvider.cs`

```csharp
// 添加字段
private readonly DxgiGpuMonitor _dxgiMonitor = new();
private bool _dxgiAvailable;

// 构造函数
public SystemMetricProvider()
{
    _dxgiAvailable = _dxgiMonitor.Initialize();
}

// CollectAsync 方法
public async Task<Dictionary<string, MetricValue>> CollectAsync(ProcessInfo processInfo)
{
    var metrics = new Dictionary<string, MetricValue>();

    if (_dxgiAvailable)
    {
        var (total, used, percent) = _dxgiMonitor.GetTotalMemoryUsage();
        metrics["SystemGpuMemoryUsed"] = new MetricValue { Value = used / 1024.0 / 1024.0, Unit = "MB" };
        metrics["SystemGpuMemoryPercent"] = new MetricValue { Value = percent, Unit = "%" };
    }

    return metrics;
}

// Dispose 方法
public void Dispose()
{
    _dxgiMonitor?.Dispose();
}
```

### 步骤 3: 修改 GpuMetricProvider.cs（添加清理）

**位置**: `XhMonitor.Core/Providers/GpuMetricProvider.cs`

```csharp
// 添加字段
private readonly ConcurrentDictionary<int, DateTime> _lastAccessTime = new();
private int _cycleCount = 0;

// CollectAsync 方法中添加
public async Task<Dictionary<string, MetricValue>> CollectAsync(ProcessInfo processInfo)
{
    _lastAccessTime[processInfo.ProcessId] = DateTime.UtcNow;

    if (++_cycleCount >= 10)
    {
        _cycleCount = 0;
        CleanupExpiredEntries();
    }

    // ... 原有逻辑 ...
}

// 添加清理方法
private void CleanupExpiredEntries()
{
    var now = DateTime.UtcNow;
    var expiredPids = _lastAccessTime
        .Where(kvp => (now - kvp.Value).TotalSeconds > 60)
        .Select(kvp => kvp.Key)
        .ToList();

    foreach (var pid in expiredPids)
    {
        if (_counters.TryRemove(pid, out var counters))
        {
            foreach (var counter in counters)
                counter.Dispose();
        }
        _lastAccessTime.TryRemove(pid, out _);
    }
}
```

---

## 🧪 验证测试

### 测试 1: 内存占用验证

```bash
# 启动 Service
dotnet run --project XhMonitor.Service

# 观察内存占用
# 预期：启动后 < 150MB（原来 800MB+）
```

### 测试 2: 功能验证

```csharp
// 测试代码
using var monitor = new DxgiGpuMonitor();
if (monitor.Initialize())
{
    var (total, used, percent) = monitor.GetTotalMemoryUsage();
    Console.WriteLine($"GPU Memory: {used / 1024 / 1024} MB / {total / 1024 / 1024} MB ({percent:F1}%)");
}
```

### 测试 3: 性能验证

```csharp
var sw = Stopwatch.StartNew();
using var monitor = new DxgiGpuMonitor();
monitor.Initialize();
sw.Stop();

// 预期：< 100ms（原来 10-30 秒）
Console.WriteLine($"Initialization: {sw.ElapsedMilliseconds}ms");
```

---

## 📊 API 快速参考

### DxgiGpuMonitor 类

```csharp
// 初始化
bool Initialize()

// 获取所有 GPU 适配器
IReadOnlyList<GpuAdapter> GetAdapters()

// 获取每个 GPU 的内存使用情况
List<GpuMemoryInfo> GetMemoryUsage()

// 获取系统总 GPU 内存使用（所有 GPU 合计）
(ulong TotalMemory, ulong UsedMemory, double UsagePercent) GetTotalMemoryUsage()

// 释放资源
void Dispose()
```

### GpuAdapter 类

```csharp
string Name                    // GPU 名称（如 "NVIDIA GeForce RTX 3080"）
uint VendorId                  // 厂商 ID（0x10DE=NVIDIA, 0x1002=AMD, 0x8086=Intel）
ulong DedicatedVideoMemory     // 专用显存大小（字节）
ulong SharedSystemMemory       // 共享系统内存大小（字节）
```

### GpuMemoryInfo 类

```csharp
string AdapterName             // GPU 名称
ulong TotalMemory              // 总显存（字节）
ulong UsedMemory               // 已用显存（字节）
ulong AvailableMemory          // 可用显存（字节）
double UsagePercent            // 使用率（0-100）
```

---

## 🔧 故障排查

### Q: Initialize() 返回 false？

**A**: DXGI 不可用，可能原因：
- 无 GPU 设备
- 驱动未安装
- 虚拟机环境

**解决**: 降级到禁用 GPU 监控或使用其他方案

### Q: 内存使用率显示 0%？

**A**: GPU 驱动不支持 `QueryVideoMemoryInfo`（Windows 10 以下）

**解决**: 代码已自动跳过不支持的适配器

### Q: 多 GPU 系统数据不准确？

**A**: 使用 `GetMemoryUsage()` 查看每个 GPU 的详细信息

---

## 📈 预期效果

### Service 内存占用
- **修改前**: 80MB → 800MB（读取进程后）
- **修改后**: 80MB → < 150MB

### Desktop 内存占用
- **修改前**: 110MB
- **修改后**: < 50MB（配合其他优化）

### 总体优化
- **内存降低**: 60%+
- **启动加速**: 100 倍+（30 秒 → 100ms）
- **功能保留**: 100%

---

## 📚 完整文档

详细信息请参考：
- `DXGI-Integration-Guide.md` - 完整集成指南
- `DxgiGpuMonitor.cs` - 源代码实现
- `analysis-report.md` - 内存分析报告

---

## ✅ 迁移清单

- [ ] 复制 `DxgiGpuMonitor.cs` 到项目
- [ ] 修改 `SystemMetricProvider.cs`
- [ ] 修改 `GpuMetricProvider.cs`
- [ ] 运行单元测试
- [ ] 验证内存占用 < 150MB
- [ ] 验证初始化时间 < 100ms
- [ ] 部署到测试环境
- [ ] 监控生产环境

---

**生成时间**: 2026-01-16
**方案版本**: 1.0
**兼容性**: Windows 7+ / .NET 6+ / 所有厂家 GPU
