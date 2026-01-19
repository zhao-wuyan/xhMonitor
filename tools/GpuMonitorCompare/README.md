# GPU 监控对比工具

独立的 GPU 监控对比工具，用于对比 D3DKMT 和 Performance Counter 两种 GPU 监控方式的准确性。

## 功能

- 同时运行 D3DKMT 和 Performance Counter 两种监控方式
- 实时显示两种方式的 GPU 使用率对比
- 根据差异大小使用颜色标识 (绿色/黄色/红色)
- 支持自定义采样间隔和运行时长

## 编译

```bash
cd tools/GpuMonitorCompare
dotnet build
```

## 运行

```bash
# 默认参数 (1秒采样间隔，无限运行)
dotnet run

# 自定义采样间隔 (500ms)
dotnet run 500

# 自定义采样间隔和运行时长 (500ms, 60秒)
dotnet run 500 60
```

## 输出示例

```
================================================================================
GPU 监控对比工具 - D3DKMT vs Performance Counter
================================================================================
采样间隔: 1000ms
运行时长: 无限
按 Ctrl+C 退出
================================================================================

【计算方式说明】

D3DKMT 方式:
  - 查询 GPU 节点运行时间 (RunningTime, 单位: 微秒)
  - 计算公式: Usage% = (RunningDelta / TimeDelta) * 100
  - RunningDelta = 当前RunningTime - 上次RunningTime
  - TimeDelta = 当前时间戳 - 上次时间戳 (毫秒)
  - 使用 SystemInformation.RunningTime (优先) 或 GlobalInformation.RunningTime

Performance Counter 方式:
  - 直接读取 Windows 性能计数器 "GPU Engine\Utilization Percentage"
  - 需要 100ms 预热时间 (调用两次 NextValue)
  - 返回各个 GPU 引擎的使用率 (3D, Compute, Copy, Video Decode 等)

[D3DKMT] 初始化成功: LUID=10780_0, Nodes=4
[PerfCounter] 初始化成功: 24 个引擎

时间         #     D3DKMT     PerfCtr    差异       D3D详情                        Perf详情
------------------------------------------------------------------------------------------------------------------------
20:30:15.123 1       12.5%      13.2%       0.7%    Node0=12.5%                    3D=13.2%
  D3D详情: N0[G:1234ms,S:1234ms,Δ:125.0ms,T:1000.0ms,U:12.5%]
  Perf详情: 3D:13.2% Copy:0.5%
20:30:16.124 2       45.8%      46.1%       0.3%    Node0=45.8%                    3D=46.1%
  D3D详情: N0[G:2692ms,S:2692ms,Δ:458.0ms,T:1000.0ms,U:45.8%]
  Perf详情: 3D:46.1% Copy:1.2%
20:30:17.125 3       78.2%      65.4%      12.8%    Node0=78.2%                    Compute=65.4%
  D3D详情: N0[G:3474ms,S:3474ms,Δ:782.0ms,T:1000.0ms,U:78.2%]
  Perf详情: Compute:65.4% 3D:12.3%
```

**详细信息说明**:
- `N0[...]`: 节点 0 的详细信息
- `G`: GlobalInformation.RunningTime (毫秒)
- `S`: SystemInformation.RunningTime (毫秒)
- `Δ`: RunningDelta (本次采样的运行时间增量, 毫秒)
- `T`: TimeDelta (本次采样的时间间隔, 毫秒)
- `U`: Usage (计算出的使用率, %)


## 颜色说明

- **绿色**: 差异 ≤ 10% (两种方式结果接近)
- **黄色**: 差异 10-20% (存在一定差异)
- **红色**: 差异 > 20% (差异较大，可能需要切换监控方式)

## 使用场景

### 1. 验证监控准确性

在不同 GPU 上运行此工具，观察两种方式的差异：

- **AMD GPU**: Performance Counter 通常更准确
- **NVIDIA/Intel GPU**: D3DKMT 通常更准确

### 2. 选择合适的监控方式

根据对比结果，在主程序中设置 `GpuMetricProvider.PreferPerformanceCounter`：

```csharp
// AMD GPU 用户
GpuMetricProvider.PreferPerformanceCounter = true;

// NVIDIA/Intel GPU 用户
GpuMetricProvider.PreferPerformanceCounter = false;
```

### 3. 故障排查

如果主程序的 GPU 使用率显示不准确，运行此工具对比两种方式的结果，找出问题原因。

## 技术细节

### D3DKMT 监控

- 使用 DXGI + D3DKMT API 查询 GPU 节点运行时间
- 通过计算时间差值得出使用率
- 轻量级，查询速度快 (10-20ms)

### Performance Counter 监控

- 使用 Windows Performance Counter 查询 GPU 引擎使用率
- 需要 100ms 预热时间
- 能正确识别不同引擎类型 (3D, Compute, Copy 等)

## 相关文档

- [GpuMetricProvider.README.md](../../XhMonitor.Core/Providers/GpuMetricProvider.README.md) - 主程序 GPU 监控方式切换说明
