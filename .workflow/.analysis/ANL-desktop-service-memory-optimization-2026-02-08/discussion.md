# Analysis Discussion

**Session ID**: `ANL-desktop-service-memory-optimization-2026-02-08`  
**Topic**: 分析桌面端和 service 端内存占用问题，在不减少功能情况下尽可能降低内存和资源占用  
**Started**: `2026-02-08T22:25:49+08:00`  
**Dimensions**: `performance`, `implementation`, `architecture`, `decision`

---

## User Context

**Focus Areas**: 代码技术优化、架构优化、运行时配置优化、数据通路优化、UI 渲染优化  
**Analysis Depth**: `full`  
**Hard Constraints**: 不减少功能；service 需长时间稳定运行；desktop 保持现有体验

---

## Discussion Timeline

### Round 1 - Initial Understanding (2026-02-08 22:25 +08:00)

#### Topic Analysis

基于主题“service 长时间运行约 170 MB+、desktop 约 80 MB”：

- **Primary dimensions**:
  - `implementation`: 高频采集循环、对象分配、序列化与集合重建。
  - `architecture`: 采集层、传输层、存储层、展示层之间的数据形态与频率耦合。
  - `performance`: 常驻内存（steady-state）与峰值内存（burst）共同优化。
  - `decision`: 在“无功能删减”前提下做分层降本（先低风险，再中风险，再架构演进）。
- **Initial scope**:
  - Service：采集循环、GPU/系统指标提供者、SignalR 广播、EF Core 聚合任务、SQLite 参数、GC 参数。
  - Desktop：SignalR 消息反序列化、ViewModel 列表更新策略、WPF 定时器/动画、内嵌 WebServer（YARP）开销。
- **Key questions to explore**:
  - 在不降功能下，哪些分配是“可压缩格式”问题，哪些是“采样节奏”问题？
  - 哪些点会造成长期运行后的内存抬升（缓存未回收、集合重建频繁、聚合全量加载）？
  - 哪些优化可以先做且风险最低，并能快速验证收益？

#### Exploration Results (2026-02-08 22:25-22:35 +08:00)

**Sources Analyzed**:

- 代码：
  - `XhMonitor.Service/Worker.cs`
  - `XhMonitor.Service/Core/PerformanceMonitor.cs`
  - `XhMonitor.Service/Core/ProcessScanner.cs`
  - `XhMonitor.Service/Workers/AggregationWorker.cs`
  - `XhMonitor.Service/Data/Repositories/MetricRepository.cs`
  - `XhMonitor.Core/Services/LibreHardwareManager.cs`
  - `XhMonitor.Core/Providers/SystemMetricProvider.cs`
  - `XhMonitor.Core/Providers/GpuMetricProvider.cs`
  - `XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
  - `XhMonitor.Desktop/FloatingWindow.xaml.cs`
  - `XhMonitor.Desktop/Services/WebServerService.cs`
  - `XhMonitor.Desktop/Services/SignalRService.cs`
- 运行日志：
  - `XhMonitor.Service/service-out.log`
- 官方文档：
  - [.NET GC runtime config](https://learn.microsoft.com/dotnet/core/runtime-config/garbage-collector)
  - [ASP.NET Core memory](https://learn.microsoft.com/aspnet/core/performance/memory)
  - [SignalR configuration](https://learn.microsoft.com/aspnet/core/signalr/configuration)
  - [SignalR scale and memory note](https://learn.microsoft.com/aspnet/core/signalr/scale)
  - [EF Core efficient querying](https://learn.microsoft.com/ef/core/performance/efficient-querying)
  - [SQLite PRAGMA](https://sqlite.org/pragma.html)

**Key Findings**:

1. Service 的队列不是主要泄漏点：`BoundedChannel(1)` 已限制了快照堆积，但每轮构造的对象/字典仍会造成持续分配与 GC 压力。  
2. 高频采集是当前主成本：进程采集默认 3 秒、系统采集 1 秒；每轮都有 JSON 序列化、对象映射、推送。  
3. `AggregationWorker` 按分钟窗口使用 `ToListAsync` 全量读取再分组，窗口较大时会产生明显瞬时内存峰值。  
4. `ProcessScanner` 每轮遍历进程并读取命令行，字符串分配开销不可忽略，尤其是监控目标较多时。  
5. `LibreHardwareManager` 每轮快照会构造传感器列表，系统/GPU/磁盘网络指标采集链路存在“高频 + 多对象”特征。  
6. Desktop `FloatingWindowViewModel` 在收到进程消息后存在排序与集合重建，UI 线程频繁分配和绑定刷新会推高常驻内存和 CPU。  
7. Desktop 内嵌 WebServer（YARP）本身会带来基础内存占用；在“桌面 + 本地代理”模式下，80 MB 基线并不异常，但仍有可压缩空间。  
8. 现有 runtime 仅设置 `HeapHardLimit=512MB`，缺少更细粒度 GC 调优策略（如延迟模式切换、服务器/工作站 GC 选择验证）。  
9. 数据库存储链路已包含定期清理和 `VACUUM`，但 SQLite 运行期缓存和 checkpoint 节奏仍可进一步控制。

**Points for Discussion**:

1. 是否接受“分层采样节奏”策略（静态 + 动态）：稳定进程低频，活跃进程高频。  
2. 是否允许将指标通路改为更紧凑的结构（减少每进程字典对象、字段按位压缩或结构化数组）。  
3. 是否可将聚合逻辑改为分批流式聚合，避免单次窗口全量拉取。  
4. 是否接受 Desktop UI 改为增量 diff 更新（减少整表排序与重建）。  
5. 是否优先做“可观测性先行”：先接入 `dotnet-counters/gcdump` 建立基线再分阶段优化。

**Open Questions**:

- 线上长期监控的典型“目标进程数”是多少（5、20、50+）？
- service 端是否允许把部分指标改为“变化触发上报”而非“固定频率全量上报”？
- desktop 是否必须始终启用内嵌 WebServer，还是可按场景懒启动？
- 当前环境是否有 CPU 上限或节能策略，影响采样间隔上调空间？

#### Next Steps

- 进入 Round 2：根据你的优先级（代码级/架构级/运行时级）进一步下钻。
- 生成“低风险改造包”与“中期架构包”清单，含收益、风险、验证指标。

### Round 2 - Discussion (2026-02-08 22:50 +08:00)

#### User Input

用户选择：`1`（保持当前方向，输出 `Phase A（2-3 天）` 低风险改造清单，要求具体到文件和改动点）。

#### Updated Understanding

基于本轮选择，Phase A 目标收敛为：

- 优先做 **低风险、可灰度、可快速回滚** 的优化，避免一次性大改。
- 先降低分配率和瞬时峰值，再做更大范围架构演进（作为 Phase B）。
- 每项改动必须配套验证指标（RSS、alloc rate、P95 延迟），防止“省内存但伤性能”。

#### Phase A Package (2-3 天)

1. **A1 - Service 快照构建减分配（高优先）**
   - 文件：
     - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Worker.cs`
   - 改动点：
     - `SendProcessDataAsync`（约 `:280`）：将 `Select + ToDictionary + ToList` 改为预分配容量的手工循环，减少 LINQ 临时对象。
     - `RunProcessPushLoopAsync`（约 `:337`）：复用上一步构建结果，避免再次投影构造匿名对象集合。
   - 预期收益：降低采集链路分配率，减轻 Gen0/Gen1 压力。
   - 风险：低（不改协议字段，不改业务含义）。

2. **A2 - ProcessScanner 命令行缓存（高优先）**
   - 文件：
     - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Core/ProcessScanner.cs`
   - 改动点：
     - `ProcessSingleProcess`（约 `:174`）：为 `ProcessCommandLineReader.GetCommandLine(pid)` 增加短 TTL 缓存（建议 20-30 秒）。
     - `ScanProcesses`（约 `:130`）：每轮扫描后清理不存在 PID 的缓存项，防止缓存膨胀。
   - 预期收益：减少命令行字符串重复分配与系统调用次数。
   - 风险：低（缓存仅优化读取路径，不改变过滤逻辑）。

3. **A3 - 聚合查询无跟踪化（高优先）**
   - 文件：
     - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Workers/AggregationWorker.cs`
   - 改动点：
     - `AggregateRawToMinuteAsync`（约 `:61`）、`AggregateMinuteToHourAsync`（约 `:177`）、`AggregateHourToDayAsync`（约 `:232`）中所有窗口读取查询补充 `AsNoTracking()`。
   - 预期收益：减少 EF 跟踪对象保留，降低分钟聚合周期峰值内存。
   - 风险：低（读取场景不依赖实体跟踪）。

4. **A4 - SignalR 缓冲与消息上限治理（中高优先）**
   - 文件：
     - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Program.cs`
     - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/appsettings.json`
   - 改动点：
     - `AddSignalR`（约 `:263`）增加 `MaximumReceiveMessageSize` 配置化。
     - `MapHub`（约 `:341`）配置 `ApplicationMaxBufferSize` 与 `TransportMaxBufferSize`。
     - `appsettings.json` 新增 `SignalR` 配置节（默认给保守但不影响功能的阈值）。
   - 预期收益：削减传输层缓冲导致的瞬时内存峰值。
   - 风险：中低（阈值需与当前 payload 实测匹配）。

5. **A5 - Desktop SignalR 反序列化减分配（高优先）**
   - 文件：
     - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/Services/SignalRService.cs`
   - 改动点：
     - 各 `On<JsonElement>` 回调（约 `:50-110`）将 `JsonSerializer.Deserialize<T>(data.GetRawText(), ...)` 改为 `data.Deserialize<T>(...)`，避免中间字符串分配。
   - 预期收益：降低 UI 侧消息解码分配。
   - 风险：低（数据结构与协议不变）。

6. **A6 - Desktop 列表刷新轻量节流（中优先）**
   - 文件：
     - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
   - 改动点：
     - `OnProcessDataReceived`（约 `:271`）与 `OnMetricsReceived`（约 `:344`）提取共享刷新逻辑。
     - 将同步 `Dispatcher.Invoke` 改为 `Dispatcher.BeginInvoke`，并对进程列表重排做短窗口合并（建议 100-200 ms，可配置开关）。
   - 预期收益：降低 UI 线程抖动和集合重排带来的分配。
   - 风险：中（需要验证视觉实时性不回退）。

#### Test & Validation Plan (Phase A)

- Service 测试补充：
  - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Tests/Services/WorkerTests.cs`：新增快照 payload 字段一致性验证。
  - 新增 `ProcessScanner` 缓存单测（建议文件：`C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Tests/Services/ProcessScannerTests.cs`）。
  - 新增 `AggregationWorker` 查询策略单测（建议文件：`C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Tests/Services/AggregationWorkerTests.cs`）。
- Desktop 测试补充：
  - 新增 `SignalRService` 反序列化路径单测（建议文件：`C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop.Tests/SignalRServiceTests.cs`）。
  - 新增 `FloatingWindowViewModel` 刷新节流行为单测（建议文件：`C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop.Tests/FloatingWindowViewModelTests.cs`）。
- 验收指标：
  - Service RSS steady-state：`170MB+ -> 150MB 以下`（同负载 30-60 分钟）。
  - Desktop RSS steady-state：`80MB -> 70MB 左右`。
  - alloc rate 至少下降 `15%`；UI P95 延迟不劣化。

### Round 3 - Implementation (2026-02-08 23:15 +08:00)

#### User Input

用户要求：开始实现。

#### Implemented Changes

1. `A1` 快照构建减分配（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Worker.cs`
   - 改动：
     - `SendProcessDataAsync` 改为预分配 + 手工循环构建 `ProcessMetricSnapshot`。
     - `ReceiveProcessMetadata` 改为强类型 envelope + 预分配列表。
     - `RunProcessPushLoopAsync` 直接推送快照列表，移除二次 `Select(...).ToList()` 投影。
2. `A2` 命令行缓存（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Core/ProcessScanner.cs`
   - 改动：
     - 新增 PID 级命令行缓存（TTL 30 秒）。
     - 每轮扫描后清理过期或已退出 PID 的缓存项。
3. `A3` 聚合读取无跟踪（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Workers/AggregationWorker.cs`
   - 改动：
     - 各窗口查询补充 `AsNoTracking()`，减少 EF 跟踪对象驻留。
4. `A4` SignalR 缓冲治理（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Program.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/appsettings.json`
   - 改动：
     - 新增配置项 `MaximumReceiveMessageSize`、`ApplicationMaxBufferSize`、`TransportMaxBufferSize`。
     - 在 `AddSignalR` 与 `MapHub` 应用配置值。
5. `A5` Desktop 反序列化减分配（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/Services/SignalRService.cs`
   - 改动：
     - `JsonSerializer.Deserialize(...GetRawText())` 改为 `JsonElement.Deserialize<T>()`，去掉中间字符串分配。
6. `A6` UI 轻量节流（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
   - 改动：
     - 使用 `DispatcherTimer(150ms)` 合并高频进程刷新。
     - 事件处理从 `Dispatcher.Invoke` 调整为 `BeginInvoke`，减少同步阻塞。
     - 提取统一刷新入口 `QueueProcessRefresh/ApplyPendingProcessRefresh`。

#### Validation Results

- 执行：`dotnet test C:/ProjectDev/project/xinghe/xhMonitor/xhMonitor.sln`
- 结果：
  - `XhMonitor.Tests`：`169 passed, 0 failed`
  - `XhMonitor.Desktop.Tests`：`15 passed, 0 failed`
- 结论：实现已通过现有自动化测试，进入运行期指标验证阶段。

### Round 4 - Runtime Sampling (2026-02-09 16:10 +08:00)

#### User Input

用户要求继续跑 30 分钟采样；后续因误操作关闭采样窗口，要求直接查看当前结果。

#### Sampling Status

- 采样目录：
  - `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/runtime-sampling-20260209-155509`
- 实际采样时长：
  - `2026-02-09T15:55:10+08:00` 到 `2026-02-09T16:02:28+08:00`，约 `438s`（计划 `1800s` 的 `24.17%`）。
- 样本数：
  - Service `88` 条，Desktop `88` 条（无丢样）。

#### Partial Results (已采样区间)

- Service（PID `35884`）：
  - WorkingSet MB：`min 155.078 / avg 180.643 / max 191.305`
  - Private MB：`min 95.531 / avg 103.412 / max 108.98`
  - GC Heap MB：`min 22.755 / avg 37.593 / max 56.494`
  - Allocation Rate（B/5s）：`min 18,767,400 / avg 32,635,855.906 / max 49,267,424`
- Desktop（PID `18184`）：
  - WorkingSet MB：`min 147.457 / avg 188.701 / max 222.418`
  - Private MB：`min 153.008 / avg 155.092 / max 162.309`
  - GC Heap MB：`min 4.506 / avg 13.542 / max 23.301`
  - Allocation Rate（B/5s）：`min 334,688 / avg 712,324.93 / max 10,805,664`

#### Artifacts

- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/runtime-sampling-20260209-155509/process-memory-samples.csv`
- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/runtime-sampling-20260209-155509/service-runtime-counters.csv`
- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/runtime-sampling-20260209-155509/desktop-runtime-counters.csv`
- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/runtime-sampling-20260209-155509/summary.partial.json`

### Round 5 - Before/After Comparison (2026-02-09 16:37 +08:00)

#### User Input

用户提供优化前安装目录：`C:/my_program/XhMonitor`，要求直接对比优化前后结果。

#### Comparison Setup

- 优化前：启动 `C:/my_program/XhMonitor/Desktop/XhMonitor.Desktop.exe`（自动拉起同目录 service），采样 `480s`。
- 优化后：启动当前工作区构建 `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/bin/Debug/net8.0-windows/XhMonitor.Desktop.exe`，采样 `480s`。
- 同口径指标：`WorkingSet`、`PrivateMemory`（5 秒间隔）。

#### Comparison Results

- Service：
  - WorkingSet 平均：`233.468 -> 191.392 MB`（`-18.02%`）
  - WorkingSet 峰值：`262.766 -> 200.379 MB`（`-62.387 MB`）
  - Private 平均：`138.474 -> 109.315 MB`（`-21.06%`）
- Desktop：
  - WorkingSet 平均：`239.558 -> 221.960 MB`（`-7.35%`）
  - WorkingSet 峰值：`250.121 -> 234.348 MB`（`-15.773 MB`）
  - Private 平均：`142.953 -> 134.156 MB`（`-6.15%`）

#### Notes

- 优化前 service 拒绝 `dotnet-counters`（权限拒绝），因此 service 的 GC/alloc 无法做前后同口径对比。
- 进程级内存（WorkingSet/Private）已能确认“优化有效，且 service 改善显著”。

#### Artifacts

- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/compare-before-20260209-161838/summary.json`
- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/compare-after-20260209-162821/summary.json`
- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/compare-summary-20260209.json`
- `C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/compare-report-20260209.md`

### Round 6 - Implementation Continuation (2026-02-09 18:38 +08:00)

#### User Input

用户要求继续执行剩余项：`2（参数可调）`、`3（补单测）`、`4（Phase B 聚合流式化）`。

#### Implemented Changes

1. `2` Desktop 参数配置化（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/Configuration/UiOptimizationOptions.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/appsettings.json`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/App.xaml.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
   - 结果：节流开关与间隔改为配置驱动（默认 `150ms`），并增加归一化与可测逻辑。
2. `3` 单测补齐（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop.Tests/SignalRServiceTests.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Desktop.Tests/FloatingWindowViewModelThrottleTests.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Tests/Services/ProcessScannerTests.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Tests/Services/AggregationWorkerTests.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/AssemblyInfo.cs`
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Core/ProcessScanner.cs`
   - 结果：补齐关键优化点回归覆盖，并通过 `InternalsVisibleTo` 暴露必要内部测试面。
3. `4` Phase B 聚合流式化（已实现）
   - `C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/Workers/AggregationWorker.cs`
   - 结果：Raw→Minute、Minute→Hour、Hour→Day 全部改为按 `Id` 分批读取并增量聚合，避免窗口全量 `ToListAsync` 引发峰值抬升。

#### Validation Results

- 执行：`dotnet test C:/ProjectDev/project/xinghe/xhMonitor/xhMonitor.sln`
- 结果：
  - `XhMonitor.Tests`：`171 passed, 0 failed`
  - `XhMonitor.Desktop.Tests`：`24 passed, 0 failed`
- 结论：`2/3/4` 已完成并通过回归，当前可进入下一轮长时间运行观测（30-60 分钟）以确认稳态收益。

---

## Current Understanding

### What We Established

- 当前 service 170 MB+ 更可能是“高频采集 + 多对象分配 + 周期性峰值任务”的组合，不是单点无限队列泄漏。  
- 当前 desktop 80 MB 左右包含 WPF 基线、数据绑定和本地服务承载成本，存在优化空间但需避免 UI 功能回退。  
- 该问题应同时从 **分配率（allocation rate）**、**保留对象（retained set）**、**峰值任务（burst workload）** 三个维度治理。

### What Was Clarified/Corrected

- ~~优先怀疑 Channel 堆积~~ → 已确认 `BoundedChannel(1)` 使该风险较低。  
- ~~只需要调 GC 参数~~ → GC 仅是末端调优，主收益仍来自采样、数据模型和聚合方式优化。

### Key Insights

- 先做数据通路“减分配”与“减复制”通常比直接调 GC 更稳定。  
- 聚合任务与 UI 列表刷新是两个高价值切入点：改造成本可控、收益可观。  
- 需要用同一套观测指标衡量优化效果，防止“内存下降但 CPU 或延迟恶化”。
- 用户已明确优先级：先输出并执行 `Phase A` 低风险包，再进入更深层架构改造。
- `Round 6` 已将 Phase B 的聚合流式化落地，峰值控制能力从“查询侧无跟踪”升级为“读取侧分批 + 聚合侧增量”。

---

## Session Statistics

- **Total Rounds**: `6`（进行中）  
- **Duration**: `~20 hours`（跨 `2026-02-08` 到 `2026-02-09`）  
- **Sources Used**: codebase exploration、runtime log、official docs、before/after sampling  
- **Artifacts Generated**: `discussion.md`, `explorations.json`, `conclusions.json`, `phase-a-plan.md`, `compare-summary-20260209.json`, `compare-report-20260209.md`
