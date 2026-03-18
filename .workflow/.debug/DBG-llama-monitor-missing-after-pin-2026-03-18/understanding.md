# Understanding Document

**Session ID**: DBG-llama-monitor-missing-after-pin-2026-03-18  
**Bug Description**: 桌面端右键钉住 llama 进程后，后续即使满足 llama 指标采集条件，也可能看不到 llama 运行指标；怀疑与启动时未运行、长时间未监控、重新钉住或 web 端订阅是否能唤醒有关。  
**Started**: 2026-03-18T00:00:00+08:00

---

## Exploration Timeline

### Iteration 1 - Initial Exploration (2026-03-18 00:00 +08:00)

#### Current Understanding

- Desktop 右键钉住逻辑只保存 `_pinnedProcessIds`，即当前进程的 `ProcessId`。
- Desktop 收起时使用 `lite` 订阅；服务端只给该连接推送 `Top5 + pinnedProcessIds` 对应的进程快照。
- `llama-server` 指标采集并不是“只在启动时注册一次”，Service 会持续扫描进程并持续运行 llama 高频 enrich loop。

#### Evidence from Code Search

- `XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
  - `TogglePin()` 只操作 `_pinnedProcessIds`，没有按进程身份持久化。
  - `SyncProcessMetricsSubscription()` 在收起状态切到 `Lite`。
  - `SyncPinnedProcessRows()` / `SyncProcessIndex()` 遇到快照里不存在的 PID，会直接从 `_pinnedProcessIds` 移除。
- `XhMonitor.Service/Worker.cs`
  - `BuildProcessPushItem()` 对 `lite` 订阅只选 `Top5 + subscription.PinnedProcessIds`。
  - `RunLlamaMetricsLoopAsync()` 独立于 UI 订阅持续 enrich `_latestProcessMetrics`。
- `XhMonitor.Service/Hubs/MetricsHub.cs`
  - SignalR 订阅是“按连接”管理，web 和 desktop 互不唤醒对方的订阅模式。

#### Hypotheses Generated

- H1：问题根因是 pin 只绑定 PID；`llama-server` 重启后 PID 变化，desktop 仍然订阅旧 PID，导致 `lite` 模式拿不到新进程指标。
- H2：问题根因是 monitor 启动时没有 llama，导致 Service 后续不再扫描或不再 enrich。
- H3：web 端开始获取进程数据后，会自动让 desktop 的 `lite` 订阅也收到该 llama 进程的指标。

---

### Iteration 2 - Static Evidence Analysis (2026-03-18 00:10 +08:00)

#### Analysis Results

- **H1: CONFIRMED**
  - `TogglePin()` 仅保存 `ProcessId`。
  - `SyncPinnedProcessRows()` / `SyncProcessIndex()` 会在旧 PID 消失时直接清掉 pin。
  - `BuildProcessPushItem()` 的 `lite` 快照只按当前 pinned PID 补充，不会按名称或命令行重新绑定新 PID。
- **H2: REJECTED**
  - `SendProcessDataAsync()` 按固定周期持续执行。
  - `RunLlamaMetricsLoopAsync()` 也持续执行，不依赖“启动瞬间就存在 llama”。
  - 因此不是“启动时没跑 llama，时间一长就永远不监控”。
- **H3: REJECTED**
  - `MetricsHub` 的订阅按连接维护。
  - web 端 `full` 订阅不会修改 desktop 连接的 `lite`/`full` 状态，也不会替 desktop 自动补 pin。

#### Corrected Understanding

- ~~llama 监控可能只在启动时注册一次~~ → Service 会持续扫描和 enrich，问题不在后台采集 loop 停掉。
- ~~web 端开始获取进程数据可以顺带唤醒 desktop 的 llama 监控~~ → web 只会让自己的连接拿到 `full` 快照，不会改变 desktop 连接的筛选条件。

#### Root Cause Identified

**根因**：Desktop 的 pin 是“PID 级绑定”，不是“进程身份级绑定”。`llama-server` 一旦重启，新 PID 与旧 pin 失配；在桌面端收起后的 `lite` 模式下，服务端只会推送 `Top5 + 已钉住 PID`，所以新 llama 进程如果不在 Top5，就不会进入桌面端指标列表。

---

## Current Consolidated Understanding

### What We Know

- Service 端的 llama 指标采集没有因为“启动时未运行 llama”而永久失效。
- Desktop 收起时是 `lite` 订阅，是否能看到 llama 主要取决于：
  - 该 llama 进程是否在 Top5；
  - 或它的当前 PID 是否仍在 `_pinnedProcessIds` 中。
- 当前 pin 逻辑无法在进程重启后把“旧 PID 的 pin”自动迁移到“新 PID 的同一进程”。

### What Was Disproven

- ~~启动时没跑 llama，时间长了 Service 就不再监控~~
- ~~web 端开始获取进程数据会自动唤醒 desktop 的 llama pin 监控~~

### Current Investigation Focus

为 Desktop 增加“按进程身份重绑 pin 到新 PID”的能力，使 `llama-server` 重启后仍可自动恢复 `lite` 指标订阅。

### Iteration 3 - Resolution (2026-03-18 00:40 +08:00)

#### Fix Applied

- 修改 `XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
  - 为 pin 增加 `PinnedProcessBinding`，保留 `ProcessName + DisplayName + CommandLine + BoundProcessId`。
  - 当旧 PID 从快照里消失时，不再直接忘掉 pin 身份，而是仅解绑当前 PID。
  - 在 `SyncProcessMeta()` 和进程刷新后尝试把 pin 自动重绑到匹配的新 PID。
  - 重绑成功后立即调用 `UpdatePinnedProcessIdsAsync()`，让 desktop 的 `lite` 订阅切到新 PID。
- 修改 `XhMonitor.Desktop.Tests/FloatingWindowViewModelCollapsedRefreshTests.cs`
  - 新增测试覆盖“llama 旧 PID 消失后，metadata 到达，新 PID 自动接管 pin”。

#### Verification Results

- `dotnet test XhMonitor.Desktop.Tests/XhMonitor.Desktop.Tests.csproj --filter "FullyQualifiedName~FloatingWindowViewModelCollapsedRefreshTests"`：通过。
- `dotnet test XhMonitor.Desktop.Tests/XhMonitor.Desktop.Tests.csproj --filter "FullyQualifiedName~FloatingWindowViewModel"`：通过。

#### Lessons Learned

1. `lite` 模式下只传 PID 的 pin，对会频繁重启的本地服务类进程不够稳。
2. `ReceiveProcessMetadata` 是天然的“进程重生通知”，适合做 PID 重绑。
3. web 端 `full` 订阅不应该承担修复 desktop pin 丢失的职责，问题应在 desktop 自身闭环。

### Iteration 4 - Port Close Logic Review (2026-03-18 01:05 +08:00)

#### New Evidence

- `RunLlamaMetricsLoopAsync()` 每秒取当前 `_latestProcessMetrics` 快照重新尝试 enrich，不存在“端口失败一次就停掉该进程监控”的状态机。
- `FetchMetricsTextAsync()` 访问失败时只返回 `null`，`EnrichSingleAsync()` 直接 `return`，不会把该 PID 标记为禁用。
- 真正的状态清理只发生在：
  - 该 PID 不再出现在当前进程快照中时，`LlamaServerMetricsEnricher.CleanupStates()` 清理 `_states`；
  - `Worker.PrepareLlamaRealtimeUpdates()` 在 live llama PID 集合里清理 `_llamaLastPublished`。

#### Corrected Understanding

- ~~端口关闭后可能触发了某个“停止后不再恢复”的逻辑~~ → 当前代码里没有这种永久停监控逻辑；失败只影响当前一次 `/metrics` 拉取。

#### Current Investigation Focus

如果参数确实不变而且重启后长期不恢复，那么更可能是：

1. 新启动的 llama 进程没有重新进入 `SendProcessDataAsync()` 的进程快照；
2. 或者进程已进入快照，但新的 `/metrics` 拉取持续失败。

### Iteration 5 - Runtime Diagnostic Instrumentation (2026-03-18 01:20 +08:00)

#### Fix Applied

- 在 `XhMonitor.Service/Core/ProcessScanner.cs` 添加 llama 定向日志：
  - 命令行为空/读取失败时记录 PID；
  - 扫描命中后记录 `MatchedKeywords`、`ShouldFilter` 和 `CommandLine`。
- 在 `XhMonitor.Service/Core/LlamaServerMetricsEnricher.cs` 添加 llama 定向日志：
  - 未解析到 metrics 端口时记录 PID 和命令行；
  - 请求 `/metrics` 返回非 2xx 或请求失败时记录端口。

#### Verification Results

- `dotnet test XhMonitor.Tests/XhMonitor.Tests.csproj --filter "FullyQualifiedName~ProcessScannerTests|FullyQualifiedName~LlamaServerMetricsParsingTests"`：通过。
