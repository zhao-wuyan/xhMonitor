---
verdict: ready
summary: "P2（xhm-desktop）判 large：4 个独立子系统 + 硬串行 + 视觉验收门，go_with_conditions；契约已由 P1 冻结，POC 已锁定核心 Win32，建议 post-analyze-scope 裁决是否拆 roadmap。"
constraints: []
decisions: []
caveats: []
open_questions: []
next:
  - command: post-analyze-scope
    reason: "session chain step-001 决策点；scope_verdict=large，需人工裁决 plan vs roadmap"
    needs: [current-analysis]
  - command: plan
    reason: "若 post-analyze-scope 裁决 plan 路径，则按 G1→G2→{G3,G4} 产出执行计划"
    needs: [current-analysis, session-priors]
  - command: roadmap
    reason: "若 post-analyze-scope 裁决 large 拆分为多 milestone，则改走 roadmap（scope_verdict=large 触发条件）"
    needs: [current-analysis]
---

## 摘要

本次 analyze 对 `docs/rust-migration-guide.md` 的 P2（xhm-desktop）做证据型范围与风险评估。结论：

- **范围判定**：`large` —— 4 个独立子系统（G1 基础/SSE/配置、G2 Win32 原生壳、G3 主悬浮窗核心 UI、G4 任务栏窗+设置+关于+性能验收）+ 硬串行依赖（workspace→SSE/状态→Win32 壳→视觉对等）+ 12 项核心 UI 视觉验收门。
- **建议**：`go_with_conditions` —— 契约由 P1 冻结、POC 已锁定核心 Win32、内存门禁（3.8 MiB）实测，但 Slint 动画视觉对等与 tray-icon 集成存在需 plan 前置 spike 才能消除的未知。
- **下游**：`post-analyze-scope` 决策点（step-001）应基于 `scope_verdict=large` 裁决走 `plan` 还是改道 `roadmap`；analyze 不替其决定。

P0/P1 review 已 PASS（spec_compliance 全 MET，combined Private Bytes max 28.79 MiB，158 cargo test 通过），为 P2 提供了不可回归的契约基线。

## 结论/Verdict

### Scope Verdict: `large`

依据 `C:/Users/xinghe_zwy/.maestro/workflows/analyze.md:48-51` 的双重判据：

1. **3+ 独立子系统**：`session.json` 的 `decomposition.goals` 已显式定义 G1/G2/G3/G4 四个 goal，每个有独立 `done_when` / `boundary` / `evidence` / `lifecycle`。
2. **硬串行依赖屏障**：
   - xhm-desktop 必须先加入 workspace 才能编译（`Cargo.toml:1-6` 当前 members 仅 xhm-core/xhm-service）。
   - SSE/状态层（G1）必须先于 UI（G3/G4）落地。
   - Win32 壳（G2）必须先于双窗口几何与托盘共存。
   - Slint 有机矩阵动画 + tray-icon 集成存在经 plan 才能消除、甚至需原型才能消除的未知。

UI 对等清单核心 12 项（`docs/rust-migration-guide.md:262-278`）每项承载独立动画/交互验收，复杂度显著高于 single/few-file 的 `small` 判据，也不止 1-2 个可并行子系统。

### Recommendation: `go_with_conditions`

四项条件（进入 plan 前必须落实）：

1. **plan 前置 spike**：tray-icon crate 与 Slint winit 事件循环共存验证（go/no-go）。
2. **G3 早期原型**：有机吸附矩阵 Slint 动画与 C# Storyboard 视觉对等，让用户确认；不达标评估 Skia renderer 回退（注意 `<10 MiB` 内存门禁）。
3. **SSE 客户端**：断线重连必须独立 tokio 任务，禁止阻塞 Slint UI 线程。
4. **边界隔离**：所有 Win32/HTTP/SSE/文件系统/时钟边界 trait 注入或 cfg(test)，CI 无管理员/无硬件全绿。

### 六维评分（1-5，证据溯源见 findings.json#dimensions）

| 维度 | 分 | 置信度 | 主要证据 |
|------|---:|---:|------|
| Feasibility | 4 | 78% | POC 实测 3.8 MiB + P1 契约冻结；tray-icon/动画未验证 |
| Impact | 5 | 95% | 149→<10 MiB，为 P3 cutover 扫清阻断 |
| Risk | 3 | 65% | 视觉验收 + 多 crate 集成未知 + DPI 几何重建 |
| Complexity | 2 | 70% | 4 goal + 硬串行 + ~4k 行 C# 端口 |
| Dependencies | 4 | 90% | P1 sealed，无外部服务 |
| Alternatives | N/A | 85% | Tauri/Flutter/原生均已在可行性阶段否决 |
| **Overall** | — | **78%** | go_with_conditions |

## 讨论/复盘

### 探索证据汇总

证据锚点共 24 项，覆盖：

- **P2 主契约**：`docs/rust-migration-guide.md:238-308`（P2 全文）+ `46-103`（workspace 结构）+ `332-389`（测试/风险）。
- **POC 实测**：`docs/rust-migration-feasibility.md:296-383`（3.8 MiB + Win32 矩阵 + 待验证项）+ `poc/slint-desktop/src/win32.rs:33-164`（4 项核心 Win32 实现）。
- **P1 冻结契约**：`xhm-core/src/wire.rs:17-228` + `xhm-service/src/realtime/mod.rs:579-705` + `state.rs:14-16` + `lib.rs:29-46`。
- **C# Desktop 参考实现**：App.xaml.cs（Mutex/生命周期）、ServiceDiscovery.cs（service-endpoints.json + 健康探测）、SignalRService.cs（5 事件 + 重连）、FloatingWindow.xaml.cs 1498 行（交互全集）、TaskbarPlacementService.cs（4 边几何）、TaskbarMetricsWindow.xaml.cs 711 行（停靠 + 拖拽 + 4 样式）、TrayIconService.cs（7 项菜单）、SettingsWindow.xaml.cs（4 分区 + 重启流程）、GitHubAppUpdateService.cs（Gitee + Lite installer）。
- **契约 schema**：`XhMonitor.Desktop/service-endpoints.json:1-8` + `.schema.json:1-30`（PascalCase ServiceEndpoints）。
- **POC 依赖**：`poc/slint-desktop/Cargo.toml`（windows 0.58 + slint 1 + parking_lot）vs workspace windows-sys 0.59。

### 压力测试（Pressure Pass）

目标：`F-05 / scope_verdict=large`。四阶梯度：

1. **证据需求**：主悬浮窗是否真构成独立子系统？→ 是。FloatingWindow.xaml.cs 1498 行承载 12 项核心交互，每项独立动画/状态；TaskbarMetricsWindow 711 行承载独立 4 样式几何；两者共享 SSE 但状态机独立。
2. **假设探针**：Slint 软件渲染能在 <10 MiB 还原所有动画？→ POC 仅验证脉冲矩形。记录 `residual_risk[R-01]`，要求 G3 早期原型 + 4K 验收；不达标 `docs/rust-migration-guide.md:384` 预案为 Skia renderer（但内存门禁需重评）。
3. **边界 tradeoff**：scope=large 会触发 post-analyze-scope 改道 roadmap，是否过度？→ session.json 已定义 4 goal + 硬串行，workflow large=3+ 子系统，如实判定是诚实交付；是否拆 roadmap 由 post-analyze-scope 人工裁决。
4. **根因**：为什么 P1 顺滑而 P2 判 large？→ P1 是协议/数据层端口（C#→Rust 一对一，POC 全链路验证）；P2 是视觉/交互/系统壳端口，含人眼验收与多 crate 集成未知，本质复杂度更高。

**结论**：scope_verdict=large 与 go_with_conditions 经压力测试成立。

### Intent Coverage Matrix

| # | Original Intent | Status | Where Addressed |
|---|----------------|--------|-----------------|
| 1 | 新增 xhm-desktop crate 并加入 Cargo workspace | ✅ Addressed | F-01, decisions, technical_solutions[0] |
| 2 | 基于 Slint 实现主悬浮窗、任务栏指标窗、设置、关于与告警 Toast | ✅ Addressed | F-05, F-06, F-07 |
| 3 | 实现 SSE/REST Service 客户端与 service-endpoints.json 可执行目录发现 | ✅ Addressed | F-03, F-04 |
| 4 | 实现 HWND/topmost/点击穿透/拖拽吸附/任务栏四边定位/多显示器 DPI/托盘/窗口位置持久化/单实例 | ✅ Addressed | F-02, F-06, technical_solutions[2,3] |
| 5 | 迁移 P2 UI 对等清单核心项与扩展项 | ✅ Addressed | F-05, F-07, decisions[7,8] |
| 6 | 补充 xhm-desktop 边界隔离测试/工作区验证/Windows 手动验证证据 | ✅ Addressed | decisions, technical_solutions |
| 7 | 不修改 xhmonitor-web 前端 | 🛡 Preserved | decisions[3] |
| 8 | 不删除或切换现有 C# Desktop/Service 项目 | 🛡 Preserved | decisions[3] |
| 9 | 不执行 P3 端口切换/发布脚本/安装包迁移 | 🛡 Preserved | decisions[7] |
| 10 | 不迁移全局热键 Ctrl+Alt+Shift+X | 🛡 Preserved | decisions[2], F-08 |

无 ❌ Missed 项。

### 主要技术方案（technical_solutions，详见 findings.json）

1. **xhm-desktop crate 骨架**：加入根 Cargo.toml members；模块按 main.rs/win32.rs/service_client.rs/config.rs/ui/ 切分；POC win32.rs 直接提升。
2. **SSE 客户端**：reqwest + tokio + eventsource-stream（或手写分帧）；独立任务跑连接/重连，channel 推回 UI 线程；表驱动单测覆盖 5 类 PushEvent。
3. **Win32 壳扩展**：POC win32.rs 起点 → TaskbarPlacementService 端口（FindWindowEx 递归 TrayNotifyWnd/MSTaskListWClass）+ Mutex 单实例 + GetDpiForWindow/MonitorFromWindow DPI；PlacementCalculator 纯函数 + 表驱动单测。
4. **托盘**：tray-icon crate 优先；7 项菜单 + 双击切换 + BalloonTip；早期 spike 验证与 Slint 共存。

### 决策分类（decisions，详见 findings.json）

- **Locked (4)**：P1 契约沿用、winit-software 渲染、热键不迁移、C#/Service 不切换。
- **Free (3)**：SSE 客户端库、Win32 FFI 绑定（windows vs windows-sys）、托盘 crate。
- **Deferred (2)**：更新下载/安装器启动 → P3；AdminMode/Startup/LAN/防火墙系统级设置 → P2 仅 UI 灰显/只读。

## 产物

| 路径 | kind | role | 说明 |
|------|------|------|------|
| `outputs/findings.json` | findings | primary | 8 findings + 9 decisions + scope_verdict=large + go_with_conditions + intent_coverage(10/10) + pressure_pass |
| `outputs/risk-matrix.json` | risk-matrix | evidence | 8 risks（RISK-08 内存突破/视觉对齐最高分）+ 5 assumptions + 4 open_questions |
| `outputs/priors.json` | priors | evidence | 4 specs + 10 doc_index + 7 wiki + workspace 状态 + P1 sealed 契约清单 |
| `report.md` | — | handoff | 本文件 |

所有 JSON 含完整 `_meta`（kind + schema），路径相对 run_dir：`.workflow/sessions/maestro-rust-migration-p2-desktop-20260727-021446/runs/20260727-001-analyze/`。

## 交接/Next

1. **post-analyze-scope（step-001，pending）**：consume `current-analysis#/scope_verdict=large` 与 `recommendation=go_with_conditions`。裁决：
   - 若判定 large 成立 → 改道 `roadmap`（workflow/analyze.md:327 规则）。
   - 若判定可在单 plan 内消化（4 goal 同 chain step-003 execute）→ 进入 `plan`。
   - 建议证据：scope_rationale 的 4 goal + 硬串行 + 视觉验收门；post-analyze-scope max_retries=1，应一次性裁决。
2. **plan（step-002，pending）**：若 post-analyze-scope 通过 plan 路径，产出 G1→G2→{G3,G4} 的执行计划。前置 spike 任务：tray-icon + Slint 共存、有机矩阵动画原型。
3. **Deferred 跟踪**：更新下载/安装器、系统级设置（AdminMode/Startup/LAN/防火墙）在 plan 阶段进入 `.workflow/issues/issues.jsonl`（status=deferred, source=analyze）。

**不完成 Run** —— 按 assignment，仅产出 artifacts + report.md + 运行 `maestro run check`，由 Main 决定后续。
