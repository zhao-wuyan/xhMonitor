# Phase A Plan（2-3 天，低风险）

**Session ID**: `ANL-desktop-service-memory-optimization-2026-02-08`  
**Goal**: 在不减少功能前提下，优先降低 `service` 与 `desktop` 常驻内存及分配率。  
**Scope**: 仅做低风险、可回滚优化；不改业务功能，不改协议语义。

---

## Day 1（Service 主路径）

### A1. Worker 快照构建减分配

- 文件：
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Worker.cs`
- 位置：
  - `SendProcessDataAsync`（约 `:280`）
  - `RunProcessPushLoopAsync`（约 `:337`）
- 改动：
  - 替换 `Select/ToDictionary/ToList` 为预分配容量手工循环。
  - 复用快照对象，避免推送前再次构造匿名投影集合。
- 预计工时：`3-4h`
- 验收：
  - payload 字段与当前一致。
  - `WorkerTests` 新增断言全部通过。

### A2. ProcessScanner 命令行缓存

- 文件：
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Core/ProcessScanner.cs`
- 位置：
  - `ScanProcesses`（约 `:130`）
  - `ProcessSingleProcess`（约 `:174`）
- 改动：
  - 增加 PID 级命令行短 TTL 缓存（`20-30s`）。
  - 每轮清理不存在 PID 的缓存项。
- 预计工时：`2-3h`
- 验收：
  - 过滤结果不变化。
  - 缓存命中时减少 `GetCommandLine` 调用次数。

---

## Day 2（聚合与传输层）

### A3. 聚合查询无跟踪化

- 文件：
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Workers/AggregationWorker.cs`
- 位置：
  - `AggregateRawToMinuteAsync`（约 `:61`）
  - `AggregateMinuteToHourAsync`（约 `:177`）
  - `AggregateHourToDayAsync`（约 `:232`）
- 改动：
  - 所有窗口读取查询添加 `AsNoTracking()`。
- 预计工时：`1-2h`
- 验收：
  - 聚合结果与当前一致。
  - 聚合周期峰值内存下降或更平稳。

### A4. SignalR 缓冲上限配置化

- 文件：
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Program.cs`
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/appsettings.json`
- 位置：
  - `AddSignalR`（约 `:263`）
  - `MapHub`（约 `:341`）
- 改动：
  - 新增 `SignalR` 配置项：
    - `MaximumReceiveMessageSize`
    - `ApplicationMaxBufferSize`
    - `TransportMaxBufferSize`
  - 在 `Program` 读取配置并应用。
- 预计工时：`2h`
- 验收：
  - 连接稳定，无消息截断。
  - 峰值内存抖动下降。

---

## Day 3（Desktop）

### A5. SignalR 反序列化减分配

- 文件：
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/Services/SignalRService.cs`
- 位置：
  - 回调 `ReceiveHardwareLimits` / `ReceiveSystemUsage` / `ReceiveProcessMetrics` / `ReceiveProcessMetadata` / `metrics.latest`（约 `:50-110`）
- 改动：
  - 用 `JsonElement.Deserialize<T>()` 替换 `GetRawText()+Deserialize`。
- 预计工时：`1-2h`
- 验收：
  - 反序列化字段完整。
  - 长时间运行分配率下降。

### A6. ViewModel 列表刷新轻量节流

- 文件：
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
- 位置：
  - `OnProcessDataReceived`（约 `:271`）
  - `OnMetricsReceived`（约 `:344`）
  - `SyncCollectionOrder`（约 `:451`）
- 改动：
  - 提取公共刷新逻辑并做短窗口合并（`100-200ms`）。
  - 高频刷新改 `BeginInvoke`，避免同步阻塞。
- 预计工时：`3-4h`
- 验收：
  - 界面实时性无明显回退。
  - Desktop RSS 与 UI 抖动下降。

---

## 验证与回归

### 建议命令

```powershell
dotnet test C:/ProjectDev/project/xinghe/xhMonitor/xhMonitor.sln
dotnet-counters monitor --process-id <service_pid> System.Runtime
dotnet-counters monitor --process-id <desktop_pid> System.Runtime
```

### 验收门槛

- `service`: `170MB+ -> <=150MB`（同负载，运行 30-60 分钟）
- `desktop`: `80MB -> <=70MB`（同功能打开路径）
- `alloc rate` 下降 `>=15%`
- UI 响应 `P95` 不劣化

### 回滚策略

- 每个子任务独立提交（建议 1 个任务 1 个 commit）。
- 引入新配置项时保留原默认行为，可通过配置立即回退。
