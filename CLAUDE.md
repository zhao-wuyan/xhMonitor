# xhMonitor — 解决方案根目录

## 概述

xhMonitor（星核监视器）是一套高性能 Windows 进程资源监控系统，支持 CPU、内存、GPU、显存、功耗、网络等指标的实时采集、聚合分析与可视化展示。

- 解决方案文件：`xhMonitor.sln`
- 目标平台：Windows 10/11 (1709+)
- 运行时：.NET 8 + Node.js 18+

## 项目结构

```
xhMonitor/
├── XhMonitor.Core/          # 共享核心库（实体、接口、监控逻辑）
├── XhMonitor.Service/       # ASP.NET Core 后端服务（端口 35179）
├── XhMonitor.Desktop/       # WPF 桌面悬浮窗应用
├── XhMonitor.Tests/         # 集成测试
├── XhMonitor.Desktop.Tests/ # 桌面应用单元测试
├── xhmonitor-web/           # React/TypeScript 前端（端口 35180）
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
| `XhMonitor.Core` | .NET 类库 | 共享实体、接口（`IMetricProvider`）、数据模型、枚举、互操作层 |
| `XhMonitor.Service` | ASP.NET Core 服务 | REST API + SignalR Hub，采集并持久化指标，提供聚合数据；端口 35179 |
| `XhMonitor.Desktop` | WPF 应用（net8.0-windows） | 桌面悬浮监控窗口，嵌入前端 Web 视图，支持任务栏驻留与透明度设置 |
| `xhmonitor-web` | React + TypeScript | 实时可视化界面，通过 SignalR 接收推送；端口 35180；详见 `xhmonitor-web/CLAUDE.md` |
| `XhMonitor.Tests` | xUnit 集成测试 | 覆盖后端服务与核心逻辑 |
| `XhMonitor.Desktop.Tests` | xUnit 单元测试 | 覆盖桌面应用 ViewModel 与服务层 |

## 端口与通信

| 服务 | 端口 | 协议 |
|------|------|------|
| 后端 API / SignalR Hub | 35179 | HTTP / WebSocket |
| 前端 Web 界面 | 35180 | HTTP |
| SignalR Hub 路径 | `/hubs/metrics` | WebSocket |

## 技术栈

**后端（.NET 8）**
- ASP.NET Core + SignalR
- SQLite（EF Core，`xhmonitor.db`）
- Serilog（结构化日志）
- LibreHardwareMonitor / PerformanceCounter（系统指标）
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

- 后端配置：`XhMonitor.Service/appsettings.json`
  - `Monitor.Keywords`：进程过滤关键词列表
  - `Monitor.ProcessNameRules`：进程名称规则（正则/直接映射）
  - `MetricProviders.PreferLibreHardwareMonitor`：是否优先使用 LHM（需管理员权限）
  - `Power.RyzenAdjPath`：RyzenAdj 可执行文件路径
  - `Database.RetentionDays`：数据保留天数（默认 30）
- 桌面配置：`XhMonitor.Desktop/appsettings.json`
- 前端配置：`xhmonitor-web/vite.config.ts`（端口 35180）

## 插件化架构

核心通过 `IMetricProvider` 接口支持自定义指标扩展，插件目录由 `MetricProviders.PluginDirectory` 配置。

## 开发与构建

```bash
# 后端服务
cd XhMonitor.Service
dotnet run

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
