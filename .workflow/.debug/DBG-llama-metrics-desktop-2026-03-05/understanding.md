# Understanding Document

**Session ID**: DBG-llama-metrics-desktop-2026-03-05  
**Bug Description**: 日志中 llama /metrics 返回在变（例如 `llamacpp:n_decode_total`），但 Desktop 的 llama 指标不实时变化；只有 AI 回复完成时才跳一下。  
**Started**: 2026-03-05T00:00:00+08:00  

---

## Exploration Timeline

### Iteration 1 - Initial Exploration (2026-03-05)

#### Current Understanding

- Desktop 展示的 llama 指标来自进程指标字典中的 `llama_*` 键（如 `llama_gen_tps_compute` / `llama_out_tokens_total`），并非直接展示原始 Prometheus 指标名（如 `llamacpp:n_decode_total`）。
- Service 端有独立的 llama 高频采集循环：`Worker` 中调用 `LlamaServerMetricsEnricher.EnrichAsync()` 拉取 `/metrics`，再根据“值是否变化”决定是否通过 SignalR 推送最新快照。
- 当前日志里变化的指标（`llamacpp:n_decode_total`）并未被 `LlamaPrometheusTextParser` 解析并写入 `ProcessMetrics.Metrics`，因此：
  - `PrepareLlamaRealtimeUpdates()` 的变化检测可能判断“无变化” → `shouldPush=false` → Desktop 不会收到新一帧。
  - Desktop 的 `LlamaMetricsText` 由 `Port/Gen/Busy/Req/Out` 组合而成；若这些字段本身不变，则即使推送也不会触发 UI 文本变化。

#### Evidence from Code Search

- `XhMonitor.Service/Core/LlamaServerMetricsEnricher.cs`：
  - `LlamaPrometheusTextParser` 仅解析：
    - `llamacpp:tokens_predicted_total`
    - `llamacpp:tokens_predicted_seconds_total`
    - `llamacpp:requests_processing`
    - `llamacpp:requests_deferred`
  - 未解析 `llamacpp:n_decode_total`。
- `XhMonitor.Service/Worker.cs`：
  - llama loop：采集后调用 `PrepareLlamaRealtimeUpdates()`，只有 `TryGetLlamaValuesChanged()` 返回 true 才 `EnqueueProcessSnapshot()` 推送给 Desktop。
  - 变化检测字段来自 `LlamaRealtimeValues(Port, GenTpsCompute, BusyPercent, RequestsProcessing, RequestsDeferred, OutTokensTotal)`。
- `XhMonitor.Desktop/ViewModels/FloatingWindowViewModel.cs`：
  - `ProcessRowViewModel.UpdateLlamaMetrics()` 仅使用 `llama_port/gen/busy/req/out_tokens_total` 生成 `LlamaMetricsText`。

#### Hypotheses Generated

- **H1**：Desktop 未订阅到包含 llama 指标的推送事件（订阅缺失或事件名不一致）。  
  - 证据：`FloatingWindowViewModel` 已订阅 `ProcessDataReceived`，但需确认实际 UI 展示来源。
- **H2**：Service 的 llama 高频采集 loop 有“变化检测 gating”，而日志变化的指标未进入 gating 比较字段，导致 `shouldPush=false`。  
  - 证据：`TryGetLlamaValuesChanged()` 只比较 `LlamaRealtimeValues` 的 6 个字段。
- **H3**：Desktop 展示的字段本身只会在 AI 回复完成时变化（例如 `llama_out_tokens_total` 只在结束时更新），所以看起来“只跳一下”。  
  - 证据：日志里 `llamacpp:tokens_predicted_total` 在多次采集间保持不变，而 `llamacpp:n_decode_total` 在变。

---

## Current Consolidated Understanding

### What We Know
- 日志“在变”的指标与 Desktop 展示/推送 gating 关注的指标不是同一组。
- 当前管道中缺少对 `llamacpp:n_decode_total`（或其他实时变化 gauge）的映射与展示。

### Current Investigation Focus
- 将 `llamacpp:n_decode_total` 纳入解析→缓存→变化检测→推送，并在 Desktop 文本中展示，验证 UI 能随采样频率更新。

