---
run_id: 20260726-001-execute
session_id: maestro-rust-migration-p0p1-20260726-062756
stage: execute
goal_ref: G1
status: completed
verdict_suggestion: done-with-concerns
summary: >-
  建立 Cargo workspace 与 xhm-core 共享层（models/wire/time/traits/error，52 tests green，
  clippy 零警告），并将 lhm-bridge 从 poc/ 提升为正式目录（修正管理员权限检测、新增 banner
  与 --require-admin/--interval、优雅退出实测 exit 0）。
decisions:
  - id: D-P0-1
    text: 持久化只接受 DateTime<Utc> 落库，不复刻 C# MetricRepository.MapToEntity:79 的 SpecifyKind 假转换；llama 历史行的时区偏移保留为已知数据事实，读取端不补偿。
    status: accepted
  - id: D-P0-2
    text: 推送层 transport-agnostic —— 一份 PushEvent 枚举 + SignalR over WebSocket（Web 前端零改动）与 SSE（Desktop）两个适配器。
    status: accepted
  - id: D-P0-3
    text: MetricStore 采用同步签名，调用侧用 tokio::task::spawn_blocking 包装；避免 async-trait 装箱与 dyn 兼容性问题。
    status: accepted
concerns:
  - "C-P0-1 (medium)：lhm-bridge 提权路径未验证——当前环境非管理员，cpu_temp/cpu_temp_label 无证据；降级路径已验证正确。"
  - "C-P0-2 (low)：docs/rust-migration-guide.md 的 ConfigController/MetricsController 行号引用已过期，实现按实际源码对齐。"
next:
  - command: maestro session next --session maestro-rust-migration-p0p1-20260726-062756
    reason: 进入 P1 —— xhm-service（axum）：SQLite 层、20 个 REST 端点、SignalR negotiate+WS、SSE、LHM 子进程管理、RyzenAdj 回退、采样/聚合/清理三个 worker；并行端口 35181。
  - command: lhm-bridge\bin\Release\net8.0\win-x64\lhm-bridge.exe --require-admin
    reason: 以管理员身份复核提权路径，确认输出含 cpu_temp 与 cpu_temp_label（C-P0-1）。
---
# P0 基础层 — 执行报告

## 交付

| 产出 | 位置 | 状态 |
|------|------|------|
| Cargo workspace 根清单 | `Cargo.toml` | ✅ |
| `xhm-core` crate（models / wire / time / traits / error） | `xhm-core/src/` | ✅ 52 tests green |
| 正式 `lhm-bridge`（从 `poc/` 提升） | `lhm-bridge/` | ✅ build clean |

## 验证证据

```
cargo test -p xhm-core                                   → 52 passed; 0 failed
cargo clippy -p xhm-core --all-targets -- -D warnings    → 零警告
dotnet build -c Release (lhm-bridge)                     → 0 警告 0 错误
PTY 真实 Ctrl+C → lhm-bridge                              → exit code 0
```

全部测试在**无管理员权限、无真实硬件**下运行，满足指南 §5.3 的 CI 约束。

## 关键设计决策

**1. 时间基准不复刻 C# 缺陷。** C# `MetricRepository.MapToEntity:79` 用
`DateTime.SpecifyKind(cycleTimestamp, DateTimeKind.Utc)` —— 只贴标签不换算；
而 `Worker.cs:455-461` 的 llama 采样传本地时间、`PerformanceMonitor.cs:32` 传 UTC。
结果 `ProcessMetricRecords.Timestamp` 混存两种基准，llama 行整体偏移一个时区，
而聚合水位与保留期裁剪又一律按 UTC 比较。

Rust 侧 `MetricStore::save_process_metrics` 只接受 `DateTime<Utc>`，写入一律真 UTC；
**不对历史行做读取端补偿** —— 补偿需要猜测每行来源 provider，会把一个可见的数据事实
变成不可见的启发式。决策与理由已写进该方法的文档注释。

**2. 三种时间格式必须分开。** 迁移中同时存在：

| 场景 | 格式 |
|------|------|
| SQLite `TEXT` 列 | `2026-07-26 12:34:56.7891234` |
| REST 响应 | `2026-07-26T12:34:56.7891234Z` |
| SignalR 推送 | `2026-07-26T12:34:56.7891234+08:00` |

三者共用 .NET 的「7 位 tick、去尾零、全零省点」规则。这对 SQLite 尤其关键——
`Timestamp` 是 TEXT 列，范围查询是**字典序**比较，多写少写一位都会查错历史数据。
`time.rs` 有专门的排序不变量测试，并已对照真实库 `XhMonitor.Service/xhmonitor.db`
的实际小数位（3~7 位不等：`.111` / `.5277` / `.95316` / `.359377` / `.5164418`）验证解析。

**3. 推送层 transport-agnostic。** 一份 `PushEvent` 枚举 + 两个适配器：
Web 侧 SignalR over WebSocket（前端零改动），Desktop 侧 SSE。
前端 `useMetricsHub.ts` 既未 `skipNegotiation` 也未强制 transport，
因此 P1 必须实现完整 negotiate 握手，WebSocket-only 会断连。

**4. 线上契约有三条互相打架的序列化规则**，已在 `models.rs` 顶部固化：
属性名 camelCase、**Map key 逐字保留**、null 必须写出。
唯一例外是 SignalR `ProcessMetricSnapshot` 的 `hasMeta`/`commandLine`/`displayName`
三个条件序列化字段（`Worker.cs:1111-1116`）。

## 遗留关注项

**C-P0-1（medium）— lhm-bridge 提权路径未验证。**
指南 P0 验收要求「提权运行后输出包含 `cpu_temp` + `cpu_temp_label`」。
本次环境非管理员，实测输出仅含 `gpu_temp` / `gpu_load` / `net_*` / `disk_*`，
`cpu_temp` 与 `cpu_temp_label` 键缺失。这**符合非提权降级路径的预期**
（banner 已输出 `is_admin: false` 并在 stderr 警告），但提权路径本身
**未取得证据**。需在开发机以管理员身份运行 `lhm-bridge.exe` 复核。

**C-P0-2（low）— 迁移指南行号引用过期。**
`docs/rust-migration-guide.md:132-141` 的 ConfigController 行号与当前源码不符
（指南 `:53/:65/:84/:99/:146/:171/:214`，实际 `:58/:70/:96/:114/:181/:212/:268`）；
MetricsController 同样（指南 `:50/:99/:130`，实际 `:64/:125/:166`）。
实现已按实际源码对齐，指南待订正。

## 下一步（P1）

`xhm-service`（axum）：SQLite 层（EF 兼容 schema + `__EFMigrationsHistory`）、
20 个 REST 端点、SignalR negotiate + WS 文本协议、SSE、LHM bridge 子进程管理
（指数退避重启）、RyzenAdj native + CLI 回退装饰器、采样 / 聚合 / 清理三个 worker。
并行端口 35181，不占用 35179。
