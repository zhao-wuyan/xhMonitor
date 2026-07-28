---
verdict: ready
summary: >-
  14 个连续 task（G1=4 / G2=4 / G3=3 / G4=3，≤16 守卫）覆盖 P2 全部 54 条需求：xhm-desktop crate/workspace、
  service-endpoints.json 发现、5 类 PushEvent SSE 客户端、Win32 原生壳（单实例/HWND/topmost/click-through/DPI/四边/拖拽/双窗/托盘/持久化）、
  主悬浮窗核心 UI、任务栏 4 布局、设置/关于/更新检查/Toast、边界测试与真实 Windows/4K/内存验收；
  tray+Slint 与有机动画早期 go/no-go 门、winit-software 锁定、不迁移全局热键、不切 P3/不下载安装器。
constraints:
  - id: C-P2-1
    text: 沿用 P1 冻结契约：5 类 PushEvent、SSE /api/v1/events、默认 Service 端口 35181；禁止协议扩展或回归。
    status: locked
  - id: C-P2-2
    text: Slint 后端固定 winit-software；spike NO-GO 只能阻塞下游并提交独立决策，不得自行切 Skia/wgpu/femtovg。
    status: locked
  - id: C-P2-3
    text: tray 首选 tray-icon 0.24.1（官方 API：TrayIconBuilder、MenuEvent/TrayIconEvent::set_event_handler、EventLoopProxy 转发）；只有完整 NO-GO 记录后才回退 Shell_NotifyIcon，且不得并存。
    status: locked
  - id: C-P2-4
    text: 不迁移 RegisterHotKey/WM_HOTKEY/Ctrl+Alt+Shift+X；点击穿透仅由托盘菜单退出。
    status: locked
  - id: C-P2-5
    text: P2 与现有 C# Desktop/Service 并行；不做 P3 cutover、端口切换、发布脚本或 installer 下载/启动。
    status: locked
  - id: C-P2-6
    text: 不修改 xhmonitor-web、XhMonitor.Desktop、xhm-service、xhm-core（仅 workspace 根依赖消费）。
    status: locked
  - id: C-P2-7
    text: AdminMode/Startup/LAN/Firewall/System 只读禁用并显示“P3 启用”；只写既有 Appearance/DataCollection/非系统 Monitoring keys。
    status: deferred
decisions:
  - id: D-P2-1
    text: 统一 8+6 planner 提案为 14 个连续 TASK（G1->G2->G3->G4 硬串行）。
    status: accepted
  - id: D-P2-2
    text: TASK-002 在 G1 名下持有 tray+Slint 共存 go/no-go spike，作为 TASK-008 托盘实现的前置依赖。
    status: accepted
  - id: D-P2-3
    text: TASK-009 在 G3 名下持有有机动画 go/no-go spike，作为 TASK-010/011/012 的前置依赖，并产出可复用 G3-owned 有机壳组件。
    status: accepted
  - id: D-P2-4
    text: 每个 task 单一 goal_ref/execute_step_id；不可避免共享文件（root Cargo.toml/Cargo.lock、crate Cargo.toml/build.rs/lib.rs/main.rs、ui/shell.slint compile root）按依赖顺序串行解决，无并行写冲突。
    status: accepted
concerns:
  - verify schema 使用更丰富的对象 {commands[], manual_scenarios[], expected}，语义完整但比 prepare 骨架 verify:[] 更宽；记录为非阻塞。
  - 14 task 估算合计 765 分钟，多个 task 触 60 分钟上限；执行中如发现 slippage 需复核。
  - coverage_map 将 TASK-014 作为冒烟/质量参与的 evidence task，未在每个 task.requirement_refs 中重复全部需求 id；结构覆盖仍为 54/54 covered with evidence。
artifacts:
  - aref:current-plan — outputs/plan.json
  - plan-task × 14 — outputs/tasks/TASK-001..014.json
  - execution-waves — outputs/waves.json
  - dependency-graph — outputs/dependency-graph.json
  - collision-report — outputs/collision-report.json
  - plan-check — outputs/plan-check.json
next:
  - { command: execute, reason: plan ready, needs: [current-plan] }
---

## 摘要

Plan Run `20260727-002-plan`（Session `maestro-rust-migration-p2-desktop-20260727-021446`）按 2+1 planning 模式产出：
两个并行 planner（Foundation/Shell + UI/Acceptance）基于 P2 迁移指南 4.6/4.7/4.8 与 P0-P1 冻结契约给出 8+6 提案，
synthesis 统一为 14 个连续 TASK 并对齐到四个 goal-specific execute Run（G1->step-003, G2->step-004, G3->step-005, G4->step-006）。

任务分布：G1=4（crate/workspace/software-renderer、tray spike、service-endpoints、SSE/state+REST）、G2=4（win32 shell、placement、tray、双窗口/persistence）、G3=3（organic spike、主窗数据/Pinned/Toast、主窗交互与 Kill）、G4=3（任务栏 4 布局、设置/关于/更新检查、最终 smoke+Private Bytes+4K+workspace test/clippy）。

## 结论/Verdict

ready / PASS。直接结构校验确认：所有 formal JSON 含完整 `_meta`；plan.task_ids 与 TASK 文件一一对应；waves/DAG/collision 互洽；
依赖 28 条边全部存在且无环，topological_order = TASK-001..TASK-014；同一 wave 内零写冲突；
14 个不可避免共享文件按依赖顺序串行解决并报告；无 `xhmonitor-web/ XhMonitor.Desktop/ xhm-service/ xhm-core/` 写入（workspace 根依赖消费例外）；
54/54 cataloged requirements 全部 covered with evidence；G3/G4 delivery 任务均含 `[UI-observable]` 收敛项；
早期 tray+Slint（TASK-002）与有机动画（TASK-009）go/no-go 门到位；不迁移全局热键、不切 P3、不下载/启动安装器。

## 讨论/复盘

- 与 prepare 骨架的唯一偏差是 `verify` 用对象而非数组；语义更完整（commands + manual_scenarios + expected），checker 标为 warning 不阻塞。
- coverage_map 设计为 evidence 责任矩阵（如 TASK-014 冒烟覆盖多需求），未把每个需求 id 复制进所有相关 task 的 `requirement_refs`；结构覆盖仍 54/54。
- 两个早期 spike（tray、organic）作为 milestone 内 go/no-go 门，no-go 时阻塞下游并要求独立决策，不在 plan 内自行改 renderer 或回退到 Shell_NotifyIcon。
- 总估算 765 分钟、多 task 触 60 分钟上限；milestone 级别可接受，execute 阶段需监控 slippage。

## 产物

正式 artifact 19 个（路径相对 Run 目录）：

- `outputs/plan.json`（current-plan）
- `outputs/tasks/TASK-001.json` … `outputs/tasks/TASK-014.json`
- `outputs/waves.json`
- `outputs/dependency-graph.json`
- `outputs/collision-report.json`
- `outputs/plan-check.json`

详细 JSON 为唯一事实源，参见 aref:current-plan。

## 交接/Next

Plan 已就绪：运行 `maestro session next --session maestro-rust-migration-p2-desktop-20260727-021446` 进入 G1 execute（step-003-execute），随后按 G2->G3->G4 顺序执行；plan task/wave 已对齐四个 goal-specific execute Run。执行期间处理 plan-check 记录的三项 warning（verify schema、估算上限、coverage evidence 矩阵）；不得回归冻结契约、不得越界 P3 或修改 web/C#/xhm-service/xhm-core。
