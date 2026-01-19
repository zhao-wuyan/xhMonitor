# GPU 监控方式切换

## 静态开关

`GpuMetricProvider.PreferPerformanceCounter` 控制 GPU 监控方式：

- **`true`** (默认): 优先使用 Performance Counter，推荐用于 **AMD GPU**
- **`false`**: 优先使用 D3DKMT，推荐用于 **NVIDIA/Intel GPU**

## 使用方法

### 方式 1: 程序启动时设置

```csharp
// 在程序入口点设置
public static void Main(string[] args)
{
    // AMD GPU 用户 (默认)
    GpuMetricProvider.PreferPerformanceCounter = true;

    // NVIDIA/Intel GPU 用户
    // GpuMetricProvider.PreferPerformanceCounter = false;

    // ... 启动应用
}
```

### 方式 2: 运行时切换

```csharp
// 运行时动态切换
GpuMetricProvider.PreferPerformanceCounter = false;  // 切换到 D3DKMT
```

## 两种模式对比

### Performance Counter 模式 (AMD 推荐)

**优点**:
- ✅ 在 AMD GPU 上准确可靠
- ✅ 能正确识别 Compute 引擎使用率
- ✅ 与任务管理器显示一致

**缺点**:
- ❌ 首次查询需要 100ms 预热
- ❌ 内存占用稍高 (需要创建多个 PerformanceCounter)

### D3DKMT 模式 (NVIDIA/Intel 推荐)

**优点**:
- ✅ 轻量级，无需创建 PerformanceCounter
- ✅ 查询速度快 (10-20ms)
- ✅ 在 NVIDIA/Intel GPU 上准确

**缺点**:
- ❌ 在 AMD GPU 上可能不准确
- ❌ 需要两次调用才能返回有效数据

## 日志输出

启动时会显示当前模式：

```
[Information] GPU monitoring mode: Performance Counter (AMD 推荐)
```

或

```
[Information] GPU monitoring mode: D3DKMT (NVIDIA/Intel 推荐)
```

## 故障排查

如果 GPU 使用率显示不准确：

1. **AMD GPU**: 确保 `PreferPerformanceCounter = true`
2. **NVIDIA/Intel GPU**: 尝试 `PreferPerformanceCounter = false`
3. **查看日志**: 检查是否有 "Failed to query GPU usage" 错误
4. **运行测试**: 执行 `GpuEngineUsageTests.SystemGpuUsage_MaxEngine_PrintComparison` 对比两种方式的结果
