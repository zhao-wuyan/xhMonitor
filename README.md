# XhMonitor - Windows 资源监视器

> 高性能的 Windows 进程资源监控系统，支持 CPU、内存、GPU、显存、功耗、网络等指标的实时采集、聚合分析和可视化展示

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![React](https://img.shields.io/badge/React-19-61DAFB)](https://react.dev/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

## Features

- ✅ **多维度监控** - CPU、内存、GPU、显存、硬盘、功耗、网络速度实时监控
- ✅ **智能过滤** - 基于关键词过滤，精准监控目标进程
- ✅ **分层聚合** - 自动生成分钟/小时/天级别统计数据
- ✅ **实时推送** - SignalR 实时推送最新指标，延迟 < 100ms
- ✅ **Web 可视化** - React + TailwindCSS 现代化界面，ECharts 动态图表
- ✅ **桌面悬浮窗** - WPF 桌面应用，支持进程固定、拖拽、置顶
- ✅ **插件化架构** - IMetricProvider 接口支持自定义指标扩展
- ✅ **配置驱动** - 零前端代码修改，动态扩展指标
- ✅ **国际化支持** - 中英文切换，易于扩展多语言
- ✅ **功耗管理** - RyzenAdj 集成，支持 AMD 平台功耗监控与调节
- ✅ **设备验证** - 设备白名单机制，保护功耗调节功能
- ✅ **安全认证** - 访问密钥认证、IP 白名单、局域网访问控制

## Installation

### Prerequisites

**后端**：
- Windows 10/11 (1709+)
- .NET 8 SDK
- Visual Studio 2022 或 VS Code

**前端**：
- Node.js 18+
- npm 或 pnpm

**权限要求**：
- **推荐**：管理员权限（可监控功耗模式和切换功耗，AI MAX 395适配）
- **最低**：普通用户权限（无法进行功耗监控和切换）

### Install

**1. 克隆仓库**

```bash
git clone <repository-url>
cd xhMonitor
```

**2. 后端服务**

```bash
# 还原依赖
dotnet restore

# 应用数据库迁移
cd XhMonitor.Service
dotnet ef database update

# 启动后端服务
dotnet run --project XhMonitor.Service
```

服务将在 `http://localhost:35179` 启动。

**3. 前端界面**

```bash
# 进入前端目录
cd xhmonitor-web

# 安装依赖
npm install

# 启动开发服务器
npm run dev
```

前端将在 `http://localhost:35180` 启动。

**4. 桌面应用**

```bash
# 启动桌面应用
dotnet run --project XhMonitor.Desktop
```

或使用启动脚本：

```bash
# Windows
.\start.bat
```

## Usage

### Quick Start

**1. 配置监控关键词**

编辑 `XhMonitor.Service/appsettings.json`：

```json
{
  "Monitor": {
    "IntervalSeconds": 3,
    "Keywords": ["python", "node", "docker", "chrome"]
  }
}
```

**2. 启动服务**

```bash
dotnet run --project XhMonitor.Service
```

**3. 访问 Web 界面**

打开浏览器访问 `http://localhost:35180`，即可查看实时监控数据。

**4. 使用桌面悬浮窗**

运行 `XhMonitor.Desktop` 或执行 `start.bat`，桌面将显示悬浮窗，支持：
- 进程固定（Pin）
- 拖拽移动
- 窗口置顶
- 功耗调节（需管理员权限 + AMD 平台）

### Examples

**REST API 查询**

```bash
# 获取最新指标
curl http://localhost:35179/api/v1/metrics/latest

# 获取历史数据（分钟聚合）
curl "http://localhost:35179/api/v1/metrics/history?processId=1234&aggregation=minute"

# 获取进程列表
curl http://localhost:35179/api/v1/metrics/processes
```

**SignalR 实时订阅**

```typescript
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("http://localhost:35179/hubs/metrics")
  .withAutomaticReconnect()
  .build();

connection.on("metrics.latest", (data) => {
  console.log(`Received ${data.processCount} processes`);
  data.processes.forEach(p => {
    console.log(`${p.processName}: CPU=${p.metrics.cpu.value}%`);
  });
});

await connection.start();
```

## Configuration

### 关键配置（建议优先关注）

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `Monitor:IntervalSeconds` | `3` | Service 进程采集间隔（秒） |
| `Monitor:Keywords` | 示例见 `appsettings.json` | 目标进程过滤关键词 |
| `Server:Port` | `35179` | Service HTTP/SignalR 服务端口 |
| `SignalR:*BufferSize` | `1048576` | SignalR 缓冲上限，影响峰值内存 |
| `Aggregation:BatchSize` | `2000` | 聚合任务分批读取大小，影响聚合阶段峰值内存 |
| `UiOptimization:ProcessRefreshIntervalMs` | `Development=100` `Staging=150` `Production=200` | Desktop 刷新节流间隔 |

完整配置说明（含全部字段）请看：`docs/appsettings-reference.md`  
配置边界说明请看：[Configuration Boundaries](XhMonitor.Service/docs/configuration-boundaries.md)

## API Reference

### REST API

**Base URL**: `http://localhost:35179/api/v1`

#### Metrics API

**获取最新指标**

```http
GET /metrics/latest?processId={int}&processName={string}&keyword={string}
```

**获取历史数据**

```http
GET /metrics/history?processId={int}&from={datetime}&to={datetime}&aggregation={string}
```

参数：
- `aggregation`: `raw` | `minute` | `hour` | `day`

**获取进程列表**

```http
GET /metrics/processes?from={datetime}&to={datetime}&keyword={string}
```

#### Config API

**获取指标元数据**

```http
GET /config/metrics
```

返回所有已注册的指标提供者信息，用于前端动态渲染。

**获取配置**

```http
GET /config
```

**健康检查**

```http
GET /config/health
```

### SignalR Hub

**Hub URL**: `http://localhost:35179/hubs/metrics`

**事件**：
- `metrics.latest` - 根据 `Monitor:IntervalSeconds` 配置的间隔推送最新指标数据（默认 1 秒）

## Architecture

### 系统架构

XhMonitor 采用分层架构设计：

```
┌─────────────────────────────────────────────────────────────┐
│  采集层 (Collection Layer)                                   │
│  ├─ PerformanceMonitor (协调器)                              │
│  ├─ ProcessScanner (进程扫描)                                │
│  └─ MetricProviders (指标采集器)                             │
│     ├─ CpuMetricProvider                                     │
│     ├─ MemoryMetricProvider                                  │
│     ├─ GpuMetricProvider                                     │
│     ├─ VramMetricProvider                                    │
│     ├─ DiskMetricProvider                                    │
│     ├─ PowerMetricProvider (RyzenAdj)                        │
│     └─ NetworkMetricProvider                                 │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│  存储层 (Storage Layer)                                      │
│  ├─ SQLite Database (EF Core 8)                             │
│  ├─ ProcessMetricRecords (原始数据)                          │
│  └─ AggregatedMetricRecords (分层聚合)                       │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│  服务层 (Service Layer)                                      │
│  ├─ REST API (MetricsController, ConfigController)          │
│  └─ SignalR Hub (实时推送)                                   │
└─────────────────────────────────────────────────────────────┘
                          ↓
┌─────────────────────────────────────────────────────────────┐
│  展示层 (Presentation Layer)                                 │
│  ├─ Web 前端 (React 19 + TypeScript)                        │
│  └─ 桌面应用 (WPF + MVVM)                                    │
└─────────────────────────────────────────────────────────────┘
```

### 技术栈

| 类别 | 技术 |
|------|------|
| 后端框架 | .NET 8 + ASP.NET Core |
| 前端框架 | React 19 + TypeScript + Vite 7 |
| 桌面应用 | WPF + MVVM |
| 数据库 | SQLite + EF Core 8 |
| 实时通信 | SignalR |
| 可视化 | ECharts 6 |
| 样式 | TailwindCSS v4 (Glassmorphism) |
| 性能监控 | LibreHardwareMonitor + PerformanceCounter API |
| 功耗管理 | RyzenAdj |
| 日志 | Serilog |

### 项目结构

```
xhMonitor/
├── XhMonitor.Core/              # 核心库
│   ├── Entities/                # EF Core 实体
│   ├── Enums/                   # 枚举定义
│   ├── Interfaces/              # 接口定义
│   ├── Models/                  # 数据模型
│   └── Providers/               # 内置指标提供者
├── XhMonitor.Service/           # 后端服务
│   ├── Controllers/             # API 控制器
│   ├── Core/                    # 核心业务逻辑
│   ├── Data/                    # 数据访问层
│   ├── Hubs/                    # SignalR Hub
│   ├── Workers/                 # 后台任务
│   └── appsettings.json         # 配置文件
├── XhMonitor.Desktop/           # WPF 桌面应用
│   ├── ViewModels/              # MVVM ViewModels
│   ├── Views/                   # XAML 视图
│   └── Services/                # 服务层
├── xhmonitor-web/               # React 前端
│   ├── src/
│   │   ├── components/          # React 组件
│   │   ├── hooks/               # 自定义 Hooks
│   │   └── i18n.ts              # 国际化配置
│   └── vite.config.ts           # Vite 配置
└── tools/                       # 工具集
    └── RyzenAdj/                # RyzenAdj 功耗管理工具
```

## Development

### 添加自定义指标

**1. 实现 IMetricProvider 接口**

```csharp
public class CustomMetricProvider : IMetricProvider
{
    public string MetricId => "custom_metric";
    public string DisplayName => "Custom Metric";
    public string Unit => "units";
    public MetricType Type => MetricType.Gauge;

    public bool IsSupported() => true;

    public async Task<MetricValue> CollectAsync(int processId)
    {
        var value = await GetCustomMetricAsync(processId);
        return new MetricValue
        {
            Value = value,
            Unit = Unit,
            DisplayName = DisplayName,
            Timestamp = DateTime.UtcNow
        };
    }

    public void Dispose() { }
}
```

**2. 注册到 MetricProviderRegistry**

提供者会自动被发现并注册。

**3. 前端国际化**

在 `xhmonitor-web/src/i18n.ts` 中添加翻译：

```typescript
export const i18n = {
  zh: {
    'Custom Metric': '自定义指标',
  },
  en: {
    'Custom Metric': 'Custom Metric',
  },
};
```

前端会自动通过 `/api/v1/config/metrics` 获取指标元数据并渲染，无需修改组件代码。

### 运行测试

```bash
# 单元测试
dotnet test

# 集成测试
dotnet test --filter Category=Integration
```

### 构建发布

```bash
# 发布为单文件可执行程序
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# 输出目录
# XhMonitor.Service/bin/Release/net8.0/win-x64/publish/
```

详细发布指南参考：[PUBLISH_GUIDE.md](PUBLISH_GUIDE.md)

## Performance

**当前测试环境**：
- 监控进程数：141
- 采集间隔：3 秒
- 首次周期：102 秒（含缓存构建）
- 后续周期：8-9 秒
- CPU 占用：< 5%
- 内存占用：~50MB

**优化建议**：
- 使用进程关键词过滤减少监控数量
- 调整采集间隔（3-10 秒）
- 定期清理历史数据

## Roadmap

### 已完成

- ✅ 核心架构搭建
- ✅ 监控核心实现
- ✅ 数据持久化与聚合
- ✅ Web API + SignalR
- ✅ Web 前端开发
- ✅ WPF 桌面悬浮窗
- ✅ 功耗监控（RyzenAdj）
- ✅ 网络监控
- ✅ 进程管理（强制结束）

### 进行中

### 待开发

- ⏳ 进程详情查看

## Contributing

欢迎提交 Issue 和 Pull Request！

### 开发流程

1. Fork 本仓库
2. 创建特性分支 (`git checkout -b feature/AmazingFeature`)
3. 提交更改 (`git commit -m 'feat: Add some AmazingFeature'`)
4. 推送到分支 (`git push origin feature/AmazingFeature`)
5. 开启 Pull Request

### 代码规范

- 遵循 C# Coding Conventions
- 使用有意义的变量和方法名
- 添加必要的注释（非必要不添加）
- 保持代码简洁高效

## License

[MIT License](LICENSE)

## Changelog

详见 [CHANGELOG.md](CHANGELOG.md)

### 最新版本 v0.2.6 (2026-02-05)

- ✨ 新增硬盘指标监控（读写速度、使用率）
- ✨ 新增访问密钥认证功能
- ✨ 新增局域网访问控制和 IP 白名单
- ✨ 新增 API 端点集中化配置管理
- ✨ 完善关于页面技术栈说明
- ✨ Web 体验优化（指标顺序调整、标签图标和描述）
- ✨ 设置布局优化和面板透明度调整
- 🐛 修复设置页面相关问题

### v0.2.0 (2026-01-27)

- ✨ 新增进程排序优化
- ✨ 新增单实例模式与设备验证
- ✨ 新增点击动画视觉反馈
- ✨ 新增管理员状态指示器
- ✨ 设置页改版（监控开关、开机自启、管理员模式）
- ✨ 新增功耗监控（RyzenAdj 集成）
- ✨ 新增网络监控
- 🐛 修复悬浮窗置顶卡片宽度问题
- 🐛 修复 Web 端显存和内存占用显示问题

## Contact

- 项目地址：<repository-url>
- Issue 追踪：<repository-url>/issues

---

