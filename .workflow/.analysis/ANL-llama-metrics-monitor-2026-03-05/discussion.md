# Analysis Discussion

**Session ID**: ANL-llama-metrics-monitor-2026-03-05  
**Topic**: 在 xhMonitor 中新增 llama.cpp（llama-server.exe）`/metrics` 运行监控：service 端数据结构 + 数据采集/存储 + web/desktop 展示，并为未来不同进程监控方式预留扩展能力。  
**Started**: 2026-03-05T09:32:14+08:00  
**Dimensions**: architecture, implementation, performance, decision  
**Depth**: standard  

## Analysis Context
- Focus areas: 服务端架构与扩展点、数据采集与存储、前端（web/desktop）展示交互、性能与资源占用
- Perspectives: Technical（默认单视角覆盖）
- Constraints: 最小资源占用、尽量复用现有采集/存储/推送能力、可扩展到多类型进程监控方式

## Initial Questions
- 现有项目的“进程列表/钉住进程信息/监控采集/历史曲线/数据库”分别由哪些模块负责？数据流怎么走？
- service 端是否已有通用“采集器/指标（metric）/采样周期/缓存/写库”框架？是否已有按 pid 维度的扩展字段？
- desktop 端“右键钉住进程信息”当前显示哪些信息？扩展区域如何渲染与刷新？是否已有 HTTP 拉取或与 service 的实时通道？
- web 端“单击进程名称展开行”现有实现与数据来源？是否已有二级行（sub-row）组件？
- llama.cpp 的 `/metrics` 具体暴露哪些指标？其中 token 速度是否是累计计数，需要基于差值计算？
- 需要存库的粒度（秒级/采样周期）与保留策略？是否已有时序类表/压缩策略？

## Initial Decisions
> **Decision**: 先用“最小侵入 + 可扩展”的方式落地：抽象 `ProcessMonitorProvider`（按进程类型/特征匹配）与 `MetricsCollector`（按 URL 拉取并解析），把 llama.cpp 当作第一种 provider；UI 层只消费统一的 `ModelPerfSnapshot`。  
> - **Context**: 需求不仅要做 llama.cpp，还明确要求为未来“多个类型进程不同监控方式”预留扩展能力，并且强调性能/内存占用。  
> - **Options considered**:  
>   1) desktop/web 各自直连 `127.0.0.1:port/metrics`（实现快但分散、不可复用、权限/跨域/代理问题多）；  
>   2) 仅 service 端采集 + 统一下发（更一致、易扩展、可控资源）；  
>   3) service 端按需采集（只有被展开/钉住才采集）+ 统一下发（在 2 的基础上更省资源）。  
> - **Chosen**: 3）service 端按需采集为主，必要时保留 desktop 本地直连作为兜底（后续评估）。  
> - **Impact**: 分析与设计会优先寻找现有“订阅/展开/钉住”事件通路，优先在 service 端集中实现采集、差值计算、写库与查询接口。

---

## Discussion Timeline

### Round 1 - Exploration (2026-03-05T09:32:14+08:00)

#### User Input
用户希望监控 llama.cpp `--metrics` 打开后的 `http://127.0.0.1:{port}/metrics`，desktop 在钉住进程信息区域展示关键指标；web 在进程行下展开一行展示相同指标。需要计算 tokens/s（基于差值），并存库用于后续性能曲线。强调最小资源占用、复用现有资源、并为未来多类型进程监控预留扩展。

#### Decision Log
> **Decision**: Round 1 先做“代码库探索 + 现状盘点”，不直接实现。  
> - **Context**: 需要先充分理解项目架构与现有数据流，避免重复造轮子或引入不一致的数据通道。  
> - **Options considered**: 直接设计新方案 vs. 先找现有监控/曲线/数据库/进程详情实现。  
> - **Chosen**: 先探索现有实现与可复用点。  
> - **Impact**: 接下来会重点定位：service 的进程信息模型与 API、desktop 的钉住/详情 UI、web 的列表展开 UI、数据库 schema 与写库逻辑。

> **Decision（建议，待确认）**: 持久化优先复用现有 `ProcessMetricRecords`，把 llama 监控指标写入 `MetricsJson`（新增若干 `llama_*` key），并把端口以一个数值指标（如 `llama_port`）保存。  
> - **Context**: 项目已有完整的“写库/聚合/清理/历史接口/实时推送”链路，复用可显著降低实现成本与运行时资源占用。  
> - **Options considered**:  
>   1) 新建 `ModelPerfRecords` 表（字段含 pid/port/modelName/params），并补齐聚合/清理/API/推送；  
>   2) 复用 `ProcessMetricRecords`（把模型性能当作额外指标键）。  
> - **Chosen**: 2）复用现有表（优先）。  
> - **Impact**: 不需要 DB migration 即可落地；后续若要独立“模型面板/曲线”再补充 UI 与（可选）指标元数据扩展。

#### Analysis Results
- **架构现状（高置信）**：
  - Service 端在 `Worker.SendProcessDataAsync` 中：`CollectAllAsync` → `SaveMetricsAsync`（SQLite）→ 通过 SignalR 推送 `ReceiveProcessMetrics`（仅数值字典）与 `ReceiveProcessMetadata`（命令行/友好名）。  
  - 进程扫描在 `ProcessScanner` 中完成，读取 `CommandLine`（30s 缓存）并用 `Monitor:Keywords` 过滤；友好名由 `ProcessNameResolver` 根据 `Monitor:ProcessNameRules` 生成。  
  - `appsettings.json` 已包含 llama-server 的友好名规则：能从 `-m/--model .../*.gguf` 路径提取模型目录名，格式为 `llama-server: {model}`。
- **UI 现状（高置信）**：
  - Desktop：右键进程行会调用 `TogglePin`，Pinned 卡片模板 `PinnedCardTemplate` 当前固定高度 28，仅显示 DisplayName + CPU/RAM/GPU/VRAM。  
  - Web：`ProcessList.tsx` 当前仅渲染单行表格，不支持“点击展开二级行”。
- **落地入口（高置信）**：
  - 若把 llama 的指标写回 `ProcessMetrics.Metrics`（字典），即可复用现有：写库（`ProcessMetricRecords.MetricsJson`）、聚合/清理 worker、SignalR 实时推送（会自动带上新增 key 的数值）。  
  - 需要新增一个“批量采集 + 多指标写回”的机制，因为 `IMetricProvider` 每次只能返回一个 `MetricValue`，而 `/metrics` 一次返回多指标；否则会出现“多个 provider 重复 HTTP 抓取”的性能风险。

#### Open Items
- 需要明确 `/metrics` 的关键指标集合（建议 ≤ 6 个）：tokens 相关、requests 相关、（可选）耗时相关；并确定 tokens/s 的差值口径（按 wall-clock 还是按 predicted_seconds_total）。  
- 需要确定端口解析规则：优先解析 `--port`/`--port=`（以及可能的 `-p`），缺省值如何处理。  
- 需要确定“按需采集”策略是否必要：仅对 llama-server 且命令行包含 `--metrics` 的进程采集，并确保短超时 + 限并发 + 按 pid 清理缓存。  
- Web/desktop 的展示交互：展开/钉住时如何提示“未开启 --metrics / 无法连接 / 等待数据”，以及展示字段的最终排版。

### Round 2 - Q&A（tokens/s 口径、存储灵活性、多实例）(2026-03-05T11:47:09+08:00)

#### User Input
- 需要解释两种 tokens/s 计算方式的区别。  
- 担心不建新表会降低灵活性，尤其是难以单独控制监控频率。  
- 用户可能同时运行多个 llama server（不同端口），担心设计是否仅支持单进程。

#### Analysis Results
- **tokens/s 两种口径的差异**（以 counter 的差值为基础）：  
  - `tokensPerSecond_wall`：`Δtokens / ΔwallSeconds`（分母取采样间隔的真实时间）。反映“用户体感的总体吞吐”，会把 **排队、空闲、网络/请求间隔** 都包含进来；当服务半程空闲时，数值会被拉低。  
  - `tokensPerSecond_compute`：`Δtokens / ΔpredictedSeconds`（分母取服务端统计的“实际解码耗时”等时间类 counter 的差值）。反映“模型/硬件的纯计算效率”，更适合做横向对比（不同量化/不同硬件）。但依赖 `/metrics` 是否提供对应时间类指标，且其语义（并发时是否按请求累加）需要确认。  
  - 结论：两者都“对”，只是在回答不同问题：**体感吞吐** vs **计算效率**。如果只展示一个，我倾向先展示 `wall` 口径（与用户直觉一致），并可选同时保存 `compute` 口径用于诊断。
- **不建新表是否影响灵活性**：  
  - 复用 `ProcessMetricRecords.MetricsJson` 的优点是：零迁移、复用现有写库/聚合/清理/历史接口/SignalR 推送链路。新增 `llama_*` 指标只是新增 JSON key，灵活性在“新增/删减指标字段”层面并不差。  
  - 你担心的“频率独立控制”确实是复用表的短板：该表的写入节奏天然跟随主采集周期（`Monitor:IntervalSeconds`）。  
  - 但有两种折中：  
    1) **采集独立、展示复用**：service 内部对每个 pid 做采样节流（例如每 1s 或 2s 才抓一次 `/metrics`），在其他周期复用缓存；这样 UI 仍然通过既有 SignalR 快速拿到“最新值”，但 DB 可能会重复写同一值。  
    2) **历史独立（建议的架构预留）**：保持实时展示走统一推送，但把历史写入改为“仅在实际抓取成功时写库”，或单独建 `ModelPerfRecords` 表用于独立频率与保留策略（代价是增加迁移、清理/查询接口与推送事件）。  
  - 结论：如果你明确需要“模型性能曲线的采样频率与系统/进程采样周期完全独立”，新表/新写入通道更合适；否则先复用现有表能最快交付并保持系统简单。
- **多实例 llama server（多端口、多 pid）**：  
  - 现有 service 采集链路天然支持多进程（按 pid 列表循环），所以同时运行多个 `llama-server` 没问题。关键在于：为每个 pid 从命令行解析出对应端口，然后按 pid 维持一份差值计算状态（上一次 counter 与时间戳），就能分别计算 tokens/s。  
  - 为了性能，采集器应当是“一个组件管理多个 pid”，而不是每个进程一个线程：限制并发、短超时、按 pid 清理缓存即可。

#### Decision Log（待确认）
> **Decision（建议）**: 先实现同时保存两种 tokens/s（`wall` + `compute`，若 `compute` 的分母指标存在），UI 默认展示 `wall`；并把采集做成 per-pid 节流 + 缓存，先不强制引入新表。  
> - **Context**: 你关心“体感”和“纯算力”两个维度；同时担心频率与灵活性，但也强调最小资源占用。  
> - **Options considered**: 只保留一种口径 vs. 两种都存；立即新表 vs. 先复用表并预留扩展点。  
> - **Chosen**: 两种口径可选 + 先复用表（并预留未来新表/新频率通道）。  
> - **Impact**: 后续实现会把“采样调度/存储策略”做成可配置的策略接口，避免把选择写死在业务逻辑里。

### Round 3 - Exploration（读取实际 /metrics 输出）(2026-03-05T11:54:19+08:00)

#### User Input
用户要求我实际调用 `http://127.0.0.1:1234/metrics`，基于真实指标集合分析；并确认：先不独立建表，先复用现有逻辑；允许多实例（多端口、多 pid）。

#### Analysis Results
- **已成功读取实际输出**：保存于 `​.workflow/.analysis/ANL-llama-metrics-monitor-2026-03-05/llama-metrics-127.0.0.1-1234.txt`。当前输出的指标集合如下（Prometheus text format）：  
  - Counters（单调递增）：  
    - `llamacpp:prompt_tokens_total`：累计 prompt tokens。  
    - `llamacpp:prompt_seconds_total`：累计 prompt 处理耗时（秒）。  
    - `llamacpp:tokens_predicted_total`：累计生成 tokens。  
    - `llamacpp:tokens_predicted_seconds_total`：累计生成耗时（秒）。  
    - `llamacpp:n_decode_total`：累计 `llama_decode()` 调用次数。  
    - `llamacpp:n_tokens_max`：最大观测到的 `n_tokens`（虽然标成 counter，但语义更像“最大值”）。  
    - `llamacpp:n_busy_slots_per_decode`：每次 decode 的忙碌 slots 平均值（虽标 counter，但语义更像“平均值”）。  
  - Gauges（瞬时值）：  
    - `llamacpp:prompt_tokens_seconds`：平均 prompt 吞吐 tokens/s（由服务端计算）。  
    - `llamacpp:predicted_tokens_seconds`：平均生成吞吐 tokens/s（由服务端计算）。  
    - `llamacpp:requests_processing`：正在处理的请求数。  
    - `llamacpp:requests_deferred`：延迟/排队的请求数。
- **tokens/s 口径落到这组指标后更清晰**：  
  - `tokens/s_wall`：`Δtokens_predicted_total / ΔwallSeconds`（用户体感/端到端）。  
  - `tokens/s_compute`：`Δtokens_predicted_total / Δtokens_predicted_seconds_total`（纯计算效率）。  
  - 如果你想让 UI “更像推流速度”，更推荐展示 `tokens/s_compute`（只在生成期内稳定），同时配合 `requests_processing/deferred` 显示是否在忙/排队；`tokens/s_wall` 适合做“整体利用率”曲线。
- **关于你担心的灵活性/频率**：即使复用 `ProcessMetricRecords`，仍然可以在 service 侧做 per-pid 节流（例如只在 1s 内最多抓一次 /metrics），并把“写库节奏”绑定在真实抓取成功时；后续若需要独立 retention/聚合，再迁移到专表不会影响 UI 协议（UI 只依赖统一快照结构）。

#### Decision Log
> **Decision**: 本阶段按用户确认，先复用现有 `ProcessMetricRecords` 作为历史存储，不独立建表；并按 pid 支持多实例 llama-server。  
> - **Context**: 用户明确同意“先复用逻辑试试”，且存在多端口多进程场景。  
> - **Options considered**: 立即新表 vs. 复用现有表；单实例假设 vs. per-pid 独立状态。  
> - **Chosen**: 复用现有表 + per-pid 采集与差值状态。  
> - **Impact**: 设计会把 llama 指标写入 `ProcessMetrics.Metrics`（新增 `llama_*` key），从而自动复用：写库、历史接口、SignalR 推送。

### Round 4 - Q&A（展示字段含义与可扩展项）(2026-03-05T11:58:40+08:00)

#### User Input
用户同意默认按推荐方案实现，但希望先解释“展示字段”的具体含义，并询问可额外丰富哪些字段。

#### Analysis Results
- **建议默认展示（易懂且最有价值）**：
  - `Gen TPS (compute)`：`Δtokens_predicted_total / Δtokens_predicted_seconds_total`，单位 tokens/s；表示“模型真正生成阶段的纯计算速度”，适合对比性能。
  - `Busy%`：`Δtokens_predicted_seconds_total / ΔwallSeconds`，0–100%；表示“这段时间里模型用于生成的占比”，可理解为利用率。
  - `Req (processing/deferred)`：来自 `requests_processing` 与 `requests_deferred`；前者=正在处理的请求数，后者=排队/延迟的请求数，用于判断是否堵塞。
  - `Out tokens`：`tokens_predicted_total`；自服务启动以来累计输出 tokens（重启会归零）。
- **可选增强（不额外请求，只做派生/多显示）**：
  - `Gen TPS (wall)`：`Δtokens_predicted_total / ΔwallSeconds`；体感吞吐。
  - `Gen ms/token`：`(Δtokens_predicted_seconds_total / Δtokens_predicted_total) * 1000`；每 token 平均耗时。
  - Prompt 侧同类指标：`prompt_tokens_total`、`prompt_tokens_seconds`、`prompt_seconds_total`、以及 prompt 的 tokens/s 与 ms/token。
  - `Decode/s`：`Δn_decode_total / ΔwallSeconds`；内部 decode 调用频率（诊断用）。
  - `n_busy_slots_per_decode` 与 `n_tokens_max`：更偏内部诊断；当前输出中它们虽标为 counter，但语义是平均/最大值，展示可做“debug 模式”，不建议作为核心 KPI。
