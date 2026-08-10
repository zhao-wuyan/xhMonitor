# xhMonitor — 解决方案根目录

## 概述

xhMonitor（星核监视器）是一套高性能 Windows 进程资源监控系统，支持 CPU、内存、GPU、显存、功耗、网络等指标的实时采集、聚合分析与可视化展示。

- 解决方案文件：`xhMonitor.sln`
- 目标平台：Windows 10/11 (1709+)
- 运行时：Rust 1.82、.NET 8（WPF Desktop 与 lhm-bridge）、Node.js 18+

## 项目结构

```
xhMonitor/
├── xhm-core/               # Rust 共享模型、trait 与 wire contract
├── xhm-service/            # Rust Axum 后端服务（端口 35179）
├── lhm-bridge/             # LibreHardwareMonitor .NET 传感器桥
├── XhMonitor.Core/         # C# Desktop 仍使用的共享核心库
├── XhMonitor.Desktop/      # WPF 桌面悬浮窗应用
├── XhMonitor.Desktop.Tests/# 桌面应用单元测试
├── xhmonitor-web/          # React/TypeScript 前端（端口 35180）
├── .claude/CLAUDE.md        # 项目级 Claude 指令（勿修改）
├── .claude/rules/           # 活跃记忆规则
├── xhMonitor.sln            # Visual Studio 解决方案
├── Directory.Build.props    # 跨项目通用构建属性
└── publish.ps1              # 构建/发布脚本
└── build-installer.ps1      # 构建/发布安装包脚本
└── scripts/                 # 启动/停止脚本
```

## 子模块职责

| 模块 | 类型 | 职责 |
|------|------|------|
| `xhm-core` | Rust 类库 | 共享模型、trait、错误与 REST/SignalR wire contract |
| `xhm-service` | Rust Axum 服务 | REST API + SignalR 兼容 Hub，采集、聚合并持久化指标；端口 35179 |
| `lhm-bridge` | .NET 8 子进程 | 通过 LibreHardwareMonitor 向 Rust Service 提供硬件传感器快照 |
| `XhMonitor.Core` | .NET 类库 | C# Desktop 仍使用的共享配置与模型 |
| `XhMonitor.Desktop` | WPF 应用（net8.0-windows） | 启动 Rust Service，提供桌面悬浮窗口和内嵌 Web 界面 |
| `xhmonitor-web` | React + TypeScript | 实时可视化界面，通过 SignalR 接收推送；端口 35180 |
| `XhMonitor.Desktop.Tests` | xUnit 单元测试 | 覆盖桌面应用 ViewModel 与服务层 |

## 端口与通信

| 服务 | 端口 | 协议 |
|------|------|------|
| 后端 API / SignalR Hub | 35179 | HTTP / WebSocket |
| 前端 Web 界面 | 35180 | HTTP |
| SignalR Hub 路径 | `/hubs/metrics` | WebSocket |

## 技术栈

**后端（Rust）**
- Axum + Tokio
- SQLite（rusqlite，`xhmonitor.db`）
- tracing（结构化日志）
- lhm-bridge / sysinfo（系统指标）
- RyzenAdj（AMD 平台功耗采集与调节）

**前端（xhmonitor-web）**
- React 19 + TypeScript 5.9
- Vite 7 + Tailwind CSS 4
- ECharts / echarts-for-react
- @microsoft/signalr

**桌面（XhMonitor.Desktop）**
- WPF（net8.0-windows）
- SignalR Client
- System.Text.Json

## 关键配置

- 后端配置：`xhm-service/appsettings.json`
  - `Monitor.Keywords`：进程过滤关键词列表
  - `Monitor.ProcessNameRules`：进程名称规则（正则/直接映射）
  - `Power.RyzenAdjPath`：RyzenAdj 可执行文件路径
  - `Database.RetentionDays`：数据保留天数（默认 30）
- 桌面配置：`XhMonitor.Desktop/appsettings.json`
- 前端配置：`xhmonitor-web/vite.config.ts`（端口 35180）

## 插件化架构

Rust 核心通过 `MetricStore`、`LhmReader`、`RyzenAdjClient` 等 trait 隔离存储与硬件边界。

## 开发与构建

```bash
# 后端服务
cargo run -p xhm-service

# 前端
cd xhmonitor-web
npm run dev

# 完整发布（PowerShell）
./publish.ps1

# 一键启动所有服务
./start-all.ps1
```

## 子模块 CLAUDE.md 引用

- `xhmonitor-web/CLAUDE.md` — 前端项目详细规范
- `.claude/CLAUDE.md` — 项目级 Claude 指令（上下文检索规则，勿覆盖）
- `.claude/rules/active_memory.md` — 活跃记忆（自动生成）

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **xhMonitor** (7159 symbols, 16120 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/xhMonitor/context` | Codebase overview, check index freshness |
| `gitnexus://repo/xhMonitor/clusters` | All functional areas |
| `gitnexus://repo/xhMonitor/processes` | All execution flows |
| `gitnexus://repo/xhMonitor/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->
