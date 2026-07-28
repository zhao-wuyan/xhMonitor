---
verdict: ready
summary: "xhm-desktop release 内存根因已定位到稳定的匿名 Private commit 启动基线；普通态 33.492-33.531 MiB，P2 <10 MiB 门禁仍失败。"
mode: standalone
target: "xhm-desktop release Private Bytes"
diagnosis_status: partial
clusters: 1
gaps: 1
constraints:
  - id: C-DEBUG-1
    text: "产品源码与 Cargo 文件只读；本 Run 未实施修复。"
    status: locked
  - id: C-DEBUG-2
    text: "不移除 accessibility；不伪造组件级内存数字。"
    status: locked
decisions:
  - id: D-DEBUG-1
    text: "首个 bounded attempt 仅收窄 Tokio features，并把专用 SSE 线程内 runtime 改为 current-thread。"
    status: proposed
  - id: D-DEBUG-2
    text: "Locked click、pinned overflow、unsnapped work-area clamp 分别作为低风险 G3 repair direction。"
    status: proposed
caveats:
  - "匿名 allocation arena 的 Slint/Windows allocator/Tokio 精确归属缺少符号化 heap stack，因此 diagnosis 为 partial。"
  - "历史 baseline 条件字符串遗漏 XHM_DESKTOP_UI_SMOKE；其 0/60 失败有效，但不是普通 collapsed 状态。"
open_questions:
  - "Tokio bounded attempt 的实际 MiB 收益必须在独立修复 Run 中测量，不能由本次只读诊断推定。"
next: []
---
## 摘要

=== DEBUG SESSION ===
Mode:        standalone
Target:      xhm-desktop release Private Bytes < 10 MiB
Clusters:    1 investigated
Gaps:        1
  Diagnosed: 1 root category found
  Uncertain: 1 component-level ownership split

接受基线为 60/60 超标，范围 `34.504-34.719 MiB`。本 Run 重新采样：普通 release 为 `33.492-33.531 MiB`（30 次），重建 `XHM_DESKTOP_UI_SMOKE` 为 `37.184-37.559 MiB`（60 次），live dual-SSE 为 `33.277 MiB`（30 次）；所有样本都高于 `10 MiB`。

## 结论/Verdict

门禁失败是当前 Slint/winit-software、Windows UI 与 async client 组合的稳定启动 footprint，不是 1 分钟内持续增长的 leak，也不是 SSE payload/retry 的主导增量。

普通态 `VirtualQueryEx` 显示 `MEM_PRIVATE=31.609 MiB`；最大的 6 个匿名 RW allocation base 合计 `29.672 MiB`，占 `MEM_PRIVATE` 的 `93.87%`。同进程逐步显示 Settings 和 About 分别增加 `4.648 MiB` 与 `1.621 MiB`，累计 `6.269 MiB`。connected SSE 比 unavailable/retrying normal 低 `0.237 MiB`，所以 transport activity 不是普通态 `23.514 MiB` 超额的主因。

精确 arena owner 仍是 inference：release binary 已 strip，且产品只读边界不允许 feature-removal/source variant。诊断没有给 renderer、tray、SSE、accessibility 编造单项 MiB。

GateRecord:

```json
{"gate":"hypothesis-tested","status":"partial","checked_at":"2026-07-27T23:27:44+08:00","evidence":{"clusters":1,"diagnosed":1,"confidence":0.84},"artifact":"outputs/diagnosis.json"}
{"gate":"evidence-grounded","status":"partial","checked_at":"2026-07-27T23:27:44+08:00","evidence":{"evidence_records":13,"backward_traced":true},"artifact":"outputs/evidence.ndjson"}
```

## 讨论/复盘

历史 baseline 的 condition 仅写了 `release,winit-software,main+taskbar,dual-SSE`，但实际 launch 还设置 `XHM_DESKTOP_UI_SMOKE=1`。该 fixture 会把主窗置于 Locked details 状态、加载 36 个 process rows，并 spawn ping 子进程；采样始终只统计 desktop PID。fresh exact-smoke 数值比历史高约 `2.7-3.1 MiB`，说明 launcher/rendered state 会影响具体 band，但两者均为 0/60 通过，不影响 gate diagnosis。

最保守的首个优化尝试只改两个 ownership 点：workspace Tokio normal dependency 从 `full` 收窄到 `rt/sync/time/macros`，并将既有 `xhm-desktop-sse` 专用线程中的两 worker runtime 改为 `new_current_thread`。预计移除 2 个 Tokio worker thread 并降低部分 stack/control 与 feature/code surface；未测量前不承诺 MiB，更不宣称可单独达到 `<10 MiB`。accessibility 保持不变。

3 个 G3 parity 方向独立记录：

- `Locked` 点击应按 C# 真源从 `Locked -> Expanded`，而不是 Rust 当前的 `Locked -> Collapsed`；
- pinned cards 使用最多 4 行的 capped/scrollable viewport，避免总数直接压缩 detail panel；
- no-snap drag release 将 physical rect clamp 到目标 monitor `work_area`。

## 产物

- `outputs/diagnosis.json`：`diagnosis/1.0`，measured facts、inferences、backward trace、confidence 与 pressure pass；
- `outputs/hypotheses.json`：3 个 hypotheses 的 tested action、confirm/refute evidence 与 contradicting evidence；
- `outputs/reproduction.json`：accepted baseline 与 5 组 fresh/differential reproduction；
- `outputs/fix-directions.json`：1 个 bounded memory attempt 与 3 个独立 G3 low-risk directions，含 rollback/expected impact；
- `outputs/evidence.ndjson`：13 条 append-only evidence；
- `outputs/understanding.md`：debug 状态、root cause 与 confidence；
- `work/*.json`、`work/*.log`、`work/cargo-tree*.txt`：原始采样、地址空间、runtime action 与 dependency evidence。

## 交接/Next

本 Run 不提出 chain，`chain_effects=[]`，并保持 unsealed。产品文件未改动。

后续如执行 memory repair，必须先单独应用 Tokio bounded attempt，再用同一 launcher/warmup/sample harness 验证 dual SSE、resubscribe/cancel、shutdown、窗口/托盘行为与 60 次 release Private Bytes；收益不足或行为回归时同时回滚 feature list 与 runtime builder。G3 3 项不得混入同一次 memory attribution build。
