# xhMonitor 项目架构梳理与问题诊断报告

## 📊 架构流程图

### 整体数据流架构图

```
┌─────────────────────────────────────────────────────────────────────────┐
│                          xhMonitor 系统架构                              │
└─────────────────────────────────────────────────────────────────────────┘

┌───────────────────────────────────────────────────────────────────────────┐
│                        XhMonitor.Service (后端)                           │
│                      ASP.NET Core 8.0 + SignalR                          │
└───────────────────────────────────────────────────────────────────────────┘
                                  │
        ┌─────────────────────────┼─────────────────────────┐
        ▼                         ▼                         ▼
┌──────────────────┐   ┌──────────────────┐   ┌──────────────────┐
│  Pipeline 1      │   │  Pipeline 2      │   │  Pipeline 3      │
│  硬件限制检测     │   │  系统使用率       │   │  进程级指标       │
│  (一次性)        │   │  (1秒循环)       │   │  (5秒循环)       │
└──────────────────┘   └──────────────────┘   └──────────────────┘
        │                         │                         │
        │                         │                         │
        ▼                         ▼                         ▼
┌──────────────────┐   ┌──────────────────┐   ┌──────────────────┐
│ 内存: WinAPI     │   │ CPU/GPU/内存/    │   │ ProcessScanner   │
│ VRAM: 多级降级   │   │ VRAM 系统总量    │   │ 关键词过滤        │
│ - PowerShell     │   │                  │   │ ↓                │
│ - Registry       │   │ MetricProvider   │   │ PerformanceMonitor│
│ - DxDiag         │   │ Registry         │   │ (并行 MaxDegree=4)│
│ - WMI            │   │                  │   │ ↓                │
└──────────────────┘   └──────────────────┘   │ 4个 Providers:   │
        │                         │            │ - CPU            │
        │                         │            │ - Memory         │
        │                         │            │ - GPU            │
        │                         │            │ - VRAM           │
        │                         │            └──────────────────┘
        │                         │                         │
        └─────────────────────────┼─────────────────────────┘
                                  ▼
                    ┌──────────────────────────┐
                    │    SignalR Hub           │
                    │  /hubs/metrics           │
                    ├──────────────────────────┤
                    │ • metrics.hardware       │
                    │ • metrics.system         │
                    │ • metrics.processes      │
                    └──────────────────────────┘
                                  │
                    ┌─────────────┴─────────────┐
                    ▼                           ▼
        ┌──────────────────────┐    ┌──────────────────────┐
        │  SQLite Database     │    │  WPF Desktop Client  │
        │  xhmonitor.db        │    │  FloatingWindow      │
        ├──────────────────────┤    ├──────────────────────┤
        │ • ProcessMetrics     │    │ SignalRService       │
        │   (原始数据)          │    │ ↓                    │
        │ • AggregatedMetrics  │    │ FloatingWindowVM     │
        │   (Min/Max/Avg)      │    │ • TopProcesses       │
        │ • AlertConfig        │    │ • PinnedProcesses    │
        └──────────────────────┘    │ • AllProcesses       │
                    ▲               └──────────────────────┘
                    │
        ┌──────────────────────┐
        │  AggregationWorker   │
        │  (1分钟循环)          │
        │  Raw → Minute →      │
        │  Hour → Day          │
        └──────────────────────┘
```

### 核心数据采集流程

```
用户配置关键词 (appsettings.json)
        │
        ▼
┌─────────────────────────────────────────┐
│ ProcessScanner.ScanProcesses()          │
│ • 遍历所有进程 (并行度=4)                 │
│ • 使用 WMI 获取命令行                    │
│ • 匹配关键词: "--port 8188", "llama-*"  │
└─────────────────────────────────────────┘
        │
        ▼ 返回匹配进程列表
┌─────────────────────────────────────────┐
│ PerformanceMonitor.CollectAllAsync()    │
│ • 并行处理进程 (并行度=4) ✅ 已优化      │
│ • 每个进程调用 4 个 Provider            │
│ • 每个 Provider 超时保护 2秒            │
└─────────────────────────────────────────┘
        │
        ▼ 并行调用 Providers (MaxDegree=8)
┌────────────┬────────────┬────────────┬────────────┐
│ CPU        │ Memory     │ GPU        │ VRAM       │
│ Provider   │ Provider   │ Provider   │ Provider   │
├────────────┼────────────┼────────────┼────────────┤
│ PC:        │ Process.   │ PC:        │ PC:        │
│ %Processor │ WorkingSet │ GPU Engine │ GPU Process│
│ Time       │ 64         │ Utilization│ Memory     │
│ / 核心数   │            │ Percentage │ Dedicated  │
└────────────┴────────────┴────────────┴────────────┘
        │
        ▼ 聚合为 ProcessMetricDto
┌─────────────────────────────────────────┐
│ Worker.SendProcessDataAsync()           │
│ • 保存到 SQLite (MetricRepository)      │
│ • SignalR 推送: "metrics.processes"     │
└─────────────────────────────────────────┘
```

## 🔍 发现的主要问题

### ⚠️ 严重问题 (需要立即修复)

1. **性能瓶颈: PerformanceMonitor 串行处理** ✅ 已修复
   - 位置: `XhMonitor.Service\Core\PerformanceMonitor.cs:43`
   - 问题: `MaxDegreeOfParallelism = 1` 导致100个进程需10+秒
   - 影响: 5秒采集周期可能被阻塞
   - 修复: 提升到 4 并发

2. **架构不一致: Worker 越权采集** ⚠️ 待修复
   - 位置: `Worker.cs` 的 `SendHardwareLimitsAsync()` 和 `SendSystemUsageAsync()`
   - 问题: 直接使用 Windows API/PerformanceCounter,绕过 Provider 架构
   - 影响: 违反单一职责,代码重复(VRAM/内存逻辑分散在多处)
   - 建议: 创建 `SystemMetricProvider` 统一管理

3. **VRAM 检测慢启动阻塞** ⚠️ 待修复
   - 位置: `Worker.cs:129-143`, `VramMetricProvider.cs`
   - 问题: PowerShell/DxDiag 检测可能耗时10+秒,阻塞启动
   - 影响: 服务启动延迟,用户体验差
   - 建议: 移到独立定时任务(每小时一次)

### ⚡ 性能问题

4. **CPU Provider 锁竞争** ✅ 已修复
   - 位置: `CpuMetricProvider.cs:113`
   - 问题: 同步锁 `_cacheLock.Wait()` 高并发阻塞
   - 修复: 改用 `await _cacheLock.WaitAsync()`

5. **PerformanceCounter 缓存无清理** ⚠️ 待修复
   - 位置: 所有 Provider 的 `_counters` 字典
   - 问题: 进程退出后 Counter 未清理,长时间运行内存泄漏
   - 建议: 定期检测进程存在性并清理

6. **SQLite 并发写入冲突** ✅ 已修复
   - 位置: `AggregationWorker` 和 `Worker` 同时写入
   - 问题: 可能触发 `SQLITE_BUSY` 错误
   - 修复: 启用 WAL 模式

### 📐 设计问题

7. **代码重复: VRAM/内存逻辑分散** ⚠️ 待修复
   - Worker 有独立的 `GetVramUsageOnlyAsync()` 和内存检测
   - VramMetricProvider/MemoryMetricProvider 也有类似逻辑
   - 使用不同的性能计数器类别
   - 建议: 统一到 Provider

8. **SignalR 事件命名不统一** ✅ 已修复
   - 定义: `metrics.hardware`, `metrics.system`, `metrics.processes`
   - 前端还订阅了 `metrics.latest` 但后端未实现
   - 修复: 定义常量类 `SignalREvents` 管理

9. **硬编码配置** ✅ 已修复
   - 端口: `35179` 硬编码在 `Program.cs` 和 `SignalRService.cs`
   - 修复: 移到 `appsettings.json`

10. **错误处理不完整** ✅ 已修复
    - Provider 返回 `MetricValue.Error()` 但后续未处理
    - 前端无法区分错误和正常值
    - 修复: PerformanceMonitor 层过滤错误值

## ✅ 设计优势

- ✨ **插件化架构**: `IMetricProvider` 接口设计优秀,支持动态加载
- ✨ **多级数据聚合**: Raw → Minute → Hour → Day 完整链路
- ✨ **实时推送优化**: 三条独立管道,硬件限制分步推送
- ✨ **前端交互设计**: 穿透模式适合游戏场景,位置持久化

## 📋 修复状态总结

### 已完成修复 (6/10)

1. ✅ 性能优化: 提升 PerformanceMonitor 并行度至 4
2. ✅ 并发优化: CPU Provider 异步锁机制
3. ✅ 架构一致性: 统一 SignalR 事件命名常量
4. ✅ 可配置性: 端口和 URL 配置化
5. ✅ 并发优化: SQLite WAL 模式
6. ✅ 错误处理: 过滤无效指标

### 待完成修复 (4/10)

7. ⚠️ 修复问题2: 创建 SystemMetricProvider 统一系统指标采集
8. ⚠️ 修复问题3: VRAM 检测移出启动路径
9. ⚠️ 修复问题5: 添加 PerformanceCounter 缓存清理机制
10. ⚠️ 修复问题7: 消除 Worker 中的重复 VRAM/内存逻辑

## 🎯 下一步计划

### 短期优化 (性能提升 70%+)

- ✅ 提升 PerformanceMonitor 并行度至 4-8
- ✅ 优化 CPU Provider 异步锁
- ⚠️ VRAM 检测移出启动路径

### 中期重构 (架构一致性)

- ⚠️ 创建 `SystemMetricProvider` 统一系统指标
- ⚠️ 消除 Worker 直接采集逻辑
- ✅ 统一 SignalR 事件命名

### 长期增强 (可靠性)

- ⚠️ PerformanceCounter 缓存清理机制
- ✅ SQLite WAL 模式
- ✅ 完善错误处理和用户提示

---

**文档生成时间**: 2025-12-27
**项目状态**: 部分优化完成,架构重构进行中
