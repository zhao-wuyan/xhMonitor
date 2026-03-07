# Analysis Discussion

**Session ID**: ANL-llama-metrics-tooltips-2026-03-07  
**Topic**: llama 指标 UI 调整（隐藏 Dec；新增累计平均 Gen/Prompt 吞吐；Desktop + Web 悬浮提示 0.2s）  
**Started**: 2026-03-07T14:41:58+08:00  
**Dimensions**: implementation, ui-ux  
**Depth**: standard

## Analysis Context
- Focus areas: Service 指标采集映射；Desktop / Web 展示与交互（tooltip）
- Constraints: 隐藏 Dec 仅影响展示，Service 端仍保留采集与推送；新增指标尽量复用 llama.cpp Prometheus gauges

## Initial Questions
- 当前 UI 的 llama 指标从哪里来？哪些地方在显示 Dec？
- `llamacpp:predicted_tokens_seconds` / `llamacpp:prompt_tokens_seconds` 是否真实存在，语义是否为“累计平均 tokens/s”？
- 如何在 Desktop（WPF）与 Web（React）实现 200ms 延迟的悬浮提示？

## Initial Decisions
> **Decision**: 新增指标直接使用 llama.cpp 暴露的 gauges，而不是再在 Service 端二次派生计算。
> - **Context**: 用户描述的名字可能不精确，需要先确认 Prometheus 指标是否存在及语义。
> - **Options considered**:  
>   1) 通过 counters（tokens/seconds_total）自行计算累计平均 tokens/s；  
>   2) 直接读取 gauges（`llamacpp:*_tokens_seconds`）作为累计平均吞吐。
> - **Chosen**: 选项 2 — **Reason**: llama.cpp 已提供 gauges（语义即 average tokens/s），实现更直接且跨端一致。
> - **Impact**: Service 仅做“解析 + 写入 Metrics 字典 + 推送”，UI 只负责展示与 tooltip。

> **Decision**: Dec（`llama_decode_total`）只在 UI 隐藏，不改 Service/持久化/推送逻辑。
> - **Context**: 用户明确要求“只是隐藏，其他 server 端逻辑不变”。
> - **Options considered**: UI 隐藏 vs Service 不采集/不推送。
> - **Chosen**: UI 隐藏 — **Reason**: 风险最小，历史数据与诊断能力保留。
> - **Impact**: Desktop + Web 都不展示 Dec，但数据仍在 metrics 中。

---

## Discussion Timeline

### Round 1 - Exploration (2026-03-07T14:41:58+08:00)

#### Key Findings
- Service 端 llama 指标入口：`XhMonitor.Service/Core/LlamaServerMetricsEnricher.cs`，当前已解析 counters（`tokens_predicted_total` / `tokens_predicted_seconds_total`）与 `n_decode_total`，并派生 `llama_gen_tps_compute` / `llama_busy_percent` 等。  
- Prometheus 原始输出中已包含 gauges：  
  - `llamacpp:predicted_tokens_seconds`（Average generation throughput in tokens/s）  
  - `llamacpp:prompt_tokens_seconds`（Average prompt throughput in tokens/s）  
  证据：`.workflow/.analysis/ANL-llama-metrics-monitor-2026-03-05/llama-metrics-127.0.0.1-1234.txt`
- Web llama 指标展示点：`xhmonitor-web/src/components/ProcessList.tsx` 的展开行（当前仅 Port/Gen/Busy/Req/Out）。  
- Desktop llama 指标展示点：`XhMonitor.Desktop/FloatingWindow.xaml` 的 PinnedCardTemplate（绑定 `LlamaMetricsText`），当前包含 `Dec`。

#### Corrected Assumptions
- ~~用户写的 `llamacpp:predicted_tokens_seconds` / `llamacpp:prompt_tokens_seconds` 可能不存在~~ → 确认存在且为 gauges（累计平均 tokens/s 的口径更符合需求）。

#### Next Actions
- Service：解析并写入 `llama_gen_tps_avg`（from `llamacpp:predicted_tokens_seconds`）与 `llama_prompt_tps_avg`（from `llamacpp:prompt_tokens_seconds`），并纳入 Worker 的缓存/推送 gating。  
- UI：  
  - Desktop：隐藏 Dec；把 llama 指标拆成可 hover 的条目；tooltip 延迟 200ms。  
  - Web：在 Gen 左侧新增 GenAvg；同时新增 PromptAvg；为每个指标加 tooltip（延迟 200ms）。

