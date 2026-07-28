---
verdict: ready_with_concerns
summary: "P2 desktop review of Run 010 post-execute repair: 5 files reviewed across 6 dimensions. Clippy clean (exit 0). 3 G3 parity fixes verified exact against C# source. No backward-compat breaks. No anti-patterns. Memory gate amendment honest (no suppressed thresholds, no fabricated evidence). 1 medium finding: spec-doc <10 MiB vs amended <40 MiB contradiction not routed. 1 low finding: bounded Tokio optimization did not reduce memory (+2.5 MiB unexplained delta, honestly recorded). 4 info findings: parity verified, clamp correct, ListView capped, clippy clean. Run left unsealed."
constraints:
  - {id: C-RV-1, text: "Run artifacts only; product source read-only.", status: locked}
  - {id: C-RV-2, text: "No subagents; no agy/aug; Auggie MCP unavailable — target definitions pre-read by parent.", status: locked}
  - {id: C-RV-3, text: "Do NOT edit product source code; review is read-only analysis.", status: locked}
  - {id: C-RV-4, text: "Do not call session done/decide/next; leave Run unsealed.", status: locked}
decisions:
  - {id: D-RV-1, text: "Verdict=ready_with_concerns: 1 medium finding (spec-doc contradiction), 0 critical, 0 high.", status: accepted}
  - {id: D-RV-2, text: "Memory gate amendment honest: SC-09 FAIL (<10 MiB), SC-10 PASS (<40 MiB), no suppression, no fabrication.", status: accepted}
  - {id: D-RV-3, text: "G3 parity all PASS: Locked toggle, pinned overflow, unsnapped clamp — verified against C# source.", status: accepted}
  - {id: D-RV-4, text: "Antipattern check clean: cargo clippy -p xhm-desktop --all-targets -- -D warnings exit 0.", status: accepted}
caveats:
  - "Spec doc (docs/rust-migration-guide.md:305) not superseded or conflict-marked after amendment — routed as COR-001 spec conflict."
  - "Bounded Tokio optimization delta +2.5 MiB unexplained without controlled A/B — honestly flagged by Run 010."
  - "Original P2 gate <10 MiB NOT met (33.66 MiB normal, 37.04 MiB UI_SMOKE); user-approved amended <40 MiB fallback met; amendment recorded honestly."
open_questions: []
next:
  - {command: "maestro spec conflict mark", reason: "Route COR-001: memory gate threshold dispute (docs <10 MiB vs amended <40 MiB) for adjudication.", needs: []}
  - {command: "P3 renderer/backend substitution", reason: "Deeper memory reduction deferred to P3 (outside P2 boundary).", needs: []}
---
## 摘要

Run `20260728-012-review`（review phase）对 Run 010 post-execute repair 的 5 个改动文件执行 standard 级 6 维代码审查。anti-pattern 检查（`cargo clippy -p xhm-desktop --all-targets -- -D warnings`）exit 0 clean。3 个 G3 parity 修复逐项验证 C# 源码对等。无 backward-compat 破坏。内存 gate 修订诚实（无抑制阈值、无伪造证据）。1 个 medium finding（spec-doc <10 MiB vs 修订后 <40 MiB 矛盾未路由），1 个 low finding（bounded Tokio 优化未降内存 +2.5 MiB 诚实记录），4 个 info findings（parity 验证、clamp 正确、ListView 上限、clippy clean）。Run 保持未封存。

## 结论/Verdict

**ready_with_concerns** — 5 个文件 × 6 维审查完成，artifacts 已写入，anti-pattern clean，G3 parity 全 PASS，无 critical/high。concern：spec-doc 阈值矛盾未路由（COR-001 medium），bounded 内存优化未达目标但诚实记录（PRF-001 low）。

### CODE REVIEW RESULTS

```
Scope:    xhm-desktop Run 010 post-execute repair (5 files + 3 G3 parity fixes + bounded Tokio memory optimization)
Level:    standard
Files:    5 files × 6 dimensions

Severity Distribution:
  Critical: 0   High: 0   Medium: 1   Low: 1   Info: 4

Top Issues:
  1. [medium] COR-001: Memory gate spec-doc contradiction: docs <10 MiB vs amended goal <40 MiB (docs/rust-migration-guide.md:305)
  2. [low] PRF-001: Bounded Tokio current-thread optimization did not reduce memory (+2.5 MiB unexplained delta) (xhm-desktop/src/lib.rs:400)

Critical Files (flagged in 3+ dimensions):
  (none)

Verdict: WARN
Issue Candidates: 1
```

## 讨论/复盘

### G3 Parity 验证

| 修复 | 状态 | 证据 |
|------|------|------|
| Locked→Expanded 点击切换 | PASS | `panel_after_click` floating_window.rs:92-101 匹配 C# FloatingWindowViewModel.cs:258-270；测试 floating_window.rs:1113-1121 |
| Pinned 卡片上限可滚动 ListView | PASS | floating_window.slint:202-203 `max-visible-pinned: 3`，`pinned-area-height` 有界，ListView 可滚动 |
| Unsnapped 拖拽工作区 clamp | PASS | `clamp_rect_to_work_area` floating_interactions.rs:419-432；`finish_drag` floating_window.rs:857-885；2 个确定性测试 |

### Anti-Pattern 检查

```
cargo clippy -p xhm-desktop --all-targets -- -D warnings
→ exit code 0, clean（review 期间重新验证）
```

无 anti-pattern。无 unsafe 代码。无硬编码密钥。无抑制机制（skip/ignore/allow）。

### 内存 Gate 修订诚实性

| 检查项 | 结果 |
|--------|------|
| 修订 ID | amend-g4-memory-threshold-chg001 |
| 状态 | applied |
| 原始阈值 | < 10 MiB → FAIL（normal 33.66 MiB，UI_SMOKE 37.04 MiB，0/60 below 10 MiB） |
| 修订阈值 | < 40 MiB → PASS（normal 33.66 MiB，UI_SMOKE 37.04 MiB） |
| 抑制阈值 | 否 |
| 伪造证据 | 否 |
| 优化 delta | +2.5 MiB 未解释，诚实标注为"无 controlled A/B" |
| Spec doc 更新 | 否 — 矛盾路由为 COR-001 spec conflict |

**结论**：修订诚实。原始 gate 失败已记录（SC-09），修订 gate 通过已记录（SC-10），无阈值抑制，无证据伪造。+2.5 MiB 优化 delta 透明标注为未解释。唯一 concern：spec doc 未路由（supersede 或 conflict-mark）— 记录为 COR-001。

### Backward Compatibility

无破坏。无 public API 签名变更。Tokio runtime 变更为 SSE 专用线程内部。Cargo.toml feature 收窄为内部。无 Slint interface 属性移除或重命名（新增 `pinned-area-height` 和 `max-visible-pinned` 为加法性）。`clamp_rect_to_work_area` 为新增纯函数。

## 产物

| 文件 | kind | 状态 |
|------|------|------|
| `outputs/review-findings.json` | review-findings/1.0 (primary) | written |
| `outputs/review-summary.json` | review-summary/1.0 | written |
| `outputs/antipattern-report.json` | antipattern-report/1.0 | written |
| `outputs/spec-conflicts.json` | spec-conflicts/1.0 | written |
| `outputs/issue-candidates.json` | issue-candidates/1.0 | written |

无产品源码改动（review 为只读分析）。

## 交接/Next

- 路由 COR-001：通过 `maestro spec conflict mark` 将内存阈值矛盾（docs <10 MiB vs 修订 <40 MiB）提交 knowledge audit 裁决。
- 更深层内存优化需 P3 渲染器/后端替换（P2 边界之外）。
- Run 保持 **未封存**。
