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

### Iteration 6 - Service Log & DB Correlation (2026-03-19 14:25 +08:00)

#### Log Analysis Results

- 在 `XhMonitor.Service/logs/xhmonitor-20260319.log` 中，没有出现以下定向日志：
  - `llama-server 进程命令行为空，跳过采集`
  - `llama-server 进程命令行读取失败，跳过采集`
  - `llama-server 进程未解析到 metrics 端口，跳过 enrich`
  - `ShouldFilter=True`
- 同一份日志明确显示，Qwen35V 这条进程在多个新 PID 上都被 `ProcessScanner` 持续扫描到，且命令行完整、`ShouldFilter=false`：
  - `PID=49212`：首次工作 PID
  - `PID=47620`：第一次重启后的新 PID
  - `PID=212268`：第二次重启后的新 PID
- 日志中的失败只落在 `/metrics` HTTP 层，且仅看到：
  - `Port=1278, StatusCode=501`
  - `Port=1234, StatusCode=503`
- 没有看到 `Failed to enrich llama-server metrics for PID ...` 或 `Llama metrics loop failed`，说明这段时间里没有被日志捕获到的 loop 异常。

#### Database Evidence

- 对 `XhMonitor.Service/xhmonitor.db` 的 `ProcessMetricRecords` 进行对照后，结论更明确：
  - 旧 PID `49212` 在首条记录后，很快开始稳定写入 `llama_port=1234`，随后还能写入 `llama_req_processing=0`、`llama_out_tokens_total=0`。
  - 新 PID `47620` 与 `212268` 的主记录持续落库，`ProcessName`、`DisplayName`、`CommandLine` 都正常，但 `MetricsJson` 里长期没有任何 `llama_*` 字段，连 `llama_port` 都没有。
- `49212` 与 `47620` 的 `CommandLine` 文本完全一致，长度也一致（442），因此“新 PID 命令行内容不同，导致端口正则解析失败”这一分支不成立。

#### Live Endpoint Probe

- 当前时间直接访问：
  - `http://127.0.0.1:1234/metrics`
  - `http://127.0.0.1:21235/metrics`
- 两个端口都返回了符合当前 `LlamaPrometheusTextParser` 预期的 Prometheus 文本，包含：
  - `llamacpp:tokens_predicted_total`
  - `llamacpp:tokens_predicted_seconds_total`
  - `llamacpp:n_decode_total`
  - `llamacpp:requests_processing`
  - `llamacpp:requests_deferred`
- 这说明“指标文本格式与 parser 不兼容”在当前时刻看起来也不是问题。

#### Corrected Understanding

- ~~这次复现可能是新 PID 又被关键字过滤，或者命令行读取失败~~ → 在 `xhmonitor-20260319.log` 里，新 PID 被稳定扫描到，且命令行完整、`ShouldFilter=false`。
- ~~用户提到的 `cmd` 获取命令行失败，和当前 Service 日志应该是同一条链路~~ → 当前实现的 `ProcessCommandLineReader` 直接通过 `NtQueryInformationProcess + ReadProcessMemory` 读取 PEB，并不调用 `cmd.exe` / `wmic`。本次提供的 Service 日志里也没有出现“命令行为空 / 读取失败”的定向日志，因此这条现象要么来自别的日志源，要么发生在这份日志覆盖范围之外。
- ~~新 PID 没有 llama_*，主要是 `/metrics` 返回 503 导致样本为空~~ → 仅凭现有证据还不能这样下结论。因为按当前实现，只要新 PID 真正进入了 llama loop 并成功解析出端口，即使 `/metrics` 暂时失败，也应该有机会先建立 `llama_port` 缓存；但 `47620` / `212268` 连 `llama_port` 都没有。

#### Current Investigation Focus

问题已经缩小到 `ProcessScanner` 之后、`_llamaLastPublished` 之前的 runtime handoff：

1. 新 PID 已进入主采集链路并成功落库；
2. 但新 PID 没有进入 llama cache，因此后续主记录不会被 `ApplyLlamaCachedMetrics()` 回灌 `llama_port` / `llama_*`；
3. 这与旧 PID `49212` 的行为不同，说明“重启后的 PID 进入 llama loop”这一段存在运行时分叉。

#### Best Next Log Point

下一轮如果还要继续缩小范围，最有价值的不是再看 `ProcessScanner`，而是给 `RunLlamaMetricsLoopAsync()` 里的这几个点补首轮日志：

- `EnrichAsync(snapshot)` 前：打印 snapshot 中每个 `llama-server` 的 `PID + ProcessName + CommandLineLength`。
- `TryGetLlamaMetricsPort()` 成功后：打印 `PID + Port`，确认新 PID 是否真的在 loop 内解析出端口。
- `TryGetLlamaValuesChanged()` 首次写入 `_llamaLastPublished` 时：打印 `PID + Port + HasAnyLlamaSampleData`。
- `PrepareLlamaRealtimeUpdates()` 清理旧 cache 时：打印被移除的旧 PID，确认旧 PID 清理与新 PID 建 cache 是否发生了断档。

---

## Current Consolidated Understanding (Updated)

### What We Know

- 旧 PID 残留不是根因；旧 PID 消失后，状态会按 live PID 正常清理。
- `xhmonitor-20260319.log` 里，新 PID `49212 -> 47620 -> 212268` 都被主扫描稳定识别，且 `ShouldFilter=false`、命令行完整。
- `47620` / `212268` 的主记录持续落库，但没有任何 `llama_*` 字段；`49212` 则能正常出现 `llama_port=1234`，随后出现更多 llama 指标。
- 当前 `ProcessCommandLineReader` 不是通过 `cmd.exe` 读取命令行，因此“cmd 获取失败为空”不是当前代码的直接实现路径。

### What Was Disproven

- ~~新 PID 被关键字过滤掉了~~
- ~~这份 Service 日志里已经出现了命令行为空 / 读取失败~~
- ~~新旧 PID 的命令行内容不同，导致端口解析失败~~

### Current Investigation Focus

为什么重启后的新 PID 能进入 `SendProcessDataAsync()` 主快照，却没有进入 `RunLlamaMetricsLoopAsync()` 的有效 cache 建立路径。

### Remaining Questions

- 新 PID 在 llama loop 内看到的 `ProcessInfo.CommandLine` 是否仍然完整？
- 新 PID 在 llama loop 内是否实际命中了 `TryGetLlamaMetricsPort()`？
- 新 PID 的 `_llamaLastPublished` 首次写入是否被跳过，还是写入后又被清掉？

### Iteration 7 - Worker Runtime Instrumentation Added (2026-03-19 14:34 +08:00)

#### Fix Applied

- 修改 `XhMonitor.Service/Worker.cs`，补充了 llama loop 定向日志：
  - `llama loop 快照...`
    - 记录 `PID`、`DisplayName`、`CommandLineLength`
    - 记录 `CommandLinePortResolved`、`ResolvedPort`
    - 记录 `HasPortMetric`、`PortMetric`
    - 记录 `HasSampleData`、`LlamaMetricKeys`
  - `llama cache 首次写入...`
  - `llama cache 更新...`
    - 记录 `ChangedFields`
  - `llama realtime 当前仅建立 cache，暂无样本指标...`
  - `llama cache 清理旧 PID...`
  - `llama loop 快照移除旧 PID...`

#### Verification Results

- `dotnet test XhMonitor.Tests/XhMonitor.Tests.csproj --filter "FullyQualifiedName~WorkerTests|FullyQualifiedName~ProcessScannerTests|FullyQualifiedName~LlamaServerMetricsParsingTests"`：通过。

#### Expected Next Signal

下次复现时，只要看 `XhMonitor.Service/logs/xhmonitor-*.log` 里这几类新日志，就能立刻分出分支：

- `CommandLinePortResolved=true` 但 `HasPortMetric=false`
  - 说明问题在 `EnrichAsync()` 内部，端口解析结果没有真正落进 `process.Metrics`
- `HasPortMetric=true` 但没有 `llama cache 首次写入`
  - 说明问题在 `PrepareLlamaRealtimeUpdates()` / `TryExtractLlamaRealtimeValues()`
- 有 `llama cache 首次写入`，但下一轮主记录仍没有 `llama_port`
  - 说明问题在 `ApplyLlamaCachedMetrics()` 或主记录持久化链路

### Iteration 8 - Timeout Path Root Cause Confirmed (2026-03-19 15:05 +08:00)

#### New Evidence

- 对 `XhMonitor.Service/logs/xhmonitor-20260319.log` 继续按时间窗核对后，发现：
  - `14:39:37.476` 是最后一条 `llama metrics 请求返回非成功状态码。Port=1278, StatusCode=501`
  - 之后整份日志里再没有任何：
    - `llama metrics 请求...`
    - `llama loop 快照...`
    - `llama cache ...`
    - `llama loop 快照移除旧 PID...`
- 但同一时间窗里主采集链路持续正常运行：
  - `SendProcessDataAsync()` 仍然每隔约 `3 s` 采集、落库、推送
  - `ProcessScanner` 继续稳定扫描到 `PID=142084`，随后又扫描到 `PID=134812`
- 数据库也印证了这一点：
  - `212268` 在 `14:39:34` 之后不再有新 llama 主记录
  - `142084` / `134812` 的主记录持续出现，但 `llama_port` 始终为空
- 代码路径进一步确认：
  - `Program.cs` 为 `llama-metrics` `HttpClient` 配置了 `Timeout = 1.5 s`
  - `LlamaServerMetricsEnricher.FetchMetricsTextAsync()` 原实现对 `OperationCanceledException` 直接 `throw`
  - `Worker.RunLlamaMetricsLoopAsync()` 原实现对任何 `OperationCanceledException` 直接 `break`

#### Corrected Understanding

- ~~`RunLlamaMetricsLoopAsync()` 仍在持续跑，只是一直看不到新 PID~~
  → 更准确的情况是：loop 在 `14:39:37` 附近某次 `/metrics` 超时后，被当成“服务停止取消”直接退出了。
- ~~问题主要是新 PID 没切进 `_latestProcessMetrics`~~
  → 当前复现里，更直接的断点是：`llama` loop 自身已经退出，所以后续无论 `_latestProcessMetrics` 怎样更新，都不会再处理新 PID。

#### Root Cause Confirmed

**根因**：`llama` `/metrics` 请求超时会抛出 `TaskCanceledException / OperationCanceledException`。  
当前实现把这类超时当成真正的服务取消：

1. `FetchMetricsTextAsync()` 捕获 `OperationCanceledException` 后直接重新抛出；
2. `EnrichSingleAsync()` 继续向上抛；
3. `RunLlamaMetricsLoopAsync()` 捕获到 `OperationCanceledException` 后直接 `break`；
4. 整条 `llama` 高频 loop 永久退出；
5. 主采集链路继续运行，但再也不会为新的 llama PID 建立 `llama_port / llama_*`。

这与日志现象完全一致：

- 最后一条 `llama` 日志停在某次请求过程中；
- 之后只有主采集日志，没有任何 `llama loop` / `cache` 日志；
- 新 PID 虽然继续进入主采集和数据库，但永远拿不到 `llama_*`。

#### Fix Applied

- 修改 `XhMonitor.Service/Core/LlamaServerMetricsEnricher.cs`
  - 将“`HttpClient` 超时导致的 `OperationCanceledException`”单独识别为超时分支；
  - 记录 `llama metrics 请求超时。Port=..., TimeoutMs=...`；
  - 超时时返回 `null`，仅跳过当前端口，不再把超时向上冒泡成 loop 级取消。
- 修改 `XhMonitor.Service/Worker.cs`
  - `RunLlamaMetricsLoopAsync()` 仅在 `stoppingToken` 真正取消时才 `break`；
  - 对非服务停止的 `OperationCanceledException` 记一条 `Llama metrics loop canceled unexpectedly` 调试日志，避免静默退出。
- 修改 `XhMonitor.Tests/Services/LlamaServerMetricsParsingTests.cs`
  - 新增回归测试：`EnrichAsync_WhenMetricsRequestTimesOut_ShouldNotPropagateCancellation`

#### Verification Results

- `dotnet test XhMonitor.Tests/XhMonitor.Tests.csproj --filter "FullyQualifiedName~WorkerTests|FullyQualifiedName~ProcessScannerTests|FullyQualifiedName~LlamaServerMetricsParsingTests"`：通过（22 / 22）。

---

## Current Consolidated Understanding (Latest)

### What We Know

- Desktop 的旧 `pin -> PID` 问题已经单独修过，但这次 `Service` 端复现不是那个问题。
- 当前 `Service` 端的直接根因，不是关键字过滤，也不是命令行为空，更不是新 PID 没被扫描到。
- 当前复现中，真正发生的是：
  - 某次 `llama` `/metrics` 请求在重启窗口里超时；
  - 超时被误判为服务取消；
  - `RunLlamaMetricsLoopAsync()` 直接退出；
  - 后续新的 llama PID 再也没有机会进入 `llama cache`。

### What Was Disproven

- ~~新 PID 没进入主采集快照~~
- ~~新 PID 进入了 loop，但 `TryGetLlamaMetricsPort()` / cache 建立阶段分叉~~

### Current Fix Status

- 已补最小修复，防止单次 `/metrics` 超时杀死整条 `llama` loop。
- 已补 timeout 定向日志，下一次如果端口再次卡住，会直接在日志里体现为 `llama metrics 请求超时...`，而不是静默停更。

### Iteration 9 - Desktop CommandLine/DisplayName Missing After Restart (2026-03-19 15:25 +08:00)

#### New Evidence

- `FloatingWindowViewModel` 中，进程行主要由 `ProcessDataReceived -> QueueProcessRefresh -> SyncProcessRows()` 创建或更新。
- `ProcessRowViewModel.UpdateFrom(ProcessInfoDto)` 只会在 `dto.CommandLine` / `dto.DisplayName` 非空时覆盖现有值，本身不会把已有元数据擦成空。
- 但 `Worker.BuildProcessSnapshot()` 原实现构造的 `ReceiveProcessMetrics` 快照只包含：
  - `ProcessId`
  - `ProcessName`
  - `Metrics`
- 不包含：
  - `CommandLine`
  - `DisplayName`
- Desktop 侧 `ProcessInfoDto` 明明预留了 `CommandLine` / `DisplayName` 字段，因此新建 `ProcessRowViewModel(p)` 时，如果该行来自 `ProcessData` 而不是 `ProcessMeta`，就会天然拿到空命令行和空友好名。

#### Corrected Understanding

- ~~第二次打开 desktop 后命令行消失，是因为 `UpdateFrom()` 把 metadata 覆盖丢了~~
  → 更直接的问题是：`ProcessData` 主快照压根没有携带命令行和友好名称。
- ~~只能继续追 `ReceiveProcessMetadata` 为什么偶发没补上~~
  → 更稳的修法是让 `ReceiveProcessMetrics` 自带元数据，避免 UI 依赖两路异步消息拼装一条完整进程记录。

#### Fix Applied

- 修改 `XhMonitor.Service/Worker.cs`
  - `BuildProcessSnapshot()` 现在支持按 PID 选择性携带 metadata
  - 只有“新 PID / 元数据发生变化”的那一轮 `ProcessData` 才会顺带携带：
    - `HasMeta=true`
    - `CommandLine`
    - `DisplayName`
  - 稳定 PID 的常规高频快照仍然只推指标，避免每轮重复推整段命令行
- 修改 `XhMonitor.Desktop/Models/ProcessInfoDto.cs`
  - 新增 `HasMeta`
- 修改 `XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`
  - `ProcessRowViewModel` 新增显式状态 `HasMeta`
  - `ProcessData` 仅含指标时保持 `HasMeta=false`
  - `ProcessMeta` 或 piggyback metadata 到达后切换为 `HasMeta=true`
- 修改 `XhMonitor.Tests/Services/WorkerTests.cs`
  - 新增：
    - `DoneWhen_BuildProcessSnapshot_IncludesCommandLineAndDisplayNameForMetadataPids`
    - `DoneWhen_BuildProcessSnapshot_OmitsCommandLineAndDisplayNameForStablePids`
- 修改 `XhMonitor.Desktop.Tests/FloatingWindowViewModelCollapsedRefreshTests.cs`
  - 新增 `HasMeta` 状态流转测试：
    - 无 metadata 的 `ProcessData` 建行后保持 `HasMeta=false`
    - `ProcessMeta` 到达后切换为 `HasMeta=true`
    - piggyback metadata 的 `ProcessData` 可直接建立 `HasMeta=true`

#### Verification Results

- `dotnet test XhMonitor.Tests/XhMonitor.Tests.csproj --filter "FullyQualifiedName~WorkerTests|FullyQualifiedName~ProcessScannerTests|FullyQualifiedName~LlamaServerMetricsParsingTests"`：通过（24 / 24）。
- `dotnet test XhMonitor.Desktop.Tests/XhMonitor.Desktop.Tests.csproj --filter "FullyQualifiedName~FloatingWindowViewModelCollapsedRefreshTests"`：通过（4 / 4）。
